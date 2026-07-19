using Bot.Knowledge;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Bot.ChromeNs
{
    internal sealed class ContextualKnowledgeDecision
    {
        public bool IsFollowUp { get; set; }
        public string PreviousBuyerText { get; set; }
        public string PreviousAssistantText { get; set; }
        public string CurrentBuyerText { get; set; }
        public string Reason { get; set; }
    }

    internal static class KnowledgeContextualReplyService
    {
        private static readonly string[] Interrogatives =
        {
            "吗", "么", "呢", "怎么", "如何", "为什么", "多少", "哪个", "哪里",
            "何时", "能否", "可不可以", "是不是", "有没有", "是否", "请问"
        };

        private static readonly string[] AffirmativeCues =
        {
            "有", "有的", "是", "是的", "对", "对的", "可以", "能", "支持",
            "好的", "好", "嗯", "行", "没问题", "已经"
        };

        private static readonly string[] NegativeCues =
        {
            "没有", "没", "不是", "不可以", "不能", "不支持", "不行", "无"
        };

        public static ContextualKnowledgeDecision Analyze(
            string seller,
            string buyer,
            string currentQuestion,
            KnowledgeBaseEntry knowledge)
        {
            var decision = new ContextualKnowledgeDecision
            {
                IsFollowUp = false,
                PreviousBuyerText = string.Empty,
                PreviousAssistantText = string.Empty,
                CurrentBuyerText = (currentQuestion ?? string.Empty).Trim(),
                Reason = string.Empty
            };

            var compact = Compact(currentQuestion);
            if (compact.Length < 1 || compact.Length > 28)
            {
                return decision;
            }

            var turns = ConversationContextStore.GetRecentTurns(seller, buyer, currentQuestion, 10);
            var assistantIndex = -1;
            ConversationContextTurn previousAssistant = null;
            for (var i = turns.Count - 1; i >= 0; i--)
            {
                if (turns[i] != null
                    && turns[i].Role == "assistant"
                    && !string.IsNullOrWhiteSpace(turns[i].Text))
                {
                    assistantIndex = i;
                    previousAssistant = turns[i];
                    break;
                }
            }

            string deliveredAnswer;
            DateTime deliveredAt;
            if (ReplyDeduplicationService.TryGetLastDelivered(
                seller,
                buyer,
                out deliveredAnswer,
                out deliveredAt)
                && (previousAssistant == null
                    || previousAssistant.Timestamp == DateTime.MinValue
                    || deliveredAt >= previousAssistant.Timestamp))
            {
                previousAssistant = new ConversationContextTurn
                {
                    Role = "assistant",
                    Text = deliveredAnswer,
                    Timestamp = deliveredAt,
                    MessageKey = "local-delivered-answer",
                    Withdrawn = false
                };
                assistantIndex = turns.Count;
            }

            if (previousAssistant == null) return decision;
            if (previousAssistant.Timestamp != DateTime.MinValue
                && previousAssistant.Timestamp < DateTime.Now.AddMinutes(-20))
            {
                return decision;
            }

            var previousBuyer = turns
                .Take(Math.Max(0, assistantIndex))
                .LastOrDefault(x => x != null
                    && x.Role == "user"
                    && !string.IsNullOrWhiteSpace(x.Text));

            var assistantText = previousAssistant.Text.Trim();
            var knowledgeAnswer = knowledge == null ? string.Empty : (knowledge.Answer ?? string.Empty).Trim();
            var previousHasOpenCondition = HasOpenCondition(assistantText);
            var previousWasKnowledgeAnswer = SameOrContained(assistantText, knowledgeAnswer);
            var affirmative = ContainsCue(compact, AffirmativeCues);
            var negative = ContainsCue(compact, NegativeCues);
            var sharedTopic = SharedBigramCount(compact, Compact(assistantText)) >= 1;
            var veryShortReply = compact.Length <= 8;
            var standaloneQuestion = LooksLikeStandaloneQuestion(compact);

            if (standaloneQuestion
                && !previousWasKnowledgeAnswer
                && !previousHasOpenCondition)
            {
                return decision;
            }

            if ((previousHasOpenCondition || previousWasKnowledgeAnswer)
                && (affirmative || negative || sharedTopic || veryShortReply))
            {
                decision.IsFollowUp = true;
                decision.PreviousBuyerText = previousBuyer == null ? string.Empty : previousBuyer.Text.Trim();
                decision.PreviousAssistantText = assistantText;
                decision.Reason = previousWasKnowledgeAnswer
                    ? "上一条客服回复与本次命中的知识答案相同，当前短句应作为续答处理"
                    : "上一条客服回复包含待买家确认的条件，当前短句应作为续答处理";
            }

            return decision;
        }

        public static string BuildPromptAddon(
            ContextualKnowledgeDecision decision,
            KnowledgeBaseEntry knowledge)
        {
            if (decision == null || !decision.IsFollowUp || knowledge == null) return string.Empty;

            var sb = new StringBuilder();
            sb.Append("\n\n本轮已经优先命中本地知识库，但当前买家消息是对上一轮客服回复的补充、确认或否定，不是一个新的独立问题。")
                .Append("必须基于知识库事实进行自然衔接，不得原样重复上一条客服回复或整段知识库答案。")
                .Append("直接给出确认后的结论，一句话即可；不得增加知识库之外的承诺。")
                .Append("\n知识库问题：").Append(Safe(knowledge.Title, 240))
                .Append("\n知识库答案：").Append(Safe(knowledge.Answer, 700));

            if (!string.IsNullOrWhiteSpace(decision.PreviousBuyerText))
            {
                sb.Append("\n上一轮买家：").Append(Safe(decision.PreviousBuyerText, 300));
            }
            sb.Append("\n上一轮客服：").Append(Safe(decision.PreviousAssistantText, 700))
                .Append("\n当前买家：").Append(Safe(decision.CurrentBuyerText, 300))
                .Append("\n正确风格示例：买家确认满足条件时，回复“那就可以的”或“那就支持使用”；买家否定条件时，直接说明目前不支持。不要重复条件说明。\n");
            return sb.ToString();
        }

        public static string BuildOfflineFallback(
            ContextualKnowledgeDecision decision,
            KnowledgeBaseEntry knowledge)
        {
            if (decision == null || !decision.IsFollowUp) return string.Empty;
            var current = Compact(decision.CurrentBuyerText);
            var answer = Compact(knowledge == null ? string.Empty : knowledge.Answer);

            if (ContainsCue(current, NegativeCues))
            {
                if (answer.Contains("不支持") || answer.Contains("不能") || answer.Contains("无法"))
                {
                    return "那目前就不支持，需要满足前面说的条件后才可以。";
                }
                return "好的，当前条件不满足，先按前面说明处理。";
            }

            if (ContainsCue(current, AffirmativeCues))
            {
                if (answer.Contains("支持") || answer.Contains("可以") || answer.Contains("能使用"))
                {
                    var feature = ExtractConfirmedFeature(decision.CurrentBuyerText);
                    if (!string.IsNullOrWhiteSpace(feature))
                    {
                        return "那就可以使用" + feature + (feature.EndsWith("功能", StringComparison.Ordinal) ? string.Empty : "功能") + "。";
                    }
                    return "好的，那就可以的。";
                }
                return "好的，已确认满足前面说的条件。";
            }

            return "好的，已结合您刚才补充的信息确认。";
        }

        private static bool LooksLikeStandaloneQuestion(string compact)
        {
            if (compact.Contains("?") || compact.Contains("？")) return true;
            return Interrogatives.Any(x => compact.Contains(x));
        }

        private static bool HasOpenCondition(string text)
        {
            var compact = Compact(text);
            if (compact.Length < 1) return false;
            return Regex.IsMatch(compact,
                @"如果|若|只要|请问|确认|是否|有没有|有无|告诉我|回复|(?:有|支持|具备).{0,14}(?:就|则|才|才能|可以|支持)|(?:没有|不具备|不支持).{0,14}(?:则|就|不能|不可以|不支持)",
                RegexOptions.IgnoreCase);
        }

        private static bool SameOrContained(string left, string right)
        {
            var a = Compact(left);
            var b = Compact(right);
            if (a.Length < 6 || b.Length < 6) return false;
            return a == b || a.Contains(b) || b.Contains(a);
        }

        private static bool ContainsCue(string compact, IEnumerable<string> cues)
        {
            if (string.IsNullOrWhiteSpace(compact)) return false;
            return cues.Any(cue => compact == cue
                || compact.StartsWith(cue, StringComparison.Ordinal)
                || compact.Contains(cue));
        }

        private static int SharedBigramCount(string left, string right)
        {
            var a = Bigrams(left);
            var b = Bigrams(right);
            if (a.Count < 1 || b.Count < 1) return 0;
            return a.Intersect(b).Count();
        }

        private static HashSet<string> Bigrams(string value)
        {
            var compact = Compact(value);
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i + 1 < compact.Length; i++)
            {
                var pair = compact.Substring(i, 2);
                if (pair.All(char.IsDigit)) continue;
                result.Add(pair);
            }
            return result;
        }

        private static string ExtractConfirmedFeature(string value)
        {
            var text = (value ?? string.Empty).Trim();
            var match = Regex.Match(text, @"(?:有|支持|可以使用|能用)(?<feature>[\u4e00-\u9fa5A-Za-z0-9]{1,10})", RegexOptions.IgnoreCase);
            if (!match.Success) return string.Empty;
            var feature = match.Groups["feature"].Value.Trim();
            if (feature == "的" || feature == "了" || feature == "这个") return string.Empty;
            return feature;
        }

        private static string Compact(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"[\s，。！？、；：,.!?:;\-—_()（）\[\]【】]+", string.Empty);
        }

        private static string Safe(string value, int maxLength)
        {
            var text = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (text.Contains("  ")) text = text.Replace("  ", " ");
            return text.Length <= maxLength ? text : text.Substring(0, maxLength) + "...";
        }
    }
}
