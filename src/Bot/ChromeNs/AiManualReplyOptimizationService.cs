using Bot.Common;
using BotLib;
using BotLib.Db.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal sealed class AiOptimizationRecordEntity
    {
        [PrimaryKey]
        public string EntityId { get; set; }
        public string Seller { get; set; }
        public string Buyer { get; set; }
        public string Question { get; set; }
        public string AiAnswer { get; set; }
        public string HumanAnswer { get; set; }
        public double AccuracyScore { get; set; }
        public string AccuracyAnalysis { get; set; }
        public string HumanReplyReason { get; set; }
        public string KnowledgeStrategy { get; set; }
        public string SuggestionsJson { get; set; }
        public string Status { get; set; }
        public string Error { get; set; }
        public int AppliedCount { get; set; }
        public int SkippedCount { get; set; }
        public long QuestionDetectedAtTicks { get; set; }
        public long ManualReplyAtTicks { get; set; }
        public long CreatedAtTicks { get; set; }
        public long UpdatedAtTicks { get; set; }
    }

    internal sealed class AiOptimizationRecordView
    {
        public string Id { get; set; }
        public string Seller { get; set; }
        public string Buyer { get; set; }
        public string Question { get; set; }
        public string AiAnswer { get; set; }
        public string HumanAnswer { get; set; }
        public double AccuracyScore { get; set; }
        public string AccuracyAnalysis { get; set; }
        public string HumanReplyReason { get; set; }
        public string KnowledgeStrategy { get; set; }
        public string SuggestionsJson { get; set; }
        public string Status { get; set; }
        public string Error { get; set; }
        public int AppliedCount { get; set; }
        public int SkippedCount { get; set; }
        public DateTime QuestionDetectedAt { get; set; }
        public DateTime ManualReplyAt { get; set; }
        public DateTime CreatedAt { get; set; }

        public string CreatedAtText { get { return CreatedAt == DateTime.MinValue ? string.Empty : CreatedAt.ToString("MM-dd HH:mm:ss"); } }
        public string AccuracyText { get { return AccuracyScore <= 0 ? "-" : AccuracyScore.ToString("0") + "%"; } }
        public string ApplyText { get { return AppliedCount + "/" + (AppliedCount + SkippedCount); } }
    }

    /// <summary>
    /// When a human agent answers while an AI answer is still being generated, retain the final AI
    /// answer for comparison only. The comparison uses the recent conversation timeline plus both
    /// answers, and may update knowledge only when the human evidence is explicit and high-confidence.
    /// </summary>
    internal static class AiManualReplyOptimizationService
    {
        private sealed class ManualReplyEvidence
        {
            public string Reply;
            public DateTime At;
        }

        private static readonly object DbSync = new object();
        private static readonly ConcurrentDictionary<string, ManualReplyEvidence> RecentManualReplies =
            new ConcurrentDictionary<string, ManualReplyEvidence>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, DateTime> Inflight =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private static int _schemaReady;

        public const int RetentionDays = 180;
        public const int MaxRecords = 3000;
        public static event Action RecordsChanged;

        public static void ObserveManualReply(string seller, string buyer, string reply)
        {
            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();
            reply = StripAi((reply ?? string.Empty).Trim());
            if (seller.Length == 0 || buyer.Length == 0 || reply.Length == 0) return;
            RecentManualReplies[Key(seller, buyer)] = new ManualReplyEvidence
            {
                Reply = Clean(reply, 1800),
                At = DateTime.Now
            };
        }

        public static bool TryGetRecentManualReply(
            string seller,
            string buyer,
            DateTime answerRequestStartedAt,
            out string reply,
            out DateTime repliedAt)
        {
            reply = string.Empty;
            repliedAt = DateTime.MinValue;
            ManualReplyEvidence evidence;
            if (!RecentManualReplies.TryGetValue(Key(seller, buyer), out evidence) || evidence == null) return false;
            if (evidence.At < answerRequestStartedAt.AddSeconds(-2) || evidence.At < DateTime.Now.AddMinutes(-15)) return false;
            reply = evidence.Reply ?? string.Empty;
            repliedAt = evidence.At;
            return reply.Length > 0;
        }

        public static void QueueCompare(
            string seller,
            string buyer,
            string question,
            string aiAnswer,
            string humanAnswer,
            DateTime questionDetectedAt,
            DateTime manualReplyAt)
        {
            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();
            question = Clean(question, 1800);
            aiAnswer = StripAi(Clean(aiAnswer, 2200));
            humanAnswer = StripAi(Clean(humanAnswer, 2200));
            if (seller.Length == 0 || buyer.Length == 0 || question.Length == 0
                || aiAnswer.Length == 0 || humanAnswer.Length == 0) return;

            var signature = Key(seller, buyer) + "#" + Normalize(question) + "#" + Normalize(humanAnswer);
            DateTime until;
            if (Inflight.TryGetValue(signature, out until) && until > DateTime.Now) return;
            Inflight[signature] = DateTime.Now.AddMinutes(15);

            Task.Run(async () =>
            {
                try
                {
                    await CompareAndPersistAsync(
                        seller, buyer, question, aiAnswer, humanAnswer,
                        questionDetectedAt, manualReplyAt).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("AI人工回复即时对比失败: seller=" + seller
                        + ", buyer=" + buyer + ", error=" + ex.Message, 20);
                }
                finally
                {
                    DateTime ignored;
                    Inflight.TryRemove(signature, out ignored);
                }
            });
        }

        public static List<AiOptimizationRecordView> GetRecords(int maxCount)
        {
            EnsureSchema();
            var take = Math.Max(1, Math.Min(MaxRecords, maxCount <= 0 ? 300 : maxCount));
            try
            {
                lock (DbSync)
                {
                    return (DbHelper.Db.Select(
                        typeof(AiOptimizationRecordEntity),
                        "order by UpdatedAtTicks desc limit " + take) ?? new List<object>())
                        .OfType<AiOptimizationRecordEntity>()
                        .Where(x => x != null)
                        .Select(ToView)
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("读取AI优化记录失败：" + ex.Message, 10);
                return new List<AiOptimizationRecordView>();
            }
        }

        public static string FormatRecord(AiOptimizationRecordView record)
        {
            if (record == null) return string.Empty;
            var sb = new StringBuilder();
            sb.AppendLine("AI优化记录");
            sb.AppendLine("时间：" + (record.CreatedAt == DateTime.MinValue ? string.Empty : record.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")));
            sb.AppendLine("客服：" + record.Seller);
            sb.AppendLine("买家：" + record.Buyer);
            sb.AppendLine("状态：" + record.Status);
            sb.AppendLine("AI准确度：" + record.AccuracyText);
            sb.AppendLine();
            sb.AppendLine("买家问题：");
            sb.AppendLine(record.Question ?? string.Empty);
            sb.AppendLine();
            sb.AppendLine("AI最终答案（未发送，仅用于对比）：");
            sb.AppendLine(record.AiAnswer ?? string.Empty);
            sb.AppendLine();
            sb.AppendLine("人工客服实际回复：");
            sb.AppendLine(record.HumanAnswer ?? string.Empty);
            sb.AppendLine();
            sb.AppendLine("AI回复准确性分析：");
            sb.AppendLine(record.AccuracyAnalysis ?? string.Empty);
            sb.AppendLine();
            sb.AppendLine("人工为什么这样回复：");
            sb.AppendLine(record.HumanReplyReason ?? string.Empty);
            sb.AppendLine();
            sb.AppendLine("知识库/策略建议：");
            sb.AppendLine(record.KnowledgeStrategy ?? string.Empty);
            sb.AppendLine();
            sb.AppendLine("自动应用：" + record.AppliedCount + " 条；跳过：" + record.SkippedCount + " 条");
            if (!string.IsNullOrWhiteSpace(record.SuggestionsJson))
            {
                sb.AppendLine();
                sb.AppendLine("结构化优化建议：");
                try { sb.AppendLine(JToken.Parse(record.SuggestionsJson).ToString(Formatting.Indented)); }
                catch { sb.AppendLine(record.SuggestionsJson); }
            }
            if (!string.IsNullOrWhiteSpace(record.Error))
            {
                sb.AppendLine();
                sb.AppendLine("异常：" + record.Error);
            }
            return sb.ToString().Trim();
        }

        private static async Task CompareAndPersistAsync(
            string seller,
            string buyer,
            string question,
            string aiAnswer,
            string humanAnswer,
            DateTime questionDetectedAt,
            DateTime manualReplyAt)
        {
            EnsureSchema();
            var now = DateTime.Now;
            var entity = new AiOptimizationRecordEntity
            {
                EntityId = Guid.NewGuid().ToString("N"),
                Seller = seller,
                Buyer = buyer,
                Question = Redact(question),
                AiAnswer = Redact(aiAnswer),
                HumanAnswer = Redact(humanAnswer),
                Status = "正在分析",
                QuestionDetectedAtTicks = questionDetectedAt == DateTime.MinValue ? now.Ticks : questionDetectedAt.Ticks,
                ManualReplyAtTicks = manualReplyAt == DateTime.MinValue ? now.Ticks : manualReplyAt.Ticks,
                CreatedAtTicks = now.Ticks,
                UpdatedAtTicks = now.Ticks
            };
            Save(entity);
            NotifyChanged();

            try
            {
                var from = (questionDetectedAt == DateTime.MinValue ? now : questionDetectedAt).AddMinutes(-10);
                var to = now.AddSeconds(3);
                var turns = ConversationSessionLearningRuntimeBridge.GetTurnsBetween(
                    seller, buyer, from, to, 80, true);
                var cards = BotConversationHistoryStore.LoadRange(seller, buyer, from, to, 80);
                var transcript = BuildTranscript(turns, cards);

                var messages = new JArray
                {
                    new JObject
                    {
                        ["role"] = "system",
                        ["content"] =
                            "你是电商客服AI回复质量审计与知识库优化器。客服已经人工回复，但Bot仍完成了AI答案。"
                            + "请结合完整上下文，比较AI答案与人工实际回复，只输出JSON："
                            + "{\"accuracy_score\":0-100,\"accuracy_analysis\":\"AI哪里正确/错误/遗漏\","
                            + "\"human_reply_reason\":\"人工为什么这样回答，依据上下文说明\","
                            + "\"knowledge_strategy\":\"是否需要新增/修改知识库或回复策略以及原因\","
                            + "\"suggestions\":[{\"action\":\"add|update|skip\",\"question\":\"可复用通用问题\","
                            + "\"answer\":\"完整可复用答案\",\"old_answer\":\"旧答案\",\"category\":\"分类\","
                            + "\"keywords\":[\"关键词\"],\"confidence\":0.0,\"evidence_type\":\"manual_reply|manual_correction|insufficient\","
                            + "\"evidence\":\"人工证据\",\"reason\":\"修改理由\"}]}。"
                            + "人工回复优先作为纠错证据，但一次性订单状态、隐私、退款赔偿、验证码、账号安全等高风险结论不得自动固化。"
                            + "Bot答案不能作为新增事实的唯一证据；没有可靠人工事实证据必须skip；不得保存真实手机号、订单号、验证码等个人信息。"
                    },
                    new JObject
                    {
                        ["role"] = "user",
                        ["content"] = "客服：" + Redact(seller)
                            + "\n买家：" + Redact(buyer)
                            + "\n当前问题：" + Redact(question)
                            + "\nAI最终答案（未发送）：" + Redact(aiAnswer)
                            + "\n人工客服实际回复：" + Redact(humanAnswer)
                            + "\n\n此前及本轮聊天时间线：\n" + transcript
                    }
                };

                var result = await Task.Run(() => MyOpenAI.CallStructuredChat(
                    messages, 2400, 0.05, 50, CancellationToken.None)).ConfigureAwait(false);
                if (result == null || !result.Success || string.IsNullOrWhiteSpace(result.Answer))
                {
                    throw new Exception(result == null ? "AI对比结果为空" : result.Error);
                }

                var analysis = ParseObject(result.Answer);
                entity.AccuracyScore = ParseScore(analysis["accuracy_score"]);
                entity.AccuracyAnalysis = Redact(Clean(Convert.ToString(analysis["accuracy_analysis"]), 1800));
                entity.HumanReplyReason = Redact(Clean(Convert.ToString(analysis["human_reply_reason"]), 1800));
                entity.KnowledgeStrategy = Redact(Clean(Convert.ToString(analysis["knowledge_strategy"]), 1800));

                var suggestions = analysis["suggestions"] as JArray ?? new JArray();
                var persistedSuggestions = new JArray();
                var applied = 0;
                var skipped = 0;
                foreach (var token in suggestions.OfType<JObject>().Take(8))
                {
                    var item = (JObject)token.DeepClone();
                    var action = Clean(Convert.ToString(item["action"]), 20).ToLowerInvariant();
                    var suggestionQuestion = Redact(Clean(Convert.ToString(item["question"]), 500));
                    var suggestionAnswer = StripAi(Redact(Clean(Convert.ToString(item["answer"]), 1500)));
                    var oldAnswer = StripAi(Redact(Clean(Convert.ToString(item["old_answer"]), 1500)));
                    var category = Clean(Convert.ToString(item["category"]), 100);
                    var evidenceType = Clean(Convert.ToString(item["evidence_type"]), 80).ToLowerInvariant();
                    var confidence = ParseConfidence(item["confidence"]);
                    var keywords = item["keywords"] is JArray
                        ? string.Join(",", ((JArray)item["keywords"]).Select(x => Clean(Convert.ToString(x), 80)).Where(x => x.Length > 0))
                        : Clean(Convert.ToString(item["keywords"]), 500);
                    var canApply = (action == "add" || action == "update")
                        && confidence >= 0.88
                        && (evidenceType == "manual_reply" || evidenceType == "manual_correction")
                        && suggestionQuestion.Length > 0
                        && suggestionAnswer.Length > 0
                        && !ContainsHighRisk(suggestionQuestion + " " + suggestionAnswer);

                    string applyMessage;
                    var wasApplied = false;
                    if (canApply)
                    {
                        var write = ReviewedKnowledgeLearningService.ApplyReviewedKnowledge(
                            suggestionQuestion,
                            suggestionAnswer,
                            category,
                            keywords,
                            "人工即时对比",
                            confidence,
                            evidenceType);
                        wasApplied = write != null && write.Success && (write.Added || write.Updated);
                        applyMessage = write == null ? "知识写入结果为空" : write.Message;
                    }
                    else
                    {
                        applyMessage = action == "skip" ? "AI建议跳过" : "未达到人工证据/置信度/安全边界";
                    }
                    if (wasApplied) applied++; else skipped++;
                    item["question"] = suggestionQuestion;
                    item["answer"] = suggestionAnswer;
                    item["old_answer"] = oldAnswer;
                    item["confidence"] = confidence;
                    item["applied"] = wasApplied;
                    item["apply_message"] = applyMessage;
                    persistedSuggestions.Add(item);
                }

                entity.SuggestionsJson = persistedSuggestions.ToString(Formatting.None);
                entity.AppliedCount = applied;
                entity.SkippedCount = skipped;
                entity.Status = "分析完成";
                entity.Error = string.Empty;
                entity.UpdatedAtTicks = DateTime.Now.Ticks;
                Save(entity);
                Cleanup();
                NotifyChanged();
                Log.Info("AI人工回复即时对比完成: seller=" + seller + ", buyer=" + buyer
                    + ", accuracy=" + entity.AccuracyScore.ToString("0")
                    + ", applied=" + applied + ", skipped=" + skipped);
            }
            catch (Exception ex)
            {
                entity.Status = "分析失败";
                entity.Error = Clean(ex.Message, 1600);
                entity.UpdatedAtTicks = DateTime.Now.Ticks;
                Save(entity);
                NotifyChanged();
                throw;
            }
        }

        private static string BuildTranscript(
            List<ConversationContextTurn> turns,
            List<BotConversationHistoryEntity> cards)
        {
            var sb = new StringBuilder();
            foreach (var turn in (turns ?? new List<ConversationContextTurn>()).OrderBy(x => x.Timestamp))
            {
                if (turn == null || string.IsNullOrWhiteSpace(turn.Text)) continue;
                var role = turn.Role == "user" ? "买家" : (turn.Withdrawn ? "客服-已撤回" : (IsBotTurn(turn, cards) ? "Bot" : "人工客服"));
                var time = turn.Timestamp == DateTime.MinValue ? "时间未知" : turn.Timestamp.ToString("HH:mm:ss");
                sb.Append('[').Append(time).Append(' ').Append(role).Append("] ")
                    .AppendLine(Redact(Clean(turn.Text, 1600)));
            }
            if (sb.Length == 0) return "（未读取到更多聊天记录，仅比较当前问题与两份答案）";
            return sb.ToString().Trim();
        }

        private static bool IsBotTurn(ConversationContextTurn turn, List<BotConversationHistoryEntity> cards)
        {
            var text = (turn == null ? string.Empty : turn.Text ?? string.Empty).Trim();
            if (text.EndsWith("[AI]", StringComparison.OrdinalIgnoreCase)) return true;
            var normalized = Normalize(StripAi(text));
            return normalized.Length > 0 && cards != null
                && cards.Any(x => Normalize(StripAi(x.Answer)) == normalized);
        }

        private static JObject ParseObject(string value)
        {
            value = (value ?? string.Empty).Trim();
            var fenced = Regex.Match(value, @"```(?:json)?\s*(\{[\s\S]*\})\s*```", RegexOptions.IgnoreCase);
            if (fenced.Success) value = fenced.Groups[1].Value;
            var start = value.IndexOf('{');
            var end = value.LastIndexOf('}');
            if (start >= 0 && end > start) value = value.Substring(start, end - start + 1);
            return JObject.Parse(value);
        }

        private static void Save(AiOptimizationRecordEntity entity)
        {
            EnsureSchema();
            lock (DbSync)
            {
                DbHelper.Db.SaveRecordsInTransaction(new List<object> { entity });
            }
        }

        private static void EnsureSchema()
        {
            if (Volatile.Read(ref _schemaReady) != 0) return;
            lock (DbSync)
            {
                if (_schemaReady != 0) return;
                DbHelper.Db.Execute(
                    "create table if not exists AiOptimizationRecordEntity ("
                    + "EntityId text primary key not null,"
                    + "Seller text,Buyer text,Question text,AiAnswer text,HumanAnswer text,"
                    + "AccuracyScore real not null default 0,AccuracyAnalysis text,HumanReplyReason text,KnowledgeStrategy text,"
                    + "SuggestionsJson text,Status text,Error text,AppliedCount integer not null default 0,SkippedCount integer not null default 0,"
                    + "QuestionDetectedAtTicks integer not null default 0,ManualReplyAtTicks integer not null default 0,"
                    + "CreatedAtTicks integer not null default 0,UpdatedAtTicks integer not null default 0)");
                DbHelper.Db.Execute("create index if not exists IX_AiOptimizationRecord_Updated on AiOptimizationRecordEntity(UpdatedAtTicks)");
                Volatile.Write(ref _schemaReady, 1);
            }
        }

        private static void Cleanup()
        {
            try
            {
                EnsureSchema();
                lock (DbSync)
                {
                    DbHelper.Db.Execute("delete from AiOptimizationRecordEntity where UpdatedAtTicks < ?", DateTime.Now.AddDays(-RetentionDays).Ticks);
                    DbHelper.Db.Execute(
                        "delete from AiOptimizationRecordEntity where EntityId not in "
                        + "(select EntityId from AiOptimizationRecordEntity order by UpdatedAtTicks desc limit " + MaxRecords + ")");
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("清理AI优化记录失败：" + ex.Message, 10);
            }
        }

        private static AiOptimizationRecordView ToView(AiOptimizationRecordEntity x)
        {
            return new AiOptimizationRecordView
            {
                Id = x.EntityId,
                Seller = x.Seller,
                Buyer = x.Buyer,
                Question = x.Question,
                AiAnswer = x.AiAnswer,
                HumanAnswer = x.HumanAnswer,
                AccuracyScore = x.AccuracyScore,
                AccuracyAnalysis = x.AccuracyAnalysis,
                HumanReplyReason = x.HumanReplyReason,
                KnowledgeStrategy = x.KnowledgeStrategy,
                SuggestionsJson = x.SuggestionsJson,
                Status = x.Status,
                Error = x.Error,
                AppliedCount = x.AppliedCount,
                SkippedCount = x.SkippedCount,
                QuestionDetectedAt = TicksToDate(x.QuestionDetectedAtTicks),
                ManualReplyAt = TicksToDate(x.ManualReplyAtTicks),
                CreatedAt = TicksToDate(x.CreatedAtTicks)
            };
        }

        private static DateTime TicksToDate(long ticks)
        {
            try { return ticks > 0 ? new DateTime(ticks, DateTimeKind.Local) : DateTime.MinValue; }
            catch { return DateTime.MinValue; }
        }

        private static double ParseScore(JToken token)
        {
            double value;
            if (!double.TryParse(Convert.ToString(token), out value)) return 0;
            if (value <= 1) value *= 100;
            return Math.Max(0, Math.Min(100, value));
        }

        private static double ParseConfidence(JToken token)
        {
            double value;
            if (!double.TryParse(Convert.ToString(token), out value)) return 0;
            if (value > 1) value /= 100.0;
            return Math.Max(0, Math.Min(1, value));
        }

        private static bool ContainsHighRisk(string value)
        {
            var terms = new[] { "退款", "退货", "赔偿", "投诉", "差评", "举报", "仲裁", "身份证", "银行卡", "验证码", "密码", "订单隐私", "订单号", "手机号", "账号安全", "封号", "解封", "法律", "报警" };
            return terms.Any(x => (value ?? string.Empty).IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string Redact(string value)
        {
            value = value ?? string.Empty;
            value = Regex.Replace(value, @"(?<!\d)1[3-9]\d{9}(?!\d)", "[手机号已隐藏]");
            value = Regex.Replace(value, @"(?<!\d)\d{14,20}(?!\d)", "[长数字已隐藏]");
            value = Regex.Replace(value, @"(?i)(验证码|code)\s*[:：]?\s*\d{4,8}", "$1：[已隐藏]");
            return value;
        }

        private static string StripAi(string value)
        {
            value = (value ?? string.Empty).Trim();
            return Regex.Replace(value, @"\s*(?:\[AI\]|【AI】|［AI］)\s*$", string.Empty, RegexOptions.IgnoreCase).Trim();
        }

        private static string Clean(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\0", string.Empty).Trim();
            return value.Length <= max ? value : value.Substring(0, max);
        }

        private static string Normalize(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", string.Empty);
        }

        private static string Key(string seller, string buyer)
        {
            return (seller ?? string.Empty).Trim().ToLowerInvariant() + "#" + (buyer ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static void NotifyChanged()
        {
            try
            {
                var handler = RecordsChanged;
                if (handler != null) handler();
            }
            catch { }
        }
    }
}
