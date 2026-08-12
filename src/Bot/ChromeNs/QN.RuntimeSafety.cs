using Bot.Knowledge;
using Bot.ShopScope;
using BotLib;
using System;
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
        private const string SettingsScope = "feature";
        private const string EnabledKey = "FirstInquiryFixedReplyEnabled";
        private const string AnswerKey = "FirstInquiryFixedReplyAnswer";

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
                    if (latestPrior.Timestamp >= DateTime.Now.AddMinutes(-SessionResetMinutes)) return string.Empty;
                }

                return BotFeatureStore.ApplyOutputPolicy(settings.Answer.Trim()) ?? string.Empty;
            });

            answer = (resolved ?? string.Empty).Trim();
            return !string.IsNullOrWhiteSpace(answer);
        }

        private static FirstInquiryFixedReplySettings LoadCurrentScope()
        {
            var enabledText = BotLib.Db.Sqlite.PersistentParams.GetParam2Key(
                EnabledKey,
                SettingsScope,
                "false");
            var answer = BotLib.Db.Sqlite.PersistentParams.GetParam2Key(
                AnswerKey,
                SettingsScope,
                string.Empty);
            return new FirstInquiryFixedReplySettings
            {
                Enabled = string.Equals(enabledText, "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(enabledText, "1", StringComparison.OrdinalIgnoreCase),
                Answer = answer ?? string.Empty
            };
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
