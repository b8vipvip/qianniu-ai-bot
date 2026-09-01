from pathlib import Path


def read(path):
    return Path(path).read_text(encoding="utf-8-sig")


def write(path, text):
    Path(path).write_text(text, encoding="utf-8")


def replace_once(text, old, new, label):
    if old not in text:
        raise SystemExit("missing patch anchor: " + label)
    return text.replace(old, new, 1)


# 1) BuyerSessionAgent: retain state per generation so an older AI timeout cannot be
# rewritten as Completed merely because the latest buyer generation has moved on.
agent_path = "src/Bot/ChromeNs/BuyerSessionAgent.cs"
agent = read(agent_path)
agent = replace_once(
    agent,
    "        private const int MaxRememberedEvents = 64;\n",
    "        private const int MaxRememberedEvents = 64;\n        private const int MaxRememberedGenerationStates = 128;\n",
    "generation state cap",
)
agent = replace_once(
    agent,
    "                state.ActiveGenerations[generation] = cts;\n                token = cts.Token;\n",
    "                state.ActiveGenerations[generation] = cts;\n                SetGenerationStateLocked(state, generation, BuyerSessionAgentState.Observed);\n                token = cts.Token;\n",
    "observe generation state",
)
start = agent.index("        public bool TryTransition(\n")
end = agent.index("        public void Cancel(string sellerNick", start)
agent = agent[:start] + '''        public bool TryTransition(
            string sellerNick,
            string buyerNick,
            long generation,
            BuyerSessionAgentState next,
            string reason)
        {
            SessionState state;
            if (!Sessions.TryGetValue(BuildKey(sellerNick, buyerNick), out state)) return false;
            BuyerSessionAgentState previous;
            CancellationTokenSource completedCts = null;
            var updateLatestState = false;
            lock (state.SyncRoot)
            {
                CancellationTokenSource active;
                if (!state.ActiveGenerations.TryGetValue(generation, out active)
                    || active == null
                    || active.IsCancellationRequested)
                {
                    return false;
                }

                if (!state.GenerationStates.TryGetValue(generation, out previous))
                    previous = BuyerSessionAgentState.Observed;
                if (!CanTransition(previous, next)) return false;
                SetGenerationStateLocked(state, generation, next);

                updateLatestState = state.Generation == generation;
                if (updateLatestState)
                    SetStateLocked(state, next, reason);

                if (next == BuyerSessionAgentState.Completed
                    || next == BuyerSessionAgentState.Cancelled
                    || next == BuyerSessionAgentState.Failed)
                {
                    if (state.ActiveGenerations.TryGetValue(generation, out completedCts))
                        state.ActiveGenerations.Remove(generation);
                    if (state.GenerationCancellation == completedCts) state.GenerationCancellation = null;
                }
            }

            if (completedCts != null)
            {
                try { completedCts.Dispose(); } catch { }
            }

            Log.Info("BuyerSessionAgent transition: seller=" + Normalize(sellerNick)
                + ", buyer=" + Normalize(buyerNick)
                + ", generation=" + generation
                + ", state=" + previous + "->" + next
                + ", latest=" + updateLatestState
                + ", reason=" + Normalize(reason));
            return true;
        }

''' + agent[end:]
start = agent.index("        public void Cancel(string sellerNick")
end = agent.index("        public void CancelAll(string sellerNick", start)
agent = agent[:start] + '''        public void Cancel(string sellerNick, string buyerNick, long generation, string reason)
        {
            SessionState state;
            if (!Sessions.TryGetValue(BuildKey(sellerNick, buyerNick), out state)) return;
            CancellationTokenSource cts = null;
            lock (state.SyncRoot)
            {
                if (!state.ActiveGenerations.TryGetValue(generation, out cts)) return;
                state.ActiveGenerations.Remove(generation);
                SetGenerationStateLocked(state, generation, BuyerSessionAgentState.Cancelled);
                if (state.GenerationCancellation == cts) state.GenerationCancellation = null;
                if (state.Generation == generation)
                    SetStateLocked(state, BuyerSessionAgentState.Cancelled, reason);
            }
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
                try { cts.Dispose(); } catch { }
            }
        }

''' + agent[end:]
start = agent.index("        public void CancelAll(string sellerNick")
end = agent.index("        public BuyerSessionAgentSnapshot GetSnapshot", start)
agent = agent[:start] + '''        public void CancelAll(string sellerNick, string buyerNick, string reason)
        {
            SessionState state;
            if (!Sessions.TryGetValue(BuildKey(sellerNick, buyerNick), out state)) return;
            List<CancellationTokenSource> cancellations;
            lock (state.SyncRoot)
            {
                var generations = state.ActiveGenerations.Keys.ToList();
                cancellations = state.ActiveGenerations.Values
                    .Where(x => x != null)
                    .Distinct()
                    .ToList();
                foreach (var generation in generations)
                    SetGenerationStateLocked(state, generation, BuyerSessionAgentState.Cancelled);
                state.ActiveGenerations.Clear();
                state.GenerationCancellation = null;
                if (state.Generation > 0)
                    SetStateLocked(state, BuyerSessionAgentState.Cancelled, reason);
            }

            foreach (var cts in cancellations)
            {
                try { cts.Cancel(); } catch { }
                try { cts.Dispose(); } catch { }
            }
            Log.Info("BuyerSessionAgent hard-cancelled all generations: seller=" + Normalize(sellerNick)
                + ", buyer=" + Normalize(buyerNick)
                + ", count=" + cancellations.Count
                + ", reason=" + Normalize(reason));
        }

        public bool TryGetGenerationState(
            string sellerNick,
            string buyerNick,
            long generation,
            out BuyerSessionAgentState generationState)
        {
            generationState = BuyerSessionAgentState.Idle;
            SessionState state;
            if (!Sessions.TryGetValue(BuildKey(sellerNick, buyerNick), out state)) return false;
            lock (state.SyncRoot)
            {
                return state.GenerationStates.TryGetValue(generation, out generationState);
            }
        }

''' + agent[end:]
agent = replace_once(
    agent,
    "        private static void SetStateLocked(SessionState state, BuyerSessionAgentState next, string reason)\n",
    '''        private static void SetGenerationStateLocked(
            SessionState state,
            long generation,
            BuyerSessionAgentState next)
        {
            if (!state.GenerationStates.ContainsKey(generation))
                state.GenerationStateOrder.Enqueue(generation);
            state.GenerationStates[generation] = next;
            while (state.GenerationStateOrder.Count > MaxRememberedGenerationStates)
            {
                var oldest = state.GenerationStateOrder.Dequeue();
                if (oldest == state.Generation || state.ActiveGenerations.ContainsKey(oldest))
                {
                    state.GenerationStateOrder.Enqueue(oldest);
                    break;
                }
                state.GenerationStates.Remove(oldest);
            }
        }

        private static void SetStateLocked(SessionState state, BuyerSessionAgentState next, string reason)
''',
    "generation state helper",
)
agent = replace_once(
    agent,
    "            public readonly Dictionary<long, CancellationTokenSource> ActiveGenerations =\n                new Dictionary<long, CancellationTokenSource>();\n",
    "            public readonly Dictionary<long, CancellationTokenSource> ActiveGenerations =\n                new Dictionary<long, CancellationTokenSource>();\n            public readonly Dictionary<long, BuyerSessionAgentState> GenerationStates =\n                new Dictionary<long, BuyerSessionAgentState>();\n            public readonly Queue<long> GenerationStateOrder = new Queue<long>();\n",
    "generation state fields",
)
write(agent_path, agent)


# 2) Semantic continuation for short deictic follow-ups. Do not globally increase the
# burst quiet window; only attach recent context when the second message explicitly
# points back to the previous one (e.g. 电视版酷狗音乐 -> 这个支持吗).
burst_path = "src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs"
burst = read(burst_path)
burst = replace_once(
    burst,
    "        public long SessionGeneration { get; set; }\n",
    "        public long SessionGeneration { get; set; }\n        public string SemanticContinuationContext { get; set; }\n",
    "semantic continuation item field",
)
burst = replace_once(
    burst,
    '''            CombinedQuestion = BuildCombinedQuestion(Items);
            ModelQuestion = Items.Count <= 1
                ? CombinedQuestion
                : "【买家本轮连续消息，以下按发送顺序】\\n" + CombinedQuestion;
''',
    '''            CombinedQuestion = BuildCombinedQuestion(Items);
            var continuation = Items
                .Select(x => (x.SemanticContinuationContext ?? string.Empty).Trim())
                .LastOrDefault(x => !string.IsNullOrWhiteSpace(x));
            if (!string.IsNullOrWhiteSpace(continuation)
                && NormalizeCompare(CombinedQuestion).IndexOf(NormalizeCompare(continuation), StringComparison.Ordinal) < 0)
            {
                ModelQuestion = "【买家上一句与当前指代续问，请合并理解为一个完整问题】\\n上一句："
                    + continuation + "\\n当前：" + CombinedQuestion;
            }
            else
            {
                ModelQuestion = Items.Count <= 1
                    ? CombinedQuestion
                    : "【买家本轮连续消息，以下按发送顺序】\\n" + CombinedQuestion;
            }
''',
    "semantic model question",
)
burst = replace_once(
    burst,
    "        private const int PreMergeRuleGateWaitMilliseconds = 2500;\n",
    '''        private sealed class RecentBuyerText
        {
            public string Text { get; set; }
            public DateTime ReceivedAt { get; set; }
            public long Generation { get; set; }
        }

        private const int PreMergeRuleGateWaitMilliseconds = 2500;
        private const int SemanticContinuationWindowSeconds = 15;
''',
    "semantic recent type",
)
burst = replace_once(
    burst,
    "        private readonly ConcurrentDictionary<string, BurstState> _states =\n            new ConcurrentDictionary<string, BurstState>(StringComparer.Ordinal);\n",
    "        private readonly ConcurrentDictionary<string, BurstState> _states =\n            new ConcurrentDictionary<string, BurstState>(StringComparer.Ordinal);\n        private readonly ConcurrentDictionary<string, RecentBuyerText> _recentBuyerTexts =\n            new ConcurrentDictionary<string, RecentBuyerText>(StringComparer.Ordinal);\n",
    "semantic recent dictionary",
)
burst = replace_once(
    burst,
    "            item.SessionGeneration = observation.Generation;\n            _sessionAgent.TryTransition(\n",
    '''            item.SessionGeneration = observation.Generation;
            AttachSemanticContinuation(item);
            RememberRecentBuyerText(item);
            _sessionAgent.TryTransition(
''',
    "semantic attach call",
)
marker = "        private bool HasPendingBuyerMessages(string seller, string buyer)\n"
semantic_methods = '''        private void AttachSemanticContinuation(BuyerMessageBurstItem item)
        {
            if (item == null || !LooksLikeSemanticContinuation(item.DisplayText)) return;
            var key = Key(item.SellerNick, item.BuyerNick);
            RecentBuyerText previous;
            if (!_recentBuyerTexts.TryGetValue(key, out previous) || previous == null) return;
            var age = item.ReceivedAt - previous.ReceivedAt;
            if (age < TimeSpan.Zero || age > TimeSpan.FromSeconds(SemanticContinuationWindowSeconds)) return;
            var previousText = NormalizeSemanticText(previous.Text);
            var currentText = NormalizeSemanticText(item.DisplayText);
            if (string.IsNullOrWhiteSpace(previousText)
                || string.Equals(previousText, currentText, StringComparison.OrdinalIgnoreCase)) return;

            item.SemanticContinuationContext = previousText;
            if (previous.Generation > 0 && previous.Generation != item.SessionGeneration)
            {
                _sessionAgent.Cancel(
                    item.SellerNick,
                    item.BuyerNick,
                    previous.Generation,
                    "semantic_continuation_superseded");
            }
            Log.Info("买家短指代续问已关联上一句语义上下文: seller=" + item.SellerNick
                + ", buyer=" + item.BuyerNick
                + ", previousGeneration=" + previous.Generation
                + ", generation=" + item.SessionGeneration
                + ", ageMs=" + Math.Max(0, (long)age.TotalMilliseconds));
        }

        private void RememberRecentBuyerText(BuyerMessageBurstItem item)
        {
            if (item == null) return;
            var text = NormalizeSemanticText(item.DisplayText);
            if (string.IsNullOrWhiteSpace(text) || text.Length > 240) return;
            _recentBuyerTexts[Key(item.SellerNick, item.BuyerNick)] = new RecentBuyerText
            {
                Text = text,
                ReceivedAt = item.ReceivedAt == default(DateTime) ? DateTime.Now : item.ReceivedAt,
                Generation = item.SessionGeneration
            };
        }

        private static bool LooksLikeSemanticContinuation(string value)
        {
            var text = NormalizeSemanticText(value);
            if (string.IsNullOrWhiteSpace(text) || text.Length > 32) return false;
            var compact = Regex.Replace(text.ToLowerInvariant(), @"[\\s，。！？!?、；;：:]", string.Empty);
            var prefixes = new[] { "这个", "这款", "这种", "这个版本", "这个型号", "那个", "那款", "那种", "它" };
            if (!prefixes.Any(x => compact.StartsWith(x, StringComparison.Ordinal))) return false;
            if (compact == "这个" || compact == "这个呢" || compact == "那个" || compact == "那个呢" || compact == "它呢") return true;
            return Regex.IsMatch(compact, @"支持|能用|可以|可用|适用|兼容|行吗|能不能|可不可以|怎么样|咋样|有吗|吗$|呢$");
        }

        private static string NormalizeSemanticText(string value)
        {
            value = (value ?? string.Empty).Replace("\\r", " ").Replace("\\n", " ").Trim();
            value = Regex.Replace(value, @"\\s+", " ");
            return value;
        }

'''
burst = replace_once(burst, marker, semantic_methods + marker, "semantic helper methods")
burst = replace_once(
    burst,
    '''                    var deterministicSnapshot = _sessionAgent.GetSnapshot(
                        item.SellerNick,
                        item.BuyerNick);
                    if (deterministicSnapshot != null
                        && deterministicSnapshot.Generation == item.SessionGeneration
                        && deterministicSnapshot.State == BuyerSessionAgentState.Failed)
''',
    '''                    BuyerSessionAgentState deterministicState;
                    if (_sessionAgent.TryGetGenerationState(
                        item.SellerNick,
                        item.BuyerNick,
                        item.SessionGeneration,
                        out deterministicState)
                        && deterministicState == BuyerSessionAgentState.Failed)
''',
    "deterministic generation state",
)
burst = replace_once(
    burst,
    '''                        var snapshot = _sessionAgent.GetSnapshot(burst.SellerNick, burst.BuyerNick);
                        var failed = snapshot != null
                            && snapshot.Generation == burst.SessionGeneration
                            && snapshot.State == BuyerSessionAgentState.Failed;
                        var returnedWithoutReady = snapshot != null
                            && snapshot.Generation == burst.SessionGeneration
                            && snapshot.State == BuyerSessionAgentState.Generating;
''',
    '''                        BuyerSessionAgentState generationState;
                        var hasGenerationState = _sessionAgent.TryGetGenerationState(
                            burst.SellerNick,
                            burst.BuyerNick,
                            burst.SessionGeneration,
                            out generationState);
                        var failed = hasGenerationState && generationState == BuyerSessionAgentState.Failed;
                        var returnedWithoutReady = hasGenerationState && generationState == BuyerSessionAgentState.Generating;
''',
    "post-dispatch generation state",
)
write(burst_path, burst)


# 3) Order fixed preset: refresh from the already merged local Hub snapshot immediately
# before rendering. This recovers quantity/paid that another parser published in the
# same event tick without restoring the old multi-second network enrichment wait.
hub_path = "src/Bot/ChromeNs/OrderEventHub.cs"
hub = read(hub_path)
hub = replace_once(
    hub,
    "        public static OrderEventPublishResult Publish(OrderSnapshot snapshot)\n",
    '''        public static OrderSnapshot RefreshFromCanonical(OrderSnapshot snapshot)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.OrderId)) return snapshot;
            lock (Sync)
            {
                EnsureLoaded();
                var key = BuildKey(snapshot);
                var existing = _state.Events.FirstOrDefault(x => x != null && string.Equals(x.Key, key, StringComparison.Ordinal));
                if (existing == null || existing.Snapshot == null) return snapshot;
                Merge(existing.Snapshot, snapshot);
                return existing.Snapshot;
            }
        }

        public static OrderEventPublishResult Publish(OrderSnapshot snapshot)
''',
    "canonical snapshot refresh",
)
write(hub_path, hub)

order_path = "src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs"
order = read(order_path)
order = replace_once(
    order,
    "        public static async Task<OrderPlacedReplyResolution> ResolveAsync(OrderPlacedReplyPlan plan)\n        {\n            if (plan == null || plan.Config == null) return Fail(\"下单自动回复计划为空\");\n            var cfg = plan.Config;\n",
    '''        public static async Task<OrderPlacedReplyResolution> ResolveAsync(OrderPlacedReplyPlan plan)
        {
            if (plan == null || plan.Config == null) return Fail("下单自动回复计划为空");
            var cfg = plan.Config;
            await RefreshLocalSnapshotBeforeRenderAsync(plan, cfg.OrderPlacedReplyText);
''',
    "refresh order plan before resolve",
)
order = replace_once(
    order,
    "        public static void Complete(OrderPlacedReplyPlan plan, bool delivered)\n",
    '''        private static async Task RefreshLocalSnapshotBeforeRenderAsync(OrderPlacedReplyPlan plan, string template)
        {
            if (plan == null || plan.Snapshot == null || string.IsNullOrWhiteSpace(plan.OrderId)) return;
            var missingBefore = MissingTemplateFields(template, plan);
            if (missingBefore.Count > 0)
            {
                // Same-process parsers often publish a richer snapshot a few milliseconds after the
                // first confirmed order event. Allow only a tiny local settle; never wait on trade API.
                await Task.Delay(120).ConfigureAwait(false);
            }
            var refreshed = OrderEventHub.RefreshFromCanonical(plan.Snapshot);
            if (refreshed != null) plan.Snapshot = refreshed;
            var missingAfter = MissingTemplateFields(template, plan);
            Log.Info("订单固定预设渲染前已刷新Hub本地快照: orderId=" + plan.OrderId
                + ", missingBefore=" + string.Join(",", missingBefore)
                + ", missingAfter=" + string.Join(",", missingAfter)
                + ", quantity=" + (plan.Snapshot == null ? 0 : plan.Snapshot.Quantity)
                + ", paid=" + (plan.Snapshot == null || !plan.Snapshot.PaidAmount.HasValue ? "" : plan.Snapshot.PaidAmount.Value.ToString("0.##"))
                + ", skuPresent=" + (plan.Snapshot != null && !string.IsNullOrWhiteSpace(plan.Snapshot.SkuText)));
        }

        public static void Complete(OrderPlacedReplyPlan plan, bool delivered)
''',
    "order local refresh helper",
)
write(order_path, order)


