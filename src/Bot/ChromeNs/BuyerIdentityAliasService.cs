using Bot.ChatRecord;
using Bot.ShopScope;
using System;
using System.Collections.Concurrent;

namespace Bot.ChromeNs
{
    /// <summary>
    /// 千牛同一个买家在不同事件中可能分别使用内部 nick 与界面 display。
    /// 别名记录同时绑定 ShopKey，避免同名客服或买家在不同店铺互相覆盖。
    /// </summary>
    internal static class BuyerIdentityAliasService
    {
        private sealed class AliasRecord
        {
            public string ShopKey;
            public string Seller;
            public string InternalNick;
            public string Display;
            public string TargetId;
            public DateTime UpdatedAt;
        }

        private static readonly ConcurrentDictionary<string, AliasRecord> Aliases =
            new ConcurrentDictionary<string, AliasRecord>(StringComparer.OrdinalIgnoreCase);

        public static void ObserveMessage(string seller, QNChatMessage message)
        {
            if (message == null || message.fromid == null || message.toid == null) return;
            seller = Clean(seller);
            if (seller.Length == 0) return;

            var from = Clean(message.fromid.nick);
            var to = Clean(message.toid.nick);
            if (!Same(from, seller) && Same(to, seller))
                Observe(seller, from, message.fromid.display, message.fromid.targetId);
            else if (Same(from, seller) && !Same(to, seller))
                Observe(seller, to, string.Empty, message.toid.targetId);
        }

        public static void Observe(string seller, string internalNick, string display, string targetId)
        {
            seller = Clean(seller);
            internalNick = Clean(internalNick);
            display = Clean(display);
            targetId = Clean(targetId);
            if (seller.Length == 0 || internalNick.Length == 0 || Same(internalNick, seller)) return;
            if (Same(display, seller)) display = string.Empty;

            AliasRecord old;
            Aliases.TryGetValue(Key(seller, internalNick), out old);
            if (old == null && display.Length > 0) Aliases.TryGetValue(Key(seller, display), out old);
            var record = old ?? new AliasRecord();
            record.ShopKey = ScopeKey(seller);
            record.Seller = seller;
            record.InternalNick = internalNick;
            if (display.Length > 0) record.Display = display;
            if (targetId.Length > 0) record.TargetId = targetId;
            record.UpdatedAt = DateTime.Now;

            Aliases[Key(seller, internalNick)] = record;
            if (!string.IsNullOrWhiteSpace(record.Display)) Aliases[Key(seller, record.Display)] = record;
            if (!string.IsNullOrWhiteSpace(record.TargetId)) Aliases[Key(seller, record.TargetId)] = record;
            Cleanup();
        }

        public static string ResolveConversationKey(string seller, string value)
        {
            var record = Find(seller, value);
            if (record == null) return Clean(value);
            return !string.IsNullOrWhiteSpace(record.Display) ? record.Display : record.InternalNick;
        }

        public static string ResolveInternalNick(string seller, string value)
        {
            var record = Find(seller, value);
            return record == null || string.IsNullOrWhiteSpace(record.InternalNick)
                ? Clean(value)
                : record.InternalNick;
        }

        public static string ResolveDisplay(string seller, string value)
        {
            var record = Find(seller, value);
            return record == null || string.IsNullOrWhiteSpace(record.Display)
                ? Clean(value)
                : record.Display;
        }

        public static bool AreEquivalent(string seller, string left, string right)
        {
            left = Clean(left);
            right = Clean(right);
            if (left.Length == 0 || right.Length == 0) return false;
            if (Same(left, right)) return true;
            var a = Find(seller, left);
            var b = Find(seller, right);
            if (a == null || b == null) return false;
            return ReferenceEquals(a, b)
                || Same(a.InternalNick, b.InternalNick)
                || (!string.IsNullOrWhiteSpace(a.TargetId) && Same(a.TargetId, b.TargetId));
        }

        private static AliasRecord Find(string seller, string value)
        {
            AliasRecord record;
            return Aliases.TryGetValue(Key(Clean(seller), Clean(value)), out record) ? record : null;
        }

        private static string Key(string seller, string alias)
        {
            return ScopeKey(seller) + "#" + Clean(seller) + "#" + Clean(alias);
        }

        private static string ScopeKey(string seller)
        {
            var current = ShopSettingsScope.Current;
            if (current != null) return current.ShopKey;
            try { return ShopContextLocator.ResolveRuntimeBySellerNick(seller).ShopKey; }
            catch { return "legacy-" + Clean(seller).ToLowerInvariant(); }
        }

        private static bool Same(string left, string right)
        {
            return string.Equals(Clean(left), Clean(right), StringComparison.OrdinalIgnoreCase);
        }

        private static string Clean(string value)
        {
            return (value ?? string.Empty).Trim();
        }

        private static void Cleanup()
        {
            if (Aliases.Count < 5000) return;
            var cutoff = DateTime.Now.AddDays(-30);
            foreach (var pair in Aliases)
            {
                if (pair.Value != null && pair.Value.UpdatedAt >= cutoff) continue;
                AliasRecord ignored;
                Aliases.TryRemove(pair.Key, out ignored);
            }
        }
    }
}