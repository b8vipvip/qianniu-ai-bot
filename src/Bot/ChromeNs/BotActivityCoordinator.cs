using Bot.Options;
using BotLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Bot.ChromeNs
{
    internal sealed class BotActivityLease : IDisposable
    {
        private readonly long _id;
        private int _disposed;

        internal BotActivityLease(long id)
        {
            _id = id;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            BotActivityCoordinator.End(_id);
        }
    }

    internal sealed class BotActivitySnapshot
    {
        public int ActiveCount { get; set; }
        public DateTime LastHumanInteractionAt { get; set; }
        public string LastHumanInteractionReason { get; set; }
        public string BusyReason { get; set; }
    }

    internal static class BotActivityCoordinator
    {
        private sealed class ActivityRecord
        {
            public long Id;
            public string Kind;
            public string Seller;
            public string Buyer;
            public DateTime StartedAt;
        }

        private sealed class HumanRecord
        {
            public DateTime At;
            public string Reason;
        }

        private static long _nextId;
        private static readonly ConcurrentDictionary<long, ActivityRecord> Activities =
            new ConcurrentDictionary<long, ActivityRecord>();
        private static readonly ConcurrentDictionary<string, HumanRecord> HumanInteractions =
            new ConcurrentDictionary<string, HumanRecord>(StringComparer.OrdinalIgnoreCase);

        public static BotActivityLease Begin(string kind, string seller, string buyer)
        {
            var id = Interlocked.Increment(ref _nextId);
            Activities[id] = new ActivityRecord
            {
                Id = id,
                Kind = (kind ?? string.Empty).Trim(),
                Seller = Normalize(seller),
                Buyer = Normalize(buyer),
                StartedAt = DateTime.Now
            };
            return new BotActivityLease(id);
        }

        internal static void End(long id)
        {
            ActivityRecord ignored;
            Activities.TryRemove(id, out ignored);
        }

        public static void MarkHumanInteraction(string seller, string reason)
        {
            seller = Normalize(seller);
            if (seller.Length == 0) return;
            HumanInteractions[seller] = new HumanRecord
            {
                At = DateTime.Now,
                Reason = (reason ?? string.Empty).Trim()
            };
            Log.Info("已记录人工操作保护: seller=" + seller + ", reason=" + (reason ?? string.Empty));
        }

        public static bool IsSafeToAutoFocus(string seller, out string reason)
        {
            seller = Normalize(seller);
            var active = Activities.Values
                .Where(x => x != null && (x.Seller.Length == 0 || x.Seller == seller))
                .OrderBy(x => x.StartedAt)
                .ToList();
            if (active.Count > 0)
            {
                reason = "Bot当前有任务：" + string.Join("、", active.Select(x => x.Kind).Distinct().Take(3));
                return false;
            }

            HumanRecord human;
            var protectSeconds = OrderAttentionSettings.GetHumanProtectionSeconds();
            if (HumanInteractions.TryGetValue(seller, out human)
                && human != null
                && DateTime.Now - human.At < TimeSpan.FromSeconds(protectSeconds))
            {
                var remain = Math.Max(1, protectSeconds - (int)(DateTime.Now - human.At).TotalSeconds);
                reason = "人工操作保护中（约" + remain + "秒）：" + human.Reason;
                return false;
            }

            reason = string.Empty;
            return true;
        }

        public static BotActivitySnapshot GetSnapshot(string seller)
        {
            seller = Normalize(seller);
            HumanRecord human;
            HumanInteractions.TryGetValue(seller, out human);
            var active = Activities.Values
                .Where(x => x != null && (x.Seller.Length == 0 || x.Seller == seller))
                .OrderBy(x => x.StartedAt)
                .ToList();
            return new BotActivitySnapshot
            {
                ActiveCount = active.Count,
                LastHumanInteractionAt = human == null ? DateTime.MinValue : human.At,
                LastHumanInteractionReason = human == null ? string.Empty : human.Reason,
                BusyReason = active.Count == 0
                    ? string.Empty
                    : string.Join("、", active.Select(x => x.Kind).Distinct().Take(3))
            };
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim().ToLowerInvariant();
        }
    }
}