# 4) Build/package: ONNXRuntime is present, but it is a native MSVC binary. Bundle the
# redistributable VC runtime DLLs beside the isolated OCR worker so Server installations
# do not depend on a machine-wide Visual C++ Redistributable.
workflow_path = ".github/workflows/windows-build.yml"
workflow = read(workflow_path)
workflow = replace_once(
    workflow,
    "          $requiredOcr = @(\n",
    '''          $vcRuntimeNames = @(
            'vcruntime140.dll',
            'vcruntime140_1.dll',
            'msvcp140.dll',
            'msvcp140_1.dll',
            'msvcp140_2.dll',
            'concrt140.dll'
          )
          foreach ($name in $vcRuntimeNames) {
            $source = Join-Path $env:WINDIR ('System32\\' + $name)
            if (Test-Path -LiteralPath $source -PathType Leaf) {
              Copy-Item -LiteralPath $source -Destination (Join-Path $workerOut $name) -Force
            }
          }

          $requiredOcr = @(
''',
    "bundle vc runtime",
)
workflow = replace_once(
    workflow,
    "            (Join-Path $workerOut 'LocalOcrWorker.exe'),\n",
    "            (Join-Path $workerOut 'LocalOcrWorker.exe'),\n            (Join-Path $workerOut 'onnxruntime.dll'),\n            (Join-Path $workerOut 'vcruntime140.dll'),\n            (Join-Path $workerOut 'vcruntime140_1.dll'),\n            (Join-Path $workerOut 'msvcp140.dll'),\n",
    "worker native required files",
)
workflow = replace_once(
    workflow,
    "            'package\\Bin\\local-ocr\\LocalOcrWorker.exe',\n",
    "            'package\\Bin\\local-ocr\\LocalOcrWorker.exe',\n            'package\\Bin\\local-ocr\\onnxruntime.dll',\n            'package\\Bin\\local-ocr\\vcruntime140.dll',\n            'package\\Bin\\local-ocr\\vcruntime140_1.dll',\n            'package\\Bin\\local-ocr\\msvcp140.dll',\n",
    "package native verification",
)
write(workflow_path, workflow)


# Focused static regressions.
test_path = "tests/test_runtime_1095_context_order_ocr_static.py"
write(test_path, r'''from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_deictic_followup_uses_recent_buyer_context_without_global_long_delay():
    source = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    assert "SemanticContinuationWindowSeconds = 15" in source
    assert "买家上一句与当前指代续问，请合并理解为一个完整问题" in source
    assert '"这个", "这款", "这种"' in source
    assert "semantic_continuation_superseded" in source
    assert "QuietDelayMilliseconds" in source
    assert "SemanticContinuationWindowSeconds * 1000" not in source


def test_generation_terminal_state_is_tracked_per_generation():
    agent = read("src/Bot/ChromeNs/BuyerSessionAgent.cs")
    coordinator = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    assert "Dictionary<long, BuyerSessionAgentState> GenerationStates" in agent
    assert "TryGetGenerationState" in agent
    assert "SetGenerationStateLocked(state, generation, next)" in agent
    assert "hasGenerationState && generationState == BuyerSessionAgentState.Generating" in coordinator
    post = coordinator.split("await DispatchScopedAsync(burst, lease);", 1)[1].split("catch (OperationCanceledException)", 1)[0]
    assert "GetSnapshot(burst.SellerNick, burst.BuyerNick)" not in post


def test_fixed_preset_refreshes_only_local_hub_snapshot_before_render():
    service = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    hub = read("src/Bot/ChromeNs/OrderEventHub.cs")
    assert "RefreshLocalSnapshotBeforeRenderAsync" in service
    assert "Task.Delay(120)" in service
    assert "OrderEventHub.RefreshFromCanonical(plan.Snapshot)" in service
    helper = service.split("private static async Task RefreshLocalSnapshotBeforeRenderAsync", 1)[1].split("public static void Complete", 1)[0]
    assert "trade" not in helper.lower()
    assert "http" not in helper.lower()
    assert "public static OrderSnapshot RefreshFromCanonical" in hub
    assert "Merge(existing.Snapshot, snapshot)" in hub


def test_ocr_release_bundles_onnx_and_vc_runtime_next_to_worker():
    workflow = read(".github/workflows/windows-build.yml")
    assert "package\\Bin\\local-ocr\\onnxruntime.dll" in workflow
    assert "package\\Bin\\local-ocr\\vcruntime140.dll" in workflow
    assert "package\\Bin\\local-ocr\\vcruntime140_1.dll" in workflow
    assert "package\\Bin\\local-ocr\\msvcp140.dll" in workflow
    assert "Copy-Item -LiteralPath $source -Destination (Join-Path $workerOut $name)" in workflow
''')
