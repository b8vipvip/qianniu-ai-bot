using Bot.ChatRecord;
using BotLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal sealed class BuyerMessageBurstItem
    {
        public string SellerNick { get; set; }
        public string BuyerNick { get; set; }
        public string MessageKey { get; set; }
        public string DisplayText { get; set; }
        public QNChatMessage Message { get; set; }
        public IncomingMessageDecision SafetyDecision { get; set; }
        public VisionMessageDecision VisionDecision { get; set; }
        public long SortValue { get; set; }
        public DateTime ReceivedAt { get; set; }

        public BuyerMessageBurstItem()
        {
            ReceivedAt = DateTime.Now;
        }
    }

    internal sealed class BuyerMessageBurst
    {
        public string SellerNick { get; private set; }
        public string BuyerNick { get; private set; }
        public IList<BuyerMessageBurstItem> Items { get; private set; }
        public string CombinedQuestion { get; private set; }
        public string ModelQuestion { get; private set; }
        public int Version { get; private set; }

        public BuyerMessageBurstItem LatestVisionItem
        {
            get
            {
                return Items.LastOrDefault(
                    x => x != null
                        && x.VisionDecision != null
                        && x.VisionDecision.Kind == VisionDecisionKind.Vision);
            }
        }

        public bool HasReplyableItem
        {
            get
            {
                return Items.Any(
                    x => x != null
                        && x.VisionDecision != null
                        && x.VisionDecision.Kind != VisionDecisionKind.Skip);
            }
        }

        public BuyerMessageBurst(
            string sellerNick,
            string buyerNick,
            IEnumerable<BuyerMessageBurstItem> items,
            int version)
        {
            SellerNick = sellerNick ?? string.Empty;
            BuyerNick = buyerNick ?? string.Empty;
            Version = version;
            Items = (items ?? new BuyerMessageBurstItem[0])
                .Where(x => x != null)
                .OrderBy(x => x.SortValue <= 0 ? x.ReceivedAt.Ticks : x.SortValue)
                .ThenBy(x => x.ReceivedAt)
                .ToList();
            CombinedQuestion = BuildCombinedQuestion(Items);
            ModelQuestion = Items.Count <= 1
                ? CombinedQuestion
                : "【买家本轮连续消息，以下按发送顺序】\n" + CombinedQuestion;
        }

        public static string BuildCombinedQuestion(IEnumerable<BuyerMessageBurstItem> items)
        {
            var parts = new List<string>();
            foreach (var item in (items ?? new BuyerMessageBurstItem[0])
                .Where(x => x != null)
                .OrderBy(x => x.SortValue <= 0 ? x.ReceivedAt.Ticks : x.SortValue)
                .ThenBy(x => x.ReceivedAt))
            {
                var text = NormalizeDisplay(item.DisplayText);
                if (string.IsNullOrWhiteSpace(text)) continue;
                var normalized = NormalizeCompare(text);
                if (parts.Count > 0)
                {
                    var previous = parts[parts.Count - 1];
                    var previousNormalized = NormalizeCompare(previous);
                    if (normalized == previousNormalized) continue;

                    // 输入法上屏、复制修改或网络重发时，后一条可能完整包含前一条短片段。
                    // 此时保留更完整的新文本，避免模型把它误判为两个独立问题。
                    if (previousNormalized.Length <= 16
                        && normalized.Length > previousNormalized.Length
                        && normalized.StartsWith(previousNormalized, StringComparison.Ordinal))
                    {
                        parts[parts.Count - 1] = text;
                        continue;
                    }
                    if (normalized.Length <= 8
                        && previousNormalized.Length > normalized.Length
                        && previousNormalized.EndsWith(normalized, StringComparison.Ordinal))
                    {
                        continue;
                    }
                }
                parts.Add(text);
            }

            if (parts.Count > 10) parts = parts.Skip(parts.Count - 10).ToList();
            var combined = string.Join("\n", parts);
            return combined.Length <= 1600 ? combined : combined.Substring(combined.Length - 1600);
        }

        private static string NormalizeDisplay(string value)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Trim();
            value = Regex.Replace(value, @"[ \t]+", " ");
            value = Regex.Replace(value, @"\n{3,}", "\n\n");
            return value;
        }

        private static string NormalizeCompare(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", string.Empty);
        }
    }

    internal sealed class BuyerMessageBurstLease
    {
        private readonly Func<bool> _isCurrent;

        public BuyerMessageBurst Burst { get; private set; }

        public bool IsCurrent
        {
            get { return _isCurrent != null && _isCurrent(); }
        }

        public BuyerMessageBurstLease(BuyerMessageBurst burst, Func<bool> isCurrent)
        {
            Burst = burst;
            _isCurrent = isCurrent;
        }

        public async Task<bool> ConfirmStableAsync(int milliseconds)
        {
            await Task.Delay(Math.Max(0, milliseconds));
            return IsCurrent;
        }
    }

    internal sealed class BuyerMessageBurstCoordinator
    {
        private sealed class BurstState
        {
            public readonly object Sync = new object();
            public readonly List<BuyerMessageBurstItem> Items = new List<BuyerMessageBurstItem>();
            public CancellationTokenSource DelayCancellation = new CancellationTokenSource();
            public bool WorkerRunning;
            public int Version;
            public DateTime StartedAt = DateTime.MinValue;
        }

        private readonly ConcurrentDictionary<string, BurstState> _states =
            new ConcurrentDictionary<string, BurstState>(StringComparer.Ordinal);
        private readonly Func<BuyerMessageBurstLease, Task> _handler;

        public BuyerMessageBurstCoordinator(Func<BuyerMessageBurstLease, Task> handler)
        {
            if (handler == null) throw new ArgumentNullException("handler");
            _handler = handler;
        }

        public void Enqueue(BuyerMessageBurstItem item)
        {
            if (item == null
                || string.IsNullOrWhiteSpace(item.SellerNick)
                || string.IsNullOrWhiteSpace(item.BuyerNick))
            {
                return;
            }

            var key = Key(item.SellerNick, item.BuyerNick);
            var state = _states.GetOrAdd(key, _ => new BurstState());
            var startWorker = false;
            lock (state.Sync)
            {
                if (!string.IsNullOrWhiteSpace(item.MessageKey)
                    && state.Items.Any(x => string.Equals(x.MessageKey, item.MessageKey, StringComparison.Ordinal)))
                {
                    return;
                }
                if (state.Items.Count == 0) state.StartedAt = DateTime.Now;
                state.Items.Add(item);
                if (state.Items.Count > 12) state.Items.RemoveRange(0, state.Items.Count - 12);
                state.Version++;

                try { state.DelayCancellation.Cancel(); } catch { }
                state.DelayCancellation.Dispose();
                state.DelayCancellation = new CancellationTokenSource();

                if (!state.WorkerRunning)
                {
                    state.WorkerRunning = true;
                    startWorker = true;
                }
            }

            if (startWorker) Task.Run(() => RunAsync(key, state));
        }

        private async Task RunAsync(string key, BurstState state)
        {
            while (true)
            {
                CancellationToken token;
                int capturedVersion;
                int delayMilliseconds;
                lock (state.Sync)
                {
                    token = state.DelayCancellation.Token;
                    capturedVersion = state.Version;
                    delayMilliseconds = QuietDelayMilliseconds(state.Items, state.StartedAt);
                }

                try
                {
                    await Task.Delay(delayMilliseconds, token);
                }
                catch (OperationCanceledException)
                {
                    continue;
                }

                BuyerMessageBurst burst;
                lock (state.Sync)
                {
                    if (capturedVersion != state.Version) continue;
                    burst = new BuyerMessageBurst(
                        state.Items[0].SellerNick,
                        state.Items[0].BuyerNick,
                        state.Items.ToList(),
                        capturedVersion);
                }

                var lease = new BuyerMessageBurstLease(
                    burst,
                    () =>
                    {
                        lock (state.Sync)
                        {
                            return state.Version == capturedVersion;
                        }
                    });

                try
                {
                    await _handler(lease);
                }
                catch (Exception ex)
                {
                    Log.Exception(ex);
                }

                lock (state.Sync)
                {
                    if (state.Version != capturedVersion) continue;
                    state.Items.Clear();
                    state.StartedAt = DateTime.MinValue;
                    state.WorkerRunning = false;
                    BurstState ignored;
                    _states.TryRemove(key, out ignored);
                    return;
                }
            }
        }

        internal static int QuietDelayMilliseconds(
            IEnumerable<BuyerMessageBurstItem> items,
            DateTime startedAt)
        {
            var list = (items ?? new BuyerMessageBurstItem[0]).Where(x => x != null).ToList();
            if (list.Count == 0) return 350;
            if (startedAt != DateTime.MinValue && DateTime.Now - startedAt >= TimeSpan.FromSeconds(4))
            {
                return 80;
            }

            var latest = (list.Last().DisplayText ?? string.Empty).Trim();
            var compact = Regex.Replace(latest, @"\s+", string.Empty);
            if (list.Count >= 6) return 420;
            if (IncomingMessageSafety.IsMediaPlaceholder(latest)) return 700;
            if (IsGreetingOnly(compact)) return 950;
            if (IsOpenShortFragment(compact)) return 1200;
            if (!EndsLikeCompleteSentence(compact) && compact.Length <= 24) return 800;
            return 350;
        }

        private static bool IsGreetingOnly(string text)
        {
            return text == "在吗"
                || text == "你好"
                || text == "您好"
                || text == "有人吗"
                || text == "客服在吗"
                || text == "亲在吗";
        }

        private static bool IsOpenShortFragment(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 10) return false;
            if (EndsLikeCompleteSentence(text)) return false;
            return text != "好的"
                && text != "好"
                && text != "嗯"
                && text != "谢谢"
                && text != "知道了"
                && text != "明白了";
        }

        private static bool EndsLikeCompleteSentence(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var last = text[text.Length - 1];
            return "。！？!?；;".IndexOf(last) >= 0;
        }

        private static string Key(string seller, string buyer)
        {
            return (seller ?? string.Empty).Trim() + "#" + (buyer ?? string.Empty).Trim();
        }
    }
}
