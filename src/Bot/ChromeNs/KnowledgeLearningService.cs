using Bot.Knowledge;
using BotLib;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal sealed class KnowledgeLearningResult
    {
        public bool Success { get; set; }
        public bool Added { get; set; }
        public bool Updated { get; set; }
        public string Message { get; set; }
    }

    internal static class KnowledgeLearningService
    {
        private sealed class SourceStamp
        {
            public string Source;
            public DateTime ExpiresAt;
        }

        private sealed class BlockStamp
        {
            public string Reason;
            public string ManualAnswer;
            public DateTime ExpiresAt;
        }

        private static readonly object SaveLock = new object();
        private static readonly ConcurrentDictionary<string, SourceStamp> Sources =
            new ConcurrentDictionary<string, SourceStamp>();
        private static readonly ConcurrentDictionary<string, BlockStamp> Blocks =
            new ConcurrentDictionary<string, BlockStamp>();
        private static readonly ConcurrentDictionary<string, DateTime> ManualBypass =
            new ConcurrentDictionary<string, DateTime>();

        public static event EventHandler KnowledgeBaseChanged;

        public static void RegisterAnswerSource(
            string seller,
            string buyer,
            string question,
            string answer,
            string source)
        {
            if (string.IsNullOrWhiteSpace(answer) || string.IsNullOrWhiteSpace(source)) return;
            var stamp = new SourceStamp
            {
                Source = source,
                ExpiresAt = DateTime.Now.AddMinutes(30)
            };
            Sources[AnswerKey(seller, buyer, question, answer)] = stamp;
            Sources[QuestionSourceKey(seller, buyer, question)] = stamp;
        }

        public static string ResolveAnswerSource(
            string seller,
            string buyer,
            string question,
            string answer)
        {
            Cleanup();
            SourceStamp stamp;
            if (Sources.TryGetValue(AnswerKey(seller, buyer, question, answer), out stamp)
                && stamp.ExpiresAt >= DateTime.Now)
            {
                return stamp.Source;
            }
            if (Sources.TryGetValue(QuestionSourceKey(seller, buyer, question), out stamp)
                && stamp.ExpiresAt >= DateTime.Now)
            {
                return stamp.Source;
            }
            return string.Empty;
        }

        public static bool TryFindLocalAnswer(
            string seller,
            string buyer,
            string question,
            out KnowledgeBaseEntry matched,
            out double score)
        {
            matched = null;
            score = 0;
            var policy = BotFeatureStore.GetMessagePolicy();
            if (policy == null || !policy.EnableKnowledgeBase || string.IsNullOrWhiteSpace(question))
            {
                return false;
            }

            ConversationContextTurn latestAgentPrompt = null;
            if (IsShortContextReply(question))
            {
                var turns = ConversationContextStore.GetRecentTurns(seller, buyer, question, 8);
                latestAgentPrompt = turns
                    .LastOrDefault(x => x.Role == "assistant" && !string.IsNullOrWhiteSpace(x.Text));
            }

            foreach (var item in BotFeatureStore.GetKnowledgeBase()
                .Where(x => x != null && x.Enabled && !string.IsNullOrWhiteSpace(x.Answer)))
            {
                var currentScore = Score(item, question, false);
                if (latestAgentPrompt != null)
                {
                    currentScore = Math.Max(currentScore, Score(item, latestAgentPrompt.Text, true));
                }
                if (currentScore > score)
                {
                    score = currentScore;
                    matched = item;
                }
            }
            return matched != null && score >= 0.84;
        }

        private static bool IsShortContextReply(string value)
        {
            var compact = Normalize(value);
            if (compact.Length == 0 || compact.Length > 32) return false;
            if (compact.IndexOf('?') >= 0 || compact.IndexOf('？') >= 0) return false;
            if (Regex.IsMatch(compact, @"^[a-z0-9@._+\-:/]+$", RegexOptions.IgnoreCase)) return true;
            if (Regex.IsMatch(compact, @"^\d+$")) return true;
            return compact.Length <= 8;
        }

        private static double Score(KnowledgeBaseEntry item, string query, bool contextOnly)
        {
            var q = KnowledgeAiService.NormalizeQuestion(query);
            var title = KnowledgeAiService.NormalizeQuestion(item.Title);
            if (string.IsNullOrWhiteSpace(q) || string.IsNullOrWhiteSpace(title)) return 0;
            if (q == title) return contextOnly ? 0.91 : 1.0;
            if (Math.Min(q.Length, title.Length) >= 4
                && (q.Contains(title) || title.Contains(q)))
            {
                return contextOnly ? 0.87 : 0.95;
            }
            foreach (var keyword in SplitKeywords(item.Keywords))
            {
                var normalizedKeyword = KnowledgeAiService.NormalizeQuestion(keyword);
                if (normalizedKeyword.Length >= 2 && q.Contains(normalizedKeyword))
                {
                    return contextOnly ? 0.85 : 0.90;
                }
            }
            var similarity = BigramSimilarity(q, title);
            if (similarity >= 0.68) return contextOnly ? 0.84 : 0.86;
            return similarity * 0.75;
        }

        private static IEnumerable<string> SplitKeywords(string value)
        {
            return (value ?? string.Empty)
                .Split(new[] { ',', '，', ';', '；', '|', ' ', '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim());
        }

        private static double BigramSimilarity(string a, string b)
        {
            var aa = Bigrams(a);
            var bb = Bigrams(b);
            if (aa.Count == 0 || bb.Count == 0) return 0;
            var common = aa.Intersect(bb).Count();
            return (2.0 * common) / (aa.Count + bb.Count);
        }

        private static HashSet<string> Bigrams(string value)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i + 1 < (value ?? string.Empty).Length; i++)
            {
                set.Add(value.Substring(i, 2));
            }
            return set;
        }

        public static void AllowNextManualSend(string seller, string buyer, string answer)
        {
            ManualBypass[SendKey(seller, buyer, answer)] = DateTime.Now.AddSeconds(15);
        }

        public static bool TryBlockForManualReply(
            QN qn,
            string buyer,
            string candidateAnswer,
            out string question,
            out string manualAnswer)
        {
            question = string.Empty;
            manualAnswer = string.Empty;
            if (qn == null || qn.Seller == null) return false;

            var seller = qn.Seller.Nick ?? string.Empty;
            DateTime bypassUntil;
            var sendKey = SendKey(seller, buyer, candidateAnswer);
            if (ManualBypass.TryRemove(sendKey, out bypassUntil)
                && bypassUntil >= DateTime.Now)
            {
                return false;
            }

            DateTime questionTime;
            if (!ConversationContextStore.TryGetLatestBuyerQuestion(
                seller,
                buyer,
                out question,
                out questionTime))
            {
                return false;
            }

            try
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var type = typeof(QN);
                var buyerField = type.GetField("_lastSellerEchoBuyer", flags);
                var textField = type.GetField("_lastSellerEchoText", flags);
                var timeField = type.GetField("_lastSellerEchoTime", flags);
                if (buyerField == null || textField == null || timeField == null) return false;

                var echoBuyer = Convert.ToString(buyerField.GetValue(qn));
                var echoText = Convert.ToString(textField.GetValue(qn));
                var echoTime = (DateTime)timeField.GetValue(qn);
                if (!string.Equals(
                    (echoBuyer ?? string.Empty).Trim(),
                    (buyer ?? string.Empty).Trim(),
                    StringComparison.Ordinal))
                {
                    return false;
                }
                if (echoTime < questionTime.AddMilliseconds(-500)
                    || echoTime < DateTime.Now.AddMinutes(-20))
                {
                    return false;
                }
                if (string.IsNullOrWhiteSpace(echoText)
                    || Normalize(echoText) == Normalize(candidateAnswer))
                {
                    return false;
                }

                manualAnswer = echoText.Trim();
                Blocks[sendKey] = new BlockStamp
                {
                    Reason = "已取消：客服已人工回复，本次 Bot 答案未发送",
                    ManualAnswer = manualAnswer,
                    ExpiresAt = DateTime.Now.AddMinutes(5)
                };
                RegisterAnswerSource(seller, buyer, question, manualAnswer, "人工回复");
                QueueLearn(question, manualAnswer, "人工回复", seller, buyer);
                Log.Info("自动发送已取消：检测到客服人工回复。seller="
                    + seller + ", buyer=" + buyer);
                return true;
            }
            catch (Exception ex)
            {
                Log.Info("检测客服人工回复失败，继续原发送流程：" + ex.Message);
                return false;
            }
        }

        public static bool TryTakeSendBlock(
            string seller,
            string buyer,
            string answer,
            out string reason,
            out string manualAnswer)
        {
            reason = string.Empty;
            manualAnswer = string.Empty;
            BlockStamp stamp;
            if (!Blocks.TryRemove(SendKey(seller, buyer, answer), out stamp)
                || stamp.ExpiresAt < DateTime.Now)
            {
                return false;
            }
            reason = stamp.Reason;
            manualAnswer = stamp.ManualAnswer;
            return true;
        }

        public static void QueueLearn(
            string question,
            string answer,
            string sourceType,
            string seller,
            string buyer)
        {
            if (!CanLearn(question, answer)) return;
            Task.Run(async () =>
            {
                try
                {
                    await LearnAsync(question, answer, sourceType, seller, buyer);
                }
                catch (Exception ex)
                {
                    Log.Info("知识自动学习失败：" + ex.Message);
                }
            });
        }

        public static async Task<KnowledgeLearningResult> LearnAsync(
            string question,
            string answer,
            string sourceType,
            string seller,
            string buyer)
        {
            if (!CanLearn(question, answer))
            {
                return new KnowledgeLearningResult
                {
                    Success = false,
                    Message = "问题或答案为空，未写入知识库"
                };
            }

            var context = ConversationContextStore.BuildTimelineText(seller, buyer, question, 10);
            var safeQuestion = RedactSensitive(question);
            var safeAnswer = RedactSensitive(answer);
            var messages = new JArray
            {
                new JObject
                {
                    ["role"] = "system",
                    ["content"] = "你是电商客服知识库整理器。只输出一个JSON对象：{\"question\":\"通用化问题\",\"answer\":\"可复用答案\",\"category\":\"分类\",\"keywords\":[\"关键词\"]}。不得保留真实手机号、验证码、订单号、身份证、银行卡、买家账号等个人数据，必须改写成通用占位表达；不要编造事实。"
                },
                new JObject
                {
                    ["role"] = "user",
                    ["content"] = "来源：" + sourceType
                        + "\n原始问题：" + safeQuestion
                        + "\n原始答案：" + safeAnswer
                        + (string.IsNullOrWhiteSpace(context)
                            ? string.Empty
                            : "\n同一买家最近时间线：\n" + RedactSensitive(context))
                }
            };

            var learnedQuestion = safeQuestion;
            var learnedAnswer = safeAnswer;
            var category = "自动学习";
            var keywords = string.Empty;
            try
            {
                var result = await Task.Run(() => MyOpenAI.CallStructuredChat(
                    messages,
                    500,
                    0.05,
                    90,
                    CancellationToken.None));
                if (result.Success)
                {
                    var parsed = ParseObject(result.Answer);
                    learnedQuestion = RedactSensitive(Convert.ToString(parsed["question"])).Trim();
                    learnedAnswer = RedactSensitive(Convert.ToString(parsed["answer"])).Trim();
                    category = Convert.ToString(parsed["category"]).Trim();
                    var arr = parsed["keywords"] as JArray;
                    keywords = arr == null
                        ? Convert.ToString(parsed["keywords"])
                        : string.Join(",", arr.Select(x => x.ToString().Trim())
                            .Where(x => x.Length > 0));
                }
            }
            catch (Exception ex)
            {
                Log.Info("AI整理知识失败，使用安全兜底内容：" + ex.Message);
            }

            if (string.IsNullOrWhiteSpace(learnedQuestion)) learnedQuestion = safeQuestion;
            if (string.IsNullOrWhiteSpace(learnedAnswer)) learnedAnswer = safeAnswer;
            if (string.IsNullOrWhiteSpace(category)) category = "自动学习";
            return SaveLearned(
                learnedQuestion,
                learnedAnswer,
                category,
                keywords,
                sourceType);
        }

        private static KnowledgeLearningResult SaveLearned(
            string question,
            string answer,
            string category,
            string keywords,
            string sourceType)
        {
            lock (SaveLock)
            {
                var list = BotFeatureStore.GetKnowledgeBase();
                var qKey = KnowledgeAiService.NormalizeQuestion(question);
                var manualPreferred = (sourceType ?? string.Empty)
                    .StartsWith("人工", StringComparison.Ordinal);
                var existing = list.FirstOrDefault(
                    x => KnowledgeAiService.NormalizeQuestion(x.Title) == qKey);
                var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                if (existing != null)
                {
                    if (manualPreferred
                        && !string.Equals(
                            (existing.Answer ?? string.Empty).Trim(),
                            answer.Trim(),
                            StringComparison.Ordinal))
                    {
                        existing.Answer = answer.Trim();
                        existing.Category = category;
                        existing.Keywords = keywords;
                        existing.UpdatedAt = now;
                        existing.SourceType = sourceType;
                        existing.AiGenerated = false;
                        BotFeatureStore.SaveKnowledgeBase(list);
                        RaiseKnowledgeChanged();
                        return new KnowledgeLearningResult
                        {
                            Success = true,
                            Updated = true,
                            Message = "已用人工确认答案更新知识库"
                        };
                    }
                    return new KnowledgeLearningResult
                    {
                        Success = true,
                        Message = "知识库已存在相同问题，未重复添加"
                    };
                }

                var contentHash = KnowledgeAiService.ContentHash(question, answer);
                if (list.Any(x => KnowledgeAiService.ContentHash(x.Title, x.Answer) == contentHash))
                {
                    return new KnowledgeLearningResult
                    {
                        Success = true,
                        Message = "知识库已存在相同内容，未重复添加"
                    };
                }

                list.Add(new KnowledgeBaseEntry
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Enabled = true,
                    Category = category,
                    Title = question.Trim(),
                    Answer = answer.Trim(),
                    Keywords = keywords ?? string.Empty,
                    CreatedAt = now,
                    UpdatedAt = now,
                    AiGenerated = !manualPreferred,
                    SourceType = sourceType ?? "自动学习"
                });
                BotFeatureStore.SaveKnowledgeBase(list);
                RaiseKnowledgeChanged();
                return new KnowledgeLearningResult
                {
                    Success = true,
                    Added = true,
                    Message = "已整理并加入知识库"
                };
            }
        }

        private static JObject ParseObject(string text)
        {
            text = (text ?? string.Empty).Trim();
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start < 0 || end <= start) throw new Exception("未找到JSON对象");
            return JObject.Parse(text.Substring(start, end - start + 1));
        }

        private static bool CanLearn(string question, string answer)
        {
            return !string.IsNullOrWhiteSpace(question)
                && !string.IsNullOrWhiteSpace(answer)
                && !answer.StartsWith("错误：", StringComparison.Ordinal)
                && answer.IndexOf("已跳过", StringComparison.Ordinal) < 0;
        }

        private static string RedactSensitive(string value)
        {
            value = value ?? string.Empty;
            value = Regex.Replace(value, @"(?<!\d)1\d{10}(?!\d)", "[手机号]");
            value = Regex.Replace(value, @"(?<!\d)\d{15,19}(?!\d)", "[敏感编号]");
            value = Regex.Replace(value, @"(?i)sk-[a-z0-9_-]{12,}", "[API_KEY]");
            return value;
        }

        private static string Normalize(string value)
        {
            return Regex.Replace(
                (value ?? string.Empty).Trim().ToLowerInvariant(),
                @"\s+",
                string.Empty);
        }

        private static string AnswerKey(
            string seller,
            string buyer,
            string question,
            string answer)
        {
            return QuestionSourceKey(seller, buyer, question) + "|" + Normalize(answer);
        }

        private static string QuestionSourceKey(string seller, string buyer, string question)
        {
            return Normalize(seller) + "|" + Normalize(buyer) + "|"
                + KnowledgeAiService.NormalizeQuestion(question);
        }

        private static string SendKey(string seller, string buyer, string answer)
        {
            return Normalize(seller) + "|" + Normalize(buyer) + "|" + Normalize(answer);
        }

        private static void Cleanup()
        {
            var now = DateTime.Now;
            foreach (var key in Sources.Where(x => x.Value.ExpiresAt < now)
                .Select(x => x.Key).ToList())
            {
                SourceStamp ignored;
                Sources.TryRemove(key, out ignored);
            }
            foreach (var key in Blocks.Where(x => x.Value.ExpiresAt < now)
                .Select(x => x.Key).ToList())
            {
                BlockStamp ignored;
                Blocks.TryRemove(key, out ignored);
            }
            foreach (var key in ManualBypass.Where(x => x.Value < now)
                .Select(x => x.Key).ToList())
            {
                DateTime ignored;
                ManualBypass.TryRemove(key, out ignored);
            }
        }

        private static void RaiseKnowledgeChanged()
        {
            var handler = KnowledgeBaseChanged;
            if (handler != null) handler(null, EventArgs.Empty);
        }
    }
}
