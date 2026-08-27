using Bot.ChatRecord;
using Bot.ShopScope;
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
        public long SessionGeneration { get; set; }

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
        public long SessionGeneration { get; private set; }

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
            SessionGeneration = Items.Count < 1 ? 0 : Items.Max(x => x.SessionGeneration);
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
        private readonly BuyerSessionAgent _sessionAgent;

        public BuyerMessageBurst Burst { get; private set; }

        public bool IsCurrent
        {
            get
            {
                return _isCurrent != null
                    && _isCurrent()
                    && (_sessionAgent == null
                        || _sessionAgent.IsCurrent(Burst.SellerNick, Burst.BuyerNick, Burst.SessionGeneration));
            }
        }

        public CancellationToken CancellationToken
        {
            get
            {
                return _sessionAgent == null
                    ? CancellationToken.None
                    : _sessionAgent.GetCancellationToken(Burst.SellerNick, Burst.BuyerNick, Burst.SessionGeneration);
            }
        }

        public BuyerMessageBurstLease(
            BuyerMessageBurst burst,
            Func<bool> isCurrent,
            BuyerSessionAgent sessionAgent)
        {
            Burst = burst;
            _isCurrent = isCurrent;
            _sessionAgent = sessionAgent;
        }

        public async Task<bool> ConfirmStableAsync(int milliseconds)
        {
            await Task.Delay(Math.Max(0, milliseconds));
            if (!IsCurrent) return false;
            MarkReady("send_barrier_stable");
            return true;
        }

        public bool MarkProcessing(string reason)
        {
            return Transition(BuyerSessionAgentState.Processing, reason);
        }

        public bool MarkGenerating(string reason)
        {
            return Transition(BuyerSessionAgentState.Generating, reason);
        }

        public bool MarkReady(string reason)
        {
            return Transition(BuyerSessionAgentState.Ready, reason);
        }

        public bool MarkSending(string reason)
        {
            return Transition(BuyerSessionAgentState.Sending, reason);
        }

        public bool MarkWaiting(string reason)
        {
            return Transition(BuyerSessionAgentState.Waiting, reason);
        }

        public bool MarkCompleted(string reason)
        {
            return Transition(BuyerSessionAgentState.Completed, reason);
        }

        public bool MarkFailed(string reason)
        {
            return Transition(BuyerSessionAgentState.Failed, reason);
        }

        private bool Transition(BuyerSessionAgentState state, string reason)
        {
            return _sessionAgent != null
                && _sessionAgent.TryTransition(
                    Burst.SellerNick,
                    Burst.BuyerNick,
                    Burst.SessionGeneration,
                    state,
                    reason);
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
            public int HardCancelVersion;
            public DateTime StartedAt = DateTime.MinValue;
            public BotActivityLease ActivityLease;
            public long LatestSessionGeneration;
        }

        // Only unresolved legacy/global configuration needs serialization. Shop-scoped runtime
        // work uses AsyncLocal<ShopContext> and is safe to dispatch concurrently.
        private static readonly SemaphoreSlim LegacyAiConfigurationGate = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<string, BurstState> _states =
            new ConcurrentDictionary<string, BurstState>(StringComparer.Ordinal);
        private readonly Func<BuyerMessageBurstLease, Task> _handler;
        private readonly BuyerSessionAgent _sessionAgent = new BuyerSessionAgent();

        public BuyerMessageBurstCoordinator(Func<BuyerMessageBurstLease, Task> handler)
        {
            if (handler == null) throw new ArgumentNullException("handler");
            _handler = handler;
            // Register the knowledge-center management page from a guaranteed runtime constructor.
            // The call only installs an idempotent WPF class handler; it does not send anything.
            try { Bot.Knowledge.LocalShortReplyUi.Initialize(); } catch { }
        }

        internal BuyerSessionAgent SessionAgent
        {
            get { return _sessionAgent; }
        }

        public void Enqueue(BuyerMessageBurstItem item)
        {
            if (item == null
                || string.IsNullOrWhiteSpace(item.SellerNick)
                || string.IsNullOrWhiteSpace(item.BuyerNick))
            {
                return;
            }

            // The session generation is advanced as soon as an accepted buyer message arrives.
            // This is independent of the quiet merge window: any in-flight older draft immediately
            // loses its send permission, while the new message can still be merged with nearby input.
            var observation = _sessionAgent.ObserveBuyerMessage(
                item.SellerNick,
                item.BuyerNick,
                item.MessageKey,
                item.SortValue,
                item.ReceivedAt);
            item.SessionGeneration = observation.Generation;
            _sessionAgent.TryTransition(
                item.SellerNick,
                item.BuyerNick,
                item.SessionGeneration,
                BuyerSessionAgentState.Coalescing,
                "pre_merge_rules");

            var allowLocalShortReply = !HasPendingBuyerMessages(item.SellerNick, item.BuyerNick);

            // Fixed business rules are evaluated on the individual incoming message before it is
            // inserted into any quiet-delay/context-merge state. This guarantees that a first
            // inquiry greeting, off-hours reply or standalone local short reply can never be stuck
            // behind “等待合并本轮消息”. A short acknowledgement is not consumed locally while an
            // earlier buyer message is still waiting to be merged.
            Task.Run(async () =>
            {
                var continueToMerge = true;
                try
                {
                    continueToMerge = await DeterministicAutoReplyService.HandleBeforeMergeAsync(
                        item,
                        allowLocalShortReply);
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount(
                        "消息合并前固定规则处理失败，继续普通合并链路: seller=" + item.SellerNick
                        + ", buyer=" + item.BuyerNick + ", error=" + Safe(ex.Message, 220),
                        20);
                }
                if (continueToMerge)
                {
                    EnqueueForMerge(item);
                }
                else
                {
                    _sessionAgent.TryTransition(
                        item.SellerNick,
                        item.BuyerNick,
                        item.SessionGeneration,
                        BuyerSessionAgentState.Completed,
                        "deterministic_rule_consumed");
                }
            });
        }

        private bool HasPendingBuyerMessages(string seller, string buyer)
        {
            BurstState state;
            if (!_states.TryGetValue(Key(seller, buyer), out state) || state == null) return false;
            lock (state.Sync)
            {
                return state.Items.Count > 0;
            }
        }

        private void EnqueueForMerge(BuyerMessageBurstItem item)
        {
            if (!_sessionAgent.IsCurrent(item.SellerNick, item.BuyerNick, item.SessionGeneration))
            {
                Log.Info("固定规则返回时已有更新买家消息，本条旧代次不再进入合并: seller=" + item.SellerNick
                    + ", buyer=" + item.BuyerNick + ", generation=" + item.SessionGeneration);
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

                var previousReceivedAt = state.Items.Count == 0
                    ? DateTime.MinValue
                    : state.Items[state.Items.Count - 1].ReceivedAt;
                AdaptiveReplyTimingService.RecordInterval(
                    item.SellerNick,
                    item.BuyerNick,
                    previousReceivedAt,
                    item.ReceivedAt);

                if (state.ActivityLease == null)
                {
                    state.ActivityLease = BotActivityCoordinator.Begin("买家消息聚合/回复", item.SellerNick, item.BuyerNick);
                }
                if (state.Items.Count == 0) state.StartedAt = DateTime.Now;
                state.Items.Add(item);
                if (state.Items.Count > 12) state.Items.RemoveRange(0, state.Items.Count - 12);
                state.Version++;
                state.LatestSessionGeneration = item.SessionGeneration;

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

        public void CancelBuyer(string seller, string buyer, string reason)
        {
            var key = Key(seller, buyer);
            BurstState state;
            if (!_states.TryGetValue(key, out state) || state == null) return;

            long generation;
            lock (state.Sync)
            {
                generation = state.LatestSessionGeneration;
                state.Version++;
                state.HardCancelVersion++;
                state.Items.Clear();
                state.StartedAt = DateTime.MinValue;
                try { state.DelayCancellation.Cancel(); } catch { }
                state.DelayCancellation.Dispose();
                state.DelayCancellation = new CancellationTokenSource();
                state.WorkerRunning = false;
                DisposeActivity(state);
            }

            if (generation > 0) _sessionAgent.Cancel(seller, buyer, generation, reason);
            BurstState ignored;
            _states.TryRemove(key, out ignored);
            Log.Info("买家自动回复任务已因人工介入失效: seller=" + seller
                + ", buyer=" + buyer + ", reason=" + (reason ?? string.Empty));
        }

        private async Task RunAsync(string key, BurstState state)
        {
            while (true)
            {
                CancellationToken token;
                int capturedVersion;
                int capturedHardCancelVersion;
                int delayMilliseconds;
                lock (state.Sync)
                {
                    if (state.Items.Count < 1)
                    {
                        state.WorkerRunning = false;
                        DisposeActivity(state);
                        BurstState empty;
                        _states.TryRemove(key, out empty);
                        return;
                    }
                    token = state.DelayCancellation.Token;
                    capturedVersion = state.Version;
                    capturedHardCancelVersion = state.HardCancelVersion;
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
                    if (state.Version != capturedVersion) continue;
                    if (state.Items.Count < 1) continue;

                    var dispatchedItems = state.Items.ToList();
                    state.Items.Clear();
                    state.StartedAt = DateTime.MinValue;
                    state.WorkerRunning = false;
                    burst = new BuyerMessageBurst(
                        dispatchedItems[0].SellerNick,
                        dispatchedItems[0].BuyerNick,
                        dispatchedItems,
                        capturedVersion);
                }

                if (!_sessionAgent.IsCurrent(burst.SellerNick, burst.BuyerNick, burst.SessionGeneration))
                {
                    Log.Info("聚合完成时会话代次已过期，跳过旧回复: seller=" + burst.SellerNick
                        + ", buyer=" + burst.BuyerNick + ", generation=" + burst.SessionGeneration);
                    continue;
                }

                var lease = new BuyerMessageBurstLease(
                    burst,
                    () =>
                    {
                        lock (state.Sync)
                        {
                            return state.HardCancelVersion == capturedHardCancelVersion;
                        }
                    },
                    _sessionAgent);
                lease.MarkProcessing("burst_dispatch");
                lease.MarkGenerating("reply_generation_started");

                try
                {
                    await DispatchScopedAsync(burst, lease);
                    if (lease.IsCurrent) lease.MarkCompleted("reply_pipeline_completed");
                }
                catch (Exception ex)
                {
                    lease.MarkFailed("reply_pipeline_exception");
                    Log.Exception(ex);
                }

                lock (state.Sync)
                {
                    if (state.Version == capturedVersion
                        && state.Items.Count < 1
                        && !state.WorkerRunning)
                    {
                        DisposeActivity(state);
                        BurstState ignored;
                        _states.TryRemove(key, out ignored);
                    }
                }
                _sessionAgent.Prune(TimeSpan.FromMinutes(30));
                return;
            }
        }

        private async Task DispatchScopedAsync(BuyerMessageBurst burst, BuyerMessageBurstLease lease)
        {
            ShopContext shop = null;
            try
            {
                shop = ShopContextLocator.ResolveRuntimeBySellerNick(
                    burst == null ? string.Empty : burst.SellerNick);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount(
                    "买家回复未能解析店铺身份，使用旧全局 AI 配置兼容模式：" + Safe(ex.Message, 220),
                    20);
            }

            if (shop == null)
            {
                await LegacyAiConfigurationGate.WaitAsync();
                try
                {
                    await _handler(lease);
                }
                finally
                {
                    LegacyAiConfigurationGate.Release();
                }
                return;
            }

            using (ShopSettingsScope.Enter(shop))
            {
                await _handler(lease);
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

            var latestItem = list.Last();
            var latest = (latestItem.DisplayText ?? string.Empty).Trim();
            var compact = Regex.Replace(latest, @"\s+", string.Empty);
            int baseline;
            AdaptiveDelayKind kind;
            if (list.Count >= 6)
            {
                baseline = 420;
                kind = AdaptiveDelayKind.DenseBurst;
            }
            else if (IncomingMessageSafety.IsMediaPlaceholder(latest))
            {
                baseline = 700;
                kind = AdaptiveDelayKind.Media;
            }
            else if (IsGreetingOnly(compact))
            {
                baseline = 950;
                kind = AdaptiveDelayKind.Greeting;
            }
            else if (IsOpenShortFragment(compact))
            {
                baseline = 1200;
                kind = AdaptiveDelayKind.Fragment;
            }
            else if (!EndsLikeCompleteSentence(compact) && compact.Length <= 24)
            {
                baseline = 800;
                kind = AdaptiveDelayKind.Fragment;
            }
            else
            {
                baseline = 350;
                kind = AdaptiveDelayKind.Complete;
            }

            return AdaptiveReplyTimingService.AdjustDelay(
                latestItem.SellerNick,
                latestItem.BuyerNick,
                baseline,
                kind);
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

        private static void DisposeActivity(BurstState state)
        {
            if (state == null || state.ActivityLease == null) return;
            try { state.ActivityLease.Dispose(); } catch { }
            state.ActivityLease = null;
        }

        private static string Key(string seller, string buyer)
        {
            return (seller ?? string.Empty).Trim() + "#" + (buyer ?? string.Empty).Trim();
        }

        private static string Safe(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }
}