using Bot.Knowledge;
using BotLib;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace Bot.ChromeNs
{
    internal sealed class ReplyDeduplicationResult
    {
        public string Answer { get; set; }
        public string PreviousAnswer { get; set; }
        public string Source { get; set; }
        public bool Regenerated { get; set; }
    }

    internal static class ReplyDeduplicationService
    {
        private sealed class DeliveredAnswerStamp
        {
            public string Answer;
            public DateTime SentAt;
        }

        private static readonly ConcurrentDictionary<string, DeliveredAnswerStamp> LastDelivered =
            new ConcurrentDictionary<string, DeliveredAnswerStamp>(StringComparer.Ordinal);

        public static ReplyDeduplicationResult EnsureDistinct(
            string seller,
            string buyer,
            string question,
            string candidateAnswer)
        {
            var knowledge = ResolveKnowledge(seller, buyer, question, candidateAnswer);
            var validationRegenerated = false;
            var validationSource = string.Empty;
            var exactTrustedKnowledge = knowledge != null
                && SameAnswer(knowledge.Answer, candidateAnswer);

            // 本地知识原文已经由 Smart Reply Router 的适用条件和可靠度门槛审核，
            // 这里主要审核 AI 生成、上下文改写和重复重答结果，避免对可信固定答案再次调用 AI。
            if (!exactTrustedKnowledge
                && !string.IsNullOrWhiteSpace(candidateAnswer)
                && !candidateAnswer.StartsWith("错误：", StringComparison.Ordinal))
            {
                var validation = PreSendAnswerValidator.Validate(
                    seller,
                    buyer,
                    question,
                    candidateAnswer,
                    knowledge,
                    false);
                ReplyQualityMetricsService.RecordValidation(
                    validation.Action,
                    validation.Issues,
                    false);
                if (validation.Action == AnswerValidationAction.Manual)
                {
                    return BuildBlockedResult("发送前校验要求人工确认：" + validation.Reason);
                }
                if (validation.Action == AnswerValidationAction.Regenerate)
                {
                    var repaired = RegenerateInvalidAnswer(
                        seller,
                        buyer,
                        question,
                        candidateAnswer,
                        knowledge,
                        validation);
                    repaired = BotFeatureStore.ApplyOutputPolicy(repaired);
                    if (string.IsNullOrWhiteSpace(repaired)
                        || repaired.StartsWith("错误：", StringComparison.Ordinal))
                    {
                        ReplyQualityMetricsService.RecordRepair(false);
                        return BuildBlockedResult("发送前校验重答失败，已阻止自动发送");
                    }

                    var secondValidation = PreSendAnswerValidator.Validate(
                        seller,
                        buyer,
                        question,
                        repaired,
                        knowledge,
                        true);
                    ReplyQualityMetricsService.RecordValidation(
                        secondValidation.Action,
                        secondValidation.Issues,
                        true);
                    if (secondValidation.Action != AnswerValidationAction.Pass)
                    {
                        ReplyQualityMetricsService.RecordRepair(false);
                        return BuildBlockedResult("修正后的答案仍未通过发送前校验：" + secondValidation.Reason);
                    }

                    ReplyQualityMetricsService.RecordRepair(true);
                    candidateAnswer = repaired;
                    validationRegenerated = true;
                    validationSource = "AI校验重答";
                    KnowledgeLearningService.RegisterAnswerSource(
                        seller,
                        buyer,
                        question,
                        candidateAnswer,
                        validationSource);
                    Log.Info("发送前答案校验已完成一次安全重答: seller="
                        + seller + ", buyer=" + buyer);
                }
            }

            var result = new ReplyDeduplicationResult
            {
                Answer = BotOutboundMessageFormatter.EnsureAiMarker(candidateAnswer),
                PreviousAnswer = string.Empty,
                Source = validationSource,
                Regenerated = validationRegenerated
            };

            if (string.IsNullOrWhiteSpace(result.Answer)
                || result.Answer.StartsWith("错误：", StringComparison.Ordinal))
            {
                return result;
            }

            string previousAnswer;
            DateTime previousAt;
            if (!TryGetLastDelivered(seller, buyer, out previousAnswer, out previousAt)
                || !SameAnswer(previousAnswer, result.Answer))
            {
                return result;
            }

            ReplyQualityMetricsService.RecordDuplicateRewrite();
            knowledge = knowledge ?? ResolveKnowledge(seller, buyer, question, result.Answer);
            var regenerated = result.Regenerated
                ? BuildSafeFallback(question)
                : Regenerate(seller, buyer, question, previousAnswer, knowledge);
            if (string.IsNullOrWhiteSpace(regenerated)
                || regenerated.StartsWith("错误：", StringComparison.Ordinal)
                || SameAnswer(previousAnswer, regenerated))
            {
                regenerated = BuildSafeFallback(question);
            }

            regenerated = BotFeatureStore.ApplyOutputPolicy(regenerated);
            if (string.IsNullOrWhiteSpace(regenerated) || SameAnswer(previousAnswer, regenerated))
            {
                regenerated = "如果前面的步骤都试过仍无效，建议转人工进一步核查。";
            }

            var duplicateValidation = PreSendAnswerValidator.Validate(
                seller,
                buyer,
                question,
                regenerated,
                knowledge,
                true);
            ReplyQualityMetricsService.RecordValidation(
                duplicateValidation.Action,
                duplicateValidation.Issues,
                true);
            if (duplicateValidation.Action != AnswerValidationAction.Pass)
            {
                return BuildBlockedResult("重复答案重答未通过发送前校验：" + duplicateValidation.Reason);
            }

            result.Answer = BotOutboundMessageFormatter.EnsureAiMarker(regenerated);
            result.PreviousAnswer = previousAnswer;
            result.Source = result.Regenerated ? "AI校验重答+重复兜底" : (knowledge == null ? "AI重答" : "本地知识库重答");
            result.Regenerated = true;
            KnowledgeLearningService.RegisterAnswerSource(
                seller,
                buyer,
                question,
                BotOutboundMessageFormatter.StripAiMarker(result.Answer),
                result.Source);
            Log.Info("检测到与上一轮完全相同的答案，已重新生成。seller="
                + seller + ", buyer=" + buyer + ", source=" + result.Source);
            return result;
        }

        public static void RememberDelivered(string seller, string buyer, string answer)
        {
            if (string.IsNullOrWhiteSpace(answer)
                || answer.StartsWith("错误：", StringComparison.Ordinal))
            {
                return;
            }

            LastDelivered[Key(seller, buyer)] = new DeliveredAnswerStamp
            {
                Answer = BotOutboundMessageFormatter.EnsureAiMarker(answer),
                SentAt = DateTime.Now
            };
            Cleanup();
        }

        public static bool TryGetLastDelivered(
            string seller,
            string buyer,
            out string answer,
            out DateTime sentAt)
        {
            answer = string.Empty;
            sentAt = DateTime.MinValue;
            DeliveredAnswerStamp stamp;
            if (LastDelivered.TryGetValue(Key(seller, buyer), out stamp)
                && stamp != null
                && stamp.SentAt >= DateTime.Now.AddMinutes(-30)
                && !string.IsNullOrWhiteSpace(stamp.Answer))
            {
                answer = stamp.Answer;
                sentAt = stamp.SentAt;
                return true;
            }

            var latest = ConversationContextStore
                .GetRecentTurns(seller, buyer, string.Empty, 12)
                .Where(x => x != null
                    && x.Role == "assistant"
                    && !x.Withdrawn
                    && !string.IsNullOrWhiteSpace(x.Text))
                .OrderByDescending(x => x.Timestamp)
                .FirstOrDefault();
            if (latest == null) return false;
            if (latest.Timestamp != DateTime.MinValue
                && latest.Timestamp < DateTime.Now.AddMinutes(-30))
            {
                return false;
            }

            answer = latest.Text.Trim();
            sentAt = latest.Timestamp == DateTime.MinValue ? DateTime.Now : latest.Timestamp;
            return true;
        }

        private static KnowledgeBaseEntry ResolveKnowledge(
            string seller,
            string buyer,
            string question,
            string candidateAnswer)
        {
            var answerKey = Canonical(candidateAnswer);
            var knowledge = BotFeatureStore.GetKnowledgeBase()
                .FirstOrDefault(x => x != null
                    && x.Enabled
                    && !string.IsNullOrWhiteSpace(x.Answer)
                    && (Canonical(x.Answer) == answerKey
                        || Canonical(BotFeatureStore.ApplyOutputPolicy(x.Answer)) == answerKey));
            if (knowledge != null) return knowledge;

            KnowledgeBaseEntry matched;
            double score;
            return KnowledgeLearningService.TryFindLocalAnswer(
                seller,
                buyer,
                question,
                out matched,
                out score)
                ? matched
                : null;
        }

        private static string RegenerateInvalidAnswer(
            string seller,
            string buyer,
            string question,
            string invalidAnswer,
            KnowledgeBaseEntry knowledge,
            AnswerValidationResult validation)
        {
            try
            {
                var timeline = ConversationContextStore.BuildTimelineText(seller, buyer, question, 12);
                var evidence = PreSendAnswerValidator.BuildEvidenceText(knowledge);
                var messages = new JArray
                {
                    new JObject
                    {
                        ["role"] = "system",
                        ["content"] = "你是电商客服答案修正器。" + validation.RegenerationInstruction
                    },
                    new JObject
                    {
                        ["role"] = "user",
                        ["content"] = (string.IsNullOrWhiteSpace(evidence)
                                ? "【可靠事实】未提供可确认的店铺事实，禁止自行补充。"
                                : evidence)
                            + "\n【买家当前问题】\n" + (question ?? string.Empty)
                            + "\n【未通过校验的原答案】\n" + BotOutboundMessageFormatter.StripAiMarker(invalidAnswer)
                            + (string.IsNullOrWhiteSpace(timeline)
                                ? string.Empty
                                : "\n【同一买家最近时间线】\n" + timeline)
                    }
                };
                var response = MyOpenAI.CallStructuredChat(
                    messages,
                    220,
                    0.10,
                    45,
                    CancellationToken.None);
                return response != null && response.Success
                    ? (response.Answer ?? string.Empty).Trim()
                    : string.Empty;
            }
            catch (Exception ex)
            {
                Log.Info("发送前答案校验重答失败：" + ex.Message);
                return string.Empty;
            }
        }

        private static string Regenerate(
            string seller,
            string buyer,
            string question,
            string previousAnswer,
            KnowledgeBaseEntry knowledge)
        {
            try
            {
                var timeline = ConversationContextStore.BuildTimelineText(seller, buyer, question, 12);
                var factBoundary = knowledge == null
                    ? "上一轮答案是当前唯一事实边界，不得增加新的商品承诺或结论。"
                    : "知识库问题：" + knowledge.Title + "\n知识库答案：" + knowledge.Answer;
                var messages = new JArray
                {
                    new JObject
                    {
                        ["role"] = "system",
                        ["content"] = "你是电商客服续答助手。候选答案与上一轮客服回复完全相同，禁止再次原样回复，也不要只做同义改写。必须结合买家当前新消息推进对话：买家表示已解决时简短确认；表示没解决、否定或追问时，承认前一步未解决，并给出事实范围内的下一步；没有新步骤时建议转人工核查。只回复一句，最多60字，不得编造价格、库存、到账状态、时效或售后承诺。"
                    },
                    new JObject
                    {
                        ["role"] = "user",
                        ["content"] = factBoundary
                            + "\n上一轮客服答案：" + BotOutboundMessageFormatter.StripAiMarker(previousAnswer)
                            + "\n当前买家消息：" + (question ?? string.Empty)
                            + (string.IsNullOrWhiteSpace(timeline)
                                ? string.Empty
                                : "\n同一买家最近时间线：\n" + timeline)
                    }
                };
                var response = MyOpenAI.CallStructuredChat(
                    messages,
                    180,
                    0.35,
                    90,
                    CancellationToken.None);
                return response != null && response.Success
                    ? (response.Answer ?? string.Empty).Trim()
                    : string.Empty;
            }
            catch (Exception ex)
            {
                Log.Info("重复答案重新生成失败，使用安全兜底：" + ex.Message);
                return string.Empty;
            }
        }

        private static ReplyDeduplicationResult BuildBlockedResult(string reason)
        {
            return new ReplyDeduplicationResult
            {
                Answer = "错误：" + (reason ?? "发送前校验未通过"),
                PreviousAnswer = string.Empty,
                Source = "发送前校验",
                Regenerated = false
            };
        }

        private static string BuildSafeFallback(string question)
        {
            var compact = Canonical(question);
            if (ContainsAny(compact, "好了", "可以了", "解决了", "能用了", "正常了"))
            {
                return "好的，能正常使用就行，有其他问题再告诉我。";
            }
            if (ContainsAny(compact, "没有", "没到", "不行", "不可以", "不能", "还是", "没解决"))
            {
                return "明白，刚才的方法还没解决，我换个方向继续帮您排查。";
            }
            if (ContainsAny(compact, "怎么回事", "为什么", "怎么了"))
            {
                return "这说明刚才的处理还没生效，我继续帮您核查下一步。";
            }
            return "明白，我换个思路继续帮您处理，避免重复前面的步骤。";
        }

        private static bool ContainsAny(string value, params string[] cues)
        {
            return cues.Any(x => value.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool SameAnswer(string left, string right)
        {
            return string.Equals(Canonical(left), Canonical(right), StringComparison.Ordinal);
        }

        private static string Canonical(string value)
        {
            value = BotOutboundMessageFormatter.StripAiMarker(value);
            return Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ");
        }

        private static string Key(string seller, string buyer)
        {
            return (seller ?? string.Empty).Trim().ToLowerInvariant()
                + "|" + (buyer ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static void Cleanup()
        {
            var cutoff = DateTime.Now.AddHours(-2);
            foreach (var key in LastDelivered
                .Where(x => x.Value == null || x.Value.SentAt < cutoff)
                .Select(x => x.Key)
                .ToList())
            {
                DeliveredAnswerStamp ignored;
                LastDelivered.TryRemove(key, out ignored);
            }
        }
    }

    internal static class BotOutboundMessageFormatter
    {
        public const string AiMarker = "[AI]";
        public const string StreamAbortMarker = "[[QN_STREAM_ABORTED]]";

        public static string EnsureAiMarker(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.IndexOf(StreamAbortMarker, StringComparison.Ordinal) >= 0)
            {
                Log.Info("检测到AI流式输出中断标识，已阻止发送半截答案。");
                return "错误：AI流式输出中断，已阻止发送半截答案，请重新获取完整答案。";
            }
            if (value.Length == 0 || value.StartsWith("错误：", StringComparison.Ordinal)) return value;
            if (value.EndsWith(AiMarker, StringComparison.OrdinalIgnoreCase)) return value;
            return value + " " + AiMarker;
        }

        public static string StripAiMarker(string value)
        {
            value = (value ?? string.Empty).Trim();
            while (value.EndsWith(AiMarker, StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(0, value.Length - AiMarker.Length).TrimEnd();
            }
            return value;
        }
    }
}
