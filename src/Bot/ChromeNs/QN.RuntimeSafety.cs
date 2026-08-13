using Bot.Knowledge;
using Bot.ShopScope;
using BotLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Bot.ChromeNs
{
    internal sealed class FirstInquiryFixedReplySettings
    {
        public bool Enabled { get; set; }
        public string Answer { get; set; }
    }

    internal static class FirstInquiryFixedReplyService
    {
        internal const int SessionResetMinutes = 30;
        internal const string DefaultAnswer = "在的，亲！";
        private const int PendingReplySeconds = 15;
        private const string SettingsScope = "feature";
        private const string EnabledKey = "FirstInquiryFixedReplyEnabled";
        private const string AnswerKey = "FirstInquiryFixedReplyAnswer";

        private sealed class PendingReply
        {
            public string Answer { get; set; }
            public DateTime ExpiresAt { get; set; }
        }

        private static readonly ConcurrentDictionary<string, PendingReply> PendingReplies =
            new ConcurrentDictionary<string, PendingReply>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, DateTime> TriggeredAt =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);

        public static FirstInquiryFixedReplySettings Load(string seller)
        {
            return RunInShopScope(seller, LoadCurrentScope);
        }

        public static void Save(string seller, bool enabled, string answer)
        {
            RunInShopScope(seller, delegate
            {
                BotLib.Db.Sqlite.PersistentParams.TrySaveParam2Key(
                    EnabledKey,
                    SettingsScope,
                    enabled ? "true" : "false");
                BotLib.Db.Sqlite.PersistentParams.TrySaveParam2Key(
                    AnswerKey,
                    SettingsScope,
                    (answer ?? string.Empty).Trim());
                return true;
            });
        }

        /// <summary>
        /// Prepares the first-inquiry fixed reply before the ordinary text / vision / skip routing.
        /// This lets any fresh buyer-side message, including media and platform system tips, become
        /// the first consultation trigger without accidentally sending a historical startup message.
        /// </summary>
        public static bool TryPrepare(
            string seller,
            string buyer,
            string currentQuestion,
            IncomingMessageDecision decision,
            out string answer)
        {
            answer = string.Empty;
            if (string.IsNullOrWhiteSpace(seller)
                || string.IsNullOrWhiteSpace(buyer)
                || string.IsNullOrWhiteSpace(currentQuestion)
                || !IsEligibleTrigger(decision)) return false;

            var resolved = RunInShopScope(seller, delegate
            {
                var now = DateTime.Now;
                var key = RuntimeKey(seller, buyer);
                CleanupRuntimeState(key, now);

                DateTime triggered;
                if (TriggeredAt.TryGetValue(key, out triggered)
                    && triggered >= now.AddMinutes(-SessionResetMinutes))
                {
                    return string.Empty;
                }

                PendingReply existing;
                if (PendingReplies.TryGetValue(key, out existing)
                    && existing != null
                    && existing.ExpiresAt >= now
                    && !string.IsNullOrWhiteSpace(existing.Answer))
                {
                    return existing.Answer;
                }

                var candidate = ResolveFreshCurrentScope(seller, buyer, currentQuestion, now);
                if (string.IsNullOrWhiteSpace(candidate)) return string.Empty;

                var pending = new PendingReply
                {
                    Answer = candidate,
                    ExpiresAt = now.AddSeconds(PendingReplySeconds)
                };
                PendingReplies.AddOrUpdate(
                    key,
                    pending,
                    (ignored, old) => old != null && old.ExpiresAt >= now ? old : pending);

                PendingReply stored;
                return PendingReplies.TryGetValue(key, out stored) && stored != null
                    ? (stored.Answer ?? string.Empty)
                    : candidate;
            });

            answer = (resolved ?? string.Empty).Trim();
            return !string.IsNullOrWhiteSpace(answer);
        }

        public static bool TryResolve(
            string seller,
            string buyer,
            string currentQuestion,
            out string answer)
        {
            answer = string.Empty;
            if (string.IsNullOrWhiteSpace(seller)
                || string.IsNullOrWhiteSpace(buyer)
                || string.IsNullOrWhiteSpace(currentQuestion)) return false;

            var resolved = RunInShopScope(seller, delegate
            {
                var now = DateTime.Now;
                var key = RuntimeKey(seller, buyer);
                CleanupRuntimeState(key, now);

                PendingReply pending;
                if (PendingReplies.TryRemove(key, out pending)
                    && pending != null
                    && pending.ExpiresAt >= now
                    && !string.IsNullOrWhiteSpace(pending.Answer))
                {
                    TriggeredAt[key] = now;
                    return pending.Answer;
                }

                DateTime triggered;
                if (TriggeredAt.TryGetValue(key, out triggered)
                    && triggered >= now.AddMinutes(-SessionResetMinutes))
                {
                    return string.Empty;
                }

                var candidate = ResolveFreshCurrentScope(seller, buyer, currentQuestion, now);
                if (!string.IsNullOrWhiteSpace(candidate)) TriggeredAt[key] = now;
                return candidate;
            });

            answer = (resolved ?? string.Empty).Trim();
            return !string.IsNullOrWhiteSpace(answer);
        }

        public static bool HasPending(string seller, string buyer)
        {
            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(buyer)) return false;
            var key = RuntimeKey(seller, buyer);
            PendingReply pending;
            if (!PendingReplies.TryGetValue(key, out pending) || pending == null) return false;
            if (pending.ExpiresAt >= DateTime.Now && !string.IsNullOrWhiteSpace(pending.Answer)) return true;
            PendingReply ignored;
            PendingReplies.TryRemove(key, out ignored);
            return false;
        }

        private static bool IsEligibleTrigger(IncomingMessageDecision decision)
        {
            if (decision == null) return false;
            // Never answer stale messages loaded when Bot starts. A recharge-progress event has
            // already been claimed by its dedicated handler before this ordinary reply pipeline.
            if (string.Equals(decision.MessageLabel, "历史消息", StringComparison.Ordinal)) return false;
            if (string.Equals(decision.MessageLabel, "[充值进度查询]", StringComparison.Ordinal)) return false;
            return true;
        }

        private static string ResolveFreshCurrentScope(
            string seller,
            string buyer,
            string currentQuestion,
            DateTime now)
        {
            var settings = LoadCurrentScope();
            if (settings == null
                || !settings.Enabled
                || string.IsNullOrWhiteSpace(settings.Answer)) return string.Empty;

            var priorTurns = ConversationContextStore.GetRecentTurns(
                seller,
                buyer,
                currentQuestion,
                24);
            var latestPrior = priorTurns
                .Where(x => x != null
                    && !x.Withdrawn
                    && !string.IsNullOrWhiteSpace(x.Text))
                .OrderByDescending(x => x.Timestamp)
                .FirstOrDefault();

            if (latestPrior != null)
            {
                // 时间未知时宁可不重复欢迎，也不把历史会话误判为首条咨询。
                if (latestPrior.Timestamp == DateTime.MinValue) return string.Empty;
                if (latestPrior.Timestamp >= now.AddMinutes(-SessionResetMinutes)) return string.Empty;
            }

            return BotFeatureStore.ApplyOutputPolicy(settings.Answer.Trim()) ?? string.Empty;
        }

        private static FirstInquiryFixedReplySettings LoadCurrentScope()
        {
            var enabledText = BotLib.Db.Sqlite.PersistentParams.GetParam2Key(
                EnabledKey,
                SettingsScope,
                "true");
            var answer = BotLib.Db.Sqlite.PersistentParams.GetParam2Key(
                AnswerKey,
                SettingsScope,
                DefaultAnswer);
            return new FirstInquiryFixedReplySettings
            {
                Enabled = string.Equals(enabledText, "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(enabledText, "1", StringComparison.OrdinalIgnoreCase),
                Answer = string.IsNullOrWhiteSpace(answer) ? string.Empty : answer
            };
        }

        private static void CleanupRuntimeState(string key, DateTime now)
        {
            PendingReply pending;
            if (PendingReplies.TryGetValue(key, out pending)
                && (pending == null || pending.ExpiresAt < now))
            {
                PendingReply ignored;
                PendingReplies.TryRemove(key, out ignored);
            }

            DateTime triggered;
            if (TriggeredAt.TryGetValue(key, out triggered)
                && triggered < now.AddMinutes(-SessionResetMinutes))
            {
                DateTime ignored;
                TriggeredAt.TryRemove(key, out ignored);
            }
        }

        private static string RuntimeKey(string seller, string buyer)
        {
            return (seller ?? string.Empty).Trim() + "#" + (buyer ?? string.Empty).Trim();
        }

        private static T RunInShopScope<T>(string seller, Func<T> action)
        {
            if (action == null) return default(T);
            if (ShopSettingsScope.Current != null) return action();

            ShopContext shop = null;
            try { shop = ShopContextLocator.ResolveRuntimeBySellerNick(seller); }
            catch
            {
                try { shop = ShopContextLocator.ResolveBySellerNick(seller); }
                catch { shop = null; }
            }

            if (shop == null) return action();
            using (ShopSettingsScope.Enter(shop))
            {
                return action();
            }
        }
    }

    public partial class QN
    {
        internal static List<QN> GetRuntimeSafetySnapshot()
        {
            lock (QNSetLock)
            {
                return QNSet == null ? new List<QN>() : QNSet.Where(x => x != null).ToList();
            }
        }

        internal void CancelActiveBuyerGeneration(string seller, string buyer, string reason)
        {
            if (_buyerMessageBurstCoordinator == null) return;
            _buyerMessageBurstCoordinator.CancelBuyer(seller, buyer, reason);
        }

        internal bool HasBuyerMessageAfter(string seller, string buyer, DateTime threshold)
        {
            DateTime observedAt;
            return _latestBuyerMessageObserved.TryGetValue(RecoveryKey(seller, buyer), out observedAt)
                && observedAt > threshold.AddMilliseconds(5);
        }
    }
}
