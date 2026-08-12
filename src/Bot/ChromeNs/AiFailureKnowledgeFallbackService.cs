using Bot.Knowledge;
using Bot.ShopScope;
using BotLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Bot.ChromeNs
{
    internal static class AiFailureKnowledgeFallbackService
    {
        internal const double MinimumFallbackScore = 0.50;

        public static bool TryResolve(
            string seller,
            string buyer,
            string question,
            string aiError,
            out string answer,
            out KnowledgeBaseEntry knowledge,
            out double score)
        {
            answer = string.Empty;
            knowledge = null;
            score = 0;

            ShopContext shop = null;
            if (ShopSettingsScope.Current == null && !string.IsNullOrWhiteSpace(seller))
            {
                try { shop = ShopContextLocator.ResolveRuntimeBySellerNick(seller); }
                catch { shop = null; }
            }

            if (shop != null)
            {
                using (ShopSettingsScope.Enter(shop))
                {
                    return TryResolveCore(seller, buyer, question, aiError, out answer, out knowledge, out score);
                }
            }
            return TryResolveCore(seller, buyer, question, aiError, out answer, out knowledge, out score);
        }

        private static bool TryResolveCore(
            string seller,
            string buyer,
            string question,
            string aiError,
            out string answer,
            out KnowledgeBaseEntry knowledge,
            out double score)
        {
            answer = string.Empty;
            knowledge = null;
            score = 0;
            question = (question ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(question)) return false;
            if (!IsAuthenticatedControlPlaneUpstreamFailure(aiError)) return false;

            var policy = BotFeatureStore.GetMessagePolicy();
            if (policy == null || !policy.EnableKnowledgeBase)
            {
                Log.Info("AI上游异常，但本店知识库未启用，不能执行50%本地兜底。seller=" + seller + ", buyer=" + buyer);
                return false;
            }

            ConversationContextTurn latestAgentPrompt = null;
            if (IsShortContextReply(question))
            {
                var turns = ConversationContextStore.GetRecentTurns(seller, buyer, question, 8);
                latestAgentPrompt = turns.LastOrDefault(x => x != null
                    && x.Role == "assistant"
                    && !string.IsNullOrWhiteSpace(x.Text));
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
                    knowledge = item;
                }
            }

            if (knowledge == null || score < MinimumFallbackScore)
            {
                Log.Info("AI上游异常且Bot令牌已通过服务端鉴权，但本店知识库最高匹配不足50%，不自动发送。seller="
                    + seller + ", buyer=" + buyer + ", bestScore=" + score.ToString("0.00"));
                return false;
            }

            answer = BotFeatureStore.ApplyOutputPolicy(knowledge.Answer);
            if (string.IsNullOrWhiteSpace(answer)
                || ConversationContextStore.IsWithdrawnAnswer(seller, buyer, answer))
            {
                Log.Info("AI上游异常命中本店知识库，但答案为空或已被客服撤回，已阻止50%兜底发送。seller="
                    + seller + ", buyer=" + buyer + ", knowledgeId=" + knowledge.Id);
                answer = string.Empty;
                return false;
            }

            Log.Info("AI上游异常且Bot令牌已通过服务端鉴权，启用本店知识库50%兜底。seller="
                + seller + ", buyer=" + buyer + ", knowledgeId=" + knowledge.Id
                + ", score=" + score.ToString("0.00"));
            return true;
        }

        private static bool IsAuthenticatedControlPlaneUpstreamFailure(string aiError)
        {
            aiError = (aiError ?? string.Empty).Trim();
            if (!aiError.StartsWith("错误：AI接口调用失败", StringComparison.Ordinal)) return false;
            if (aiError.IndexOf("HTTP 502", StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (aiError.IndexOf("upstream_exhausted", StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (aiError.IndexOf("所有供应商、模型和请求协议均调用失败", StringComparison.Ordinal) < 0) return false;

            // Control Plane 的 /v1/chat/completions 先执行 Bearer 客户端令牌鉴权，
            // 只有鉴权通过后才会进入上游供应商路由，并以 502 + upstream_exhausted
            // 表示“服务端可达、Bot令牌有效，但所有AI上游均失败”。
            var controlPlaneEndpoints = AiEndpointStore.GetEnabledEndpoints()
                .Where(x => x != null
                    && string.Equals(x.Type, "服务端控制面", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (controlPlaneEndpoints.Count < 1) return false;

            return controlPlaneEndpoints.Any(endpoint =>
                string.IsNullOrWhiteSpace(endpoint.Name)
                || aiError.IndexOf(endpoint.Name + "：", StringComparison.OrdinalIgnoreCase) >= 0);
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
            if (Math.Min(q.Length, title.Length) >= 4 && (q.Contains(title) || title.Contains(q)))
                return contextOnly ? 0.87 : 0.95;
            foreach (var keyword in SplitKeywords(item.Keywords))
            {
                var normalizedKeyword = KnowledgeAiService.NormalizeQuestion(keyword);
                if (normalizedKeyword.Length >= 2 && q.Contains(normalizedKeyword))
                    return contextOnly ? 0.85 : 0.90;
            }
            var similarity = BigramSimilarity(q, title);
            if (similarity >= 0.68) return contextOnly ? 0.84 : 0.86;
            return similarity * 0.75;
        }

        private static IEnumerable<string> SplitKeywords(string value)
        {
            return (value ?? string.Empty)
                .Split(new[] { ',', '，', ';', '；', '|', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
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

        private static string Normalize(string value)
        {
            return KnowledgeAiService.NormalizeQuestion(value ?? string.Empty);
        }
    }
}
