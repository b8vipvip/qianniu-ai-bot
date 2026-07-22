using Bot.Knowledge;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Bot.ChromeNs
{
    internal enum SmartReplyRouteKind
    {
        DirectKnowledge,
        ContextualKnowledge,
        AiGeneral
    }

    internal sealed class SmartKnowledgeCandidate
    {
        public KnowledgeBaseEntry Entry { get; set; }
        public double RetrievalScore { get; set; }
        public double FinalScore { get; set; }
        public bool ExactQuestionMatch { get; set; }
        public string Intent { get; set; }
    }

    internal sealed class SmartReplyPlan
    {
        public SmartReplyRouteKind Route { get; set; }
        public double ContextDependencyScore { get; set; }
        public string Reason { get; set; }
        public List<SmartKnowledgeCandidate> Candidates { get; set; }
        public List<ConversationContextTurn> RecentTurns { get; set; }
        public string ContextDigest { get; set; }

        public SmartKnowledgeCandidate BestCandidate
        {
            get { return Candidates == null || Candidates.Count == 0 ? null : Candidates[0]; }
        }
    }

    internal static class SmartReplyRouterService
    {
        private const int RetrievalPoolSize = 10;
        private const int PromptCandidateCount = 3;
        private const int RecentPromptTurns = 8;

        private static readonly string[] ContextCues =
        {
            "这个", "那个", "这款", "那款", "它", "这样", "那样", "刚才", "上面", "前面",
            "还是", "然后呢", "那呢", "这个呢", "那个呢", "为什么不", "怎么不", "我发了",
            "收到了吗", "可以吗", "行吗", "对吗", "是不是"
        };

        private static readonly string[] ConditionalAnswerCues =
        {
            "如果", "若", "只要", "需要先", "请提供", "请确认", "视情况", "根据实际", "以页面为准",
            "联系客服", "人工确认", "订单情况"
        };

        private static readonly string[] HighRiskTerms =
        {
            "退款", "退货", "赔偿", "投诉", "差评", "举报", "仲裁", "验证码", "密码", "身份证",
            "银行卡", "订单号", "手机号", "账号安全", "封号", "解封"
        };

        public static SmartReplyPlan BuildPlan(string seller, string buyer, string question)
        {
            question = (question ?? string.Empty).Trim();
            var turns = ConversationContextStore.GetRecentTurns(seller, buyer, question, 16)
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Text))
                .OrderBy(x => x.Timestamp)
                .ToList();
            var dependency = CalculateContextDependency(question, turns);
            var candidates = RetrieveCandidates(question, turns);
            var plan = new SmartReplyPlan
            {
                ContextDependencyScore = dependency,
                Candidates = candidates.Take(PromptCandidateCount).ToList(),
                RecentTurns = turns.Skip(Math.Max(0, turns.Count - RecentPromptTurns)).ToList(),
                ContextDigest = BuildContextDigest(turns),
                Route = SmartReplyRouteKind.AiGeneral,
                Reason = "未找到足够可靠的本地知识，交给AI结合上下文处理"
            };

            var best = plan.BestCandidate;
            if (best == null) return plan;

            var second = plan.Candidates.Count > 1 ? plan.Candidates[1] : null;
            var margin = second == null ? best.FinalScore : best.FinalScore - second.FinalScore;
            if (CanDirectReply(question, best, dependency, margin))
            {
                plan.Route = SmartReplyRouteKind.DirectKnowledge;
                plan.Reason = "独立问题且知识命中唯一、确定，可直接本地回复";
                return plan;
            }

            if (best.FinalScore >= 0.58 || best.RetrievalScore >= 0.72)
            {
                plan.Route = SmartReplyRouteKind.ContextualKnowledge;
                plan.Reason = dependency >= 0.35
                    ? "当前消息明显依赖上下文，本地知识仅作为事实依据"
                    : "知识相关但不满足直接发送条件，交给AI结合上下文重写";
            }
            return plan;
        }

        public static bool CanUseOfflineKnowledgeFallback(SmartReplyPlan plan)
        {
            var best = plan == null ? null : plan.BestCandidate;
            return best != null
                && plan.ContextDependencyScore < 0.28
                && best.FinalScore >= 0.90
                && !IsUnsafeDirectAnswer(best.Entry == null ? string.Empty : best.Entry.Answer);
        }

        public static string BuildPromptAddon(SmartReplyPlan plan)
        {
            if (plan == null || plan.Candidates == null || plan.Candidates.Count == 0) return string.Empty;
            var sb = new StringBuilder();
            sb.Append("\n\n【智能知识路由】\n")
                .Append("当前路由：").Append(RouteName(plan.Route)).Append("。")
                .Append("上下文依赖度：").Append(plan.ContextDependencyScore.ToString("0.00")).Append("。")
                .Append("这些知识是候选事实依据，不是必须原样发送的固定答案。")
                .Append("必须先理解当前买家消息和最近对话，只采用真正相关的知识；候选之间冲突或与上下文不符时应忽略不相关候选。")
                .Append("不得编造候选知识和店铺固定提示词之外的价格、库存、订单状态、服务范围或售后承诺。")
                .Append("回复应像真人客服自然承接上下文，避免机械重复上一条回复。\n");

            for (var i = 0; i < plan.Candidates.Count; i++)
            {
                var item = plan.Candidates[i].Entry;
                if (item == null) continue;
                sb.Append("候选知识").Append(i + 1).Append("：\n")
                    .Append("问题：").Append(Safe(item.Title, 260)).Append("\n")
                    .Append("答案：").Append(Safe(item.Answer, 900)).Append("\n");
                if (!string.IsNullOrWhiteSpace(item.Category))
                {
                    sb.Append("分类：").Append(Safe(item.Category, 100)).Append("\n");
                }
                if (!string.IsNullOrWhiteSpace(item.Keywords))
                {
                    sb.Append("关键词：").Append(Safe(item.Keywords, 240)).Append("\n");
                }
            }
            return sb.ToString();
        }

        private static List<SmartKnowledgeCandidate> RetrieveCandidates(
            string question,
            List<ConversationContextTurn> turns)
        {
            var knowledge = BotFeatureStore.GetKnowledgeBase() ?? new List<KnowledgeBaseEntry>();
            var query = KnowledgeAiService.NormalizeQuestion(question);
            if (query.Length == 0) return new List<SmartKnowledgeCandidate>();
            var queryIntent = DetectIntent(question);
            var context = string.Join(" ", (turns ?? new List<ConversationContextTurn>())
                .Skip(Math.Max(0, (turns == null ? 0 : turns.Count) - 5))
                .Select(x => x.Text ?? string.Empty));

            var pool = knowledge
                .Where(x => x != null && x.Enabled && !string.IsNullOrWhiteSpace(x.Answer))
                .Select(x => ScoreCandidate(x, query, question, queryIntent))
                .Where(x => x.RetrievalScore >= 0.24)
                .OrderByDescending(x => x.RetrievalScore)
                .Take(RetrievalPoolSize)
                .ToList();

            foreach (var candidate in pool)
            {
                var itemText = (candidate.Entry.Title ?? string.Empty) + " "
                    + (candidate.Entry.Keywords ?? string.Empty) + " "
                    + (candidate.Entry.Category ?? string.Empty);
                var contextScore = TextSimilarity(context, itemText);
                var itemIntent = DetectIntent(candidate.Entry.Title + " " + candidate.Entry.Keywords);
                var intentAdjustment = 0.0;
                if (queryIntent.Length > 0 && itemIntent.Length > 0)
                {
                    intentAdjustment = queryIntent == itemIntent ? 0.07 : -0.08;
                }
                candidate.FinalScore = Clamp(candidate.RetrievalScore * 0.88 + contextScore * 0.12 + intentAdjustment);
                candidate.Intent = itemIntent;
            }

            return pool.OrderByDescending(x => x.FinalScore).ToList();
        }

        private static SmartKnowledgeCandidate ScoreCandidate(
            KnowledgeBaseEntry item,
            string normalizedQuery,
            string originalQuestion,
            string queryIntent)
        {
            var title = KnowledgeAiService.NormalizeQuestion(item.Title);
            var exact = normalizedQuery == title && title.Length > 0;
            double score;
            if (exact)
            {
                score = 1.0;
            }
            else if (Math.Min(normalizedQuery.Length, title.Length) >= 4
                && (normalizedQuery.Contains(title) || title.Contains(normalizedQuery)))
            {
                score = 0.91;
            }
            else
            {
                score = TextSimilarity(normalizedQuery, title) * 0.76;
            }

            var keywordBest = 0.0;
            foreach (var keyword in SplitKeywords(item.Keywords))
            {
                var normalizedKeyword = KnowledgeAiService.NormalizeQuestion(keyword);
                if (normalizedKeyword.Length < 2) continue;
                if (normalizedQuery.Contains(normalizedKeyword)) keywordBest = Math.Max(keywordBest, 0.18);
                else keywordBest = Math.Max(keywordBest, TextSimilarity(normalizedQuery, normalizedKeyword) * 0.12);
            }
            score = Math.Min(1.0, score + keywordBest);

            var titleIntent = DetectIntent(item.Title + " " + item.Keywords);
            if (queryIntent.Length > 0 && titleIntent.Length > 0 && queryIntent != titleIntent)
            {
                score -= 0.08;
            }

            return new SmartKnowledgeCandidate
            {
                Entry = item,
                RetrievalScore = Clamp(score),
                FinalScore = Clamp(score),
                ExactQuestionMatch = exact,
                Intent = titleIntent
            };
        }

        private static double CalculateContextDependency(string question, List<ConversationContextTurn> turns)
        {
            var compact = Compact(question);
            if (compact.Length == 0) return 0;
            var score = 0.0;
            if (compact.Length <= 6) score += 0.24;
            else if (compact.Length <= 12) score += 0.12;

            if (ContextCues.Any(x => compact.Contains(Compact(x)))) score += 0.38;
            if (Regex.IsMatch(compact, @"^(那|这个|那个|它|这样|还是|所以|然后|为啥|为什么|怎么不)")) score += 0.24;
            if (Regex.IsMatch(compact, @"^(有|没有|是|不是|可以|不可以|能|不能|支持|不支持|好的|好|嗯|对|不对)$")) score += 0.42;

            var previousAssistant = (turns ?? new List<ConversationContextTurn>())
                .Where(x => x.Role == "assistant" && !string.IsNullOrWhiteSpace(x.Text))
                .OrderByDescending(x => x.Timestamp)
                .FirstOrDefault();
            if (previousAssistant != null
                && (previousAssistant.Timestamp == DateTime.MinValue || previousAssistant.Timestamp >= DateTime.Now.AddMinutes(-20)))
            {
                if (compact.Length <= 16 || ContextCues.Any(x => compact.Contains(Compact(x)))) score += 0.16;
                if (SharedBigramCount(compact, Compact(previousAssistant.Text)) > 0) score += 0.08;
            }

            if (compact.Length >= 10
                && Regex.IsMatch(compact, @"怎么|如何|能不能|是否|支持|多少钱|价格|多久|什么时候|哪里|为什么"))
            {
                score -= 0.12;
            }
            return Clamp(score);
        }

        private static bool CanDirectReply(
            string question,
            SmartKnowledgeCandidate best,
            double dependency,
            double margin)
        {
            if (best == null || best.Entry == null) return false;
            if (dependency > 0.20) return false;
            if (Compact(question).Length < 4) return false;
            if (ContextCues.Any(x => Compact(question).Contains(Compact(x)))) return false;
            if (IsUnsafeDirectAnswer(best.Entry.Answer)) return false;
            if (best.ExactQuestionMatch && best.FinalScore >= 0.95) return true;
            return best.FinalScore >= 0.985 && margin >= 0.12;
        }

        private static bool IsUnsafeDirectAnswer(string answer)
        {
            answer = answer ?? string.Empty;
            if (ConditionalAnswerCues.Any(x => answer.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0)) return true;
            return HighRiskTerms.Any(x => answer.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string BuildContextDigest(List<ConversationContextTurn> turns)
        {
            if (turns == null || turns.Count <= RecentPromptTurns) return string.Empty;
            var older = turns.Take(Math.Max(0, turns.Count - RecentPromptTurns)).ToList();
            var sb = new StringBuilder();
            foreach (var turn in older)
            {
                if (turn == null || string.IsNullOrWhiteSpace(turn.Text)) continue;
                var speaker = turn.Role == "assistant" ? "客服" : (turn.Role == "user" ? "买家" : "系统");
                var line = speaker + "：" + Safe(turn.Text, 180);
                if (sb.Length + line.Length > 900) break;
                if (sb.Length > 0) sb.Append("；");
                sb.Append(line);
            }
            return sb.ToString();
        }

        private static string DetectIntent(string text)
        {
            var value = Compact(text);
            if (value.Length == 0) return string.Empty;
            if (Regex.IsMatch(value, @"退款|退货|售后|赔偿|投诉")) return "after_sale";
            if (Regex.IsMatch(value, @"多少钱|价格|费用|收费")) return "price";
            if (Regex.IsMatch(value, @"多久|什么时候|几天|时效")) return "time";
            if (Regex.IsMatch(value, @"怎么|如何|步骤|操作|登录|绑定|充值方法")) return "how_to";
            if (Regex.IsMatch(value, @"为什么|失败|不行|报错|异常|不能用")) return "troubleshoot";
            if (Regex.IsMatch(value, @"可以|能不能|能否|是否|支持|可不可以")) return "capability";
            if (Regex.IsMatch(value, @"需要什么|提供什么|要什么|资料|条件")) return "requirement";
            return string.Empty;
        }

        private static IEnumerable<string> SplitKeywords(string value)
        {
            return (value ?? string.Empty)
                .Split(new[] { ',', '，', ';', '；', '|', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim());
        }

        private static double TextSimilarity(string left, string right)
        {
            var a = Bigrams(Compact(left));
            var b = Bigrams(Compact(right));
            if (a.Count == 0 || b.Count == 0) return 0;
            var common = a.Intersect(b).Count();
            return (2.0 * common) / (a.Count + b.Count);
        }

        private static int SharedBigramCount(string left, string right)
        {
            return Bigrams(left).Intersect(Bigrams(right)).Count();
        }

        private static HashSet<string> Bigrams(string value)
        {
            var compact = Compact(value);
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i + 1 < compact.Length; i++) set.Add(compact.Substring(i, 2));
            return set;
        }

        private static string Compact(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"[\s，。！？、；：,.!?:;\-—_()（）\[\]【】]+", string.Empty);
        }

        private static string Safe(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }

        private static double Clamp(double value)
        {
            return Math.Max(0, Math.Min(1, value));
        }

        private static string RouteName(SmartReplyRouteKind route)
        {
            if (route == SmartReplyRouteKind.DirectKnowledge) return "DIRECT_KNOWLEDGE";
            if (route == SmartReplyRouteKind.ContextualKnowledge) return "CONTEXTUAL_KNOWLEDGE";
            return "AI_GENERAL";
        }
    }
}
