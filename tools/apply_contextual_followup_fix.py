from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def write(path, text):
    (ROOT / path).write_text(text, encoding="utf-8")


def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected one anchor, found {count}")
    return text.replace(old, new, 1)


# 1) Buyer burst: semantic question frames + remove redundant outer deterministic gate + liveness deadline.
path = "src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs"
s = read(path)
s = replace_once(
    s,
    '''            if (!string.IsNullOrWhiteSpace(continuation)\n                && NormalizeCompare(CombinedQuestion).IndexOf(NormalizeCompare(continuation), StringComparison.Ordinal) < 0)\n            {\n                ModelQuestion = "【买家上一句与当前指代续问，请合并理解为一个完整问题】\\n上一句："\n                    + continuation + "\\n当前：" + CombinedQuestion;\n            }''',
    '''            if (!string.IsNullOrWhiteSpace(continuation)\n                && NormalizeCompare(CombinedQuestion).IndexOf(NormalizeCompare(continuation), StringComparison.Ordinal) < 0)\n            {\n                ModelQuestion = "【买家当前消息是对上一条未解决问题的省略补充或催问。请把主问题、后续片段以及最近商品/图片/订单上下文作为同一个问题整体理解，只回答一次，不要把‘？’、‘可以吗’、‘能用吗’之类片段当成新主题。】\\n主问题："\n                    + continuation + "\\n后续片段：" + CombinedQuestion;\n            }''',
    "model question semantics")

s = replace_once(
    s,
    '''        private sealed class RecentBuyerText\n        {\n            public string Text { get; set; }\n            public DateTime ReceivedAt { get; set; }\n            public long Generation { get; set; }\n        }\n\n        private const int PreMergeRuleGateWaitMilliseconds = 2500;\n        private const int SemanticContinuationWindowSeconds = 15;\n        private static readonly SemaphoreSlim LegacyAiConfigurationGate = new SemaphoreSlim(1, 1);\n        private readonly ConcurrentDictionary<string, SemaphoreSlim> _preMergeRuleGates =\n            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);''',
    '''        private sealed class RecentBuyerText\n        {\n            // Anchor is the last substantive unresolved utterance. Punctuation nudges and short\n            // elliptical confirmations update Latest* but never erase this semantic anchor.\n            public string AnchorText { get; set; }\n            public DateTime AnchorReceivedAt { get; set; }\n            public long AnchorGeneration { get; set; }\n            public string LatestText { get; set; }\n            public DateTime LatestReceivedAt { get; set; }\n            public long LatestGeneration { get; set; }\n        }\n\n        // DeterministicAutoReplyService already owns the single per-buyer serialization gate.\n        // A second outer gate can strand every later generation behind one unhealthy fixed send.\n        private const int PreMergeRuleExecutionDeadlineMilliseconds = 20000;\n        private const int SemanticContinuationWindowSeconds = 180;\n        private static readonly SemaphoreSlim LegacyAiConfigurationGate = new SemaphoreSlim(1, 1);''',
    "recent semantic context + redundant gate")

start_marker = '''            var allowLocalShortReply = !HasPendingBuyerMessages(item.SellerNick, item.BuyerNick);\n            var preMergeGate = _preMergeRuleGates.GetOrAdd('''
start = s.index(start_marker)
end = s.index('        private void AttachSemanticContinuation(BuyerMessageBurstItem item)', start)
new_block = '''            var allowLocalShortReply = !HasPendingBuyerMessages(item.SellerNick, item.BuyerNick);\n            Task.Run(async () =>\n            {\n                var continueToMerge = true;\n                try\n                {\n                    // DeterministicAutoReplyService has the authoritative same-buyer gate. Run it\n                    // directly so an unhealthy earlier generation cannot own a second outer lock.\n                    // The hard deadline is a final liveness boundary, not the normal rule timeout.\n                    var rulesTask = DeterministicAutoReplyService.HandleBeforeMergeAsync(\n                        item,\n                        allowLocalShortReply);\n                    using (var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(observation.CancellationToken))\n                    {\n                        deadlineCts.CancelAfter(PreMergeRuleExecutionDeadlineMilliseconds);\n                        var deadlineTask = Task.Delay(Timeout.Infinite, deadlineCts.Token);\n                        var completed = await Task.WhenAny(rulesTask, deadlineTask);\n                        if (completed == rulesTask)\n                        {\n                            deadlineCts.Cancel();\n                            continueToMerge = await rulesTask;\n                        }\n                        else\n                        {\n                            if (observation.CancellationToken.IsCancellationRequested) return;\n                            Log.ErrorWithMaxCount(\n                                "消息合并前固定规则执行超过硬截止，已fail-open继续普通合并链路: seller="\n                                + item.SellerNick + ", buyer=" + item.BuyerNick\n                                + ", generation=" + item.SessionGeneration\n                                + ", deadlineMs=" + PreMergeRuleExecutionDeadlineMilliseconds,\n                                50);\n                            rulesTask.ContinueWith(t =>\n                            {\n                                var error = t.Exception == null ? string.Empty : Safe(t.Exception.GetBaseException().Message, 220);\n                                Log.ErrorWithMaxCount("超时后的固定规则任务最终异常: " + error, 20);\n                            }, TaskContinuationOptions.OnlyOnFaulted);\n                            continueToMerge = true;\n                        }\n                    }\n                }\n                catch (OperationCanceledException)\n                {\n                    if (observation.CancellationToken.IsCancellationRequested)\n                    {\n                        Log.Info("消息合并前固定规则已因generation显式失效取消: seller="\n                            + item.SellerNick + ", buyer=" + item.BuyerNick\n                            + ", generation=" + item.SessionGeneration);\n                        return;\n                    }\n                    Log.ErrorWithMaxCount(\n                        "消息合并前固定规则发生非会话取消，已fail-open继续普通合并链路: seller="\n                        + item.SellerNick + ", buyer=" + item.BuyerNick\n                        + ", generation=" + item.SessionGeneration,\n                        20);\n                    continueToMerge = true;\n                }\n                catch (Exception ex)\n                {\n                    Log.ErrorWithMaxCount(\n                        "消息合并前固定规则处理失败，继续普通合并链路: seller=" + item.SellerNick\n                        + ", buyer=" + item.BuyerNick + ", error=" + Safe(ex.Message, 220),\n                        20);\n                    continueToMerge = true;\n                }\n\n                if (observation.CancellationToken.IsCancellationRequested\n                    || !_sessionAgent.IsCurrent(item.SellerNick, item.BuyerNick, item.SessionGeneration))\n                {\n                    return;\n                }\n\n                if (continueToMerge)\n                {\n                    try\n                    {\n                        EnqueueForMerge(item);\n                    }\n                    catch (Exception ex)\n                    {\n                        _sessionAgent.TryTransition(\n                            item.SellerNick,\n                            item.BuyerNick,\n                            item.SessionGeneration,\n                            BuyerSessionAgentState.Failed,\n                            "pre_merge_enqueue_exception");\n                        Log.ErrorWithMaxCount(\n                            "消息进入合并队列异常，已结束Coalescing避免永久等待: seller="\n                            + item.SellerNick + ", buyer=" + item.BuyerNick\n                            + ", generation=" + item.SessionGeneration\n                            + ", error=" + Safe(ex.Message, 220),\n                            50);\n                    }\n                }\n                else\n                {\n                    BuyerSessionAgentState deterministicState;\n                    if (_sessionAgent.TryGetGenerationState(\n                        item.SellerNick,\n                        item.BuyerNick,\n                        item.SessionGeneration,\n                        out deterministicState)\n                        && deterministicState == BuyerSessionAgentState.Failed)\n                    {\n                        Log.Info("固定规则发送失败后保留Failed终态，禁止升级Completed: seller="\n                            + item.SellerNick + ", buyer=" + item.BuyerNick\n                            + ", generation=" + item.SessionGeneration);\n                    }\n                    else\n                    {\n                        _sessionAgent.TryTransition(\n                            item.SellerNick,\n                            item.BuyerNick,\n                            item.SessionGeneration,\n                            BuyerSessionAgentState.Completed,\n                            "deterministic_rule_consumed");\n                    }\n                }\n            });\n        }\n\n'''
s = s[:start] + new_block + s[end:]

start = s.index('        private void AttachSemanticContinuation(BuyerMessageBurstItem item)')
end = s.index('        private bool HasPendingBuyerMessages(string seller, string buyer)', start)
semantic_block = '''        private void AttachSemanticContinuation(BuyerMessageBurstItem item)\n        {\n            if (item == null || !LooksLikeSemanticContinuation(item.DisplayText)) return;\n            var key = Key(item.SellerNick, item.BuyerNick);\n            RecentBuyerText previous;\n            if (!_recentBuyerTexts.TryGetValue(key, out previous) || previous == null) return;\n\n            var currentAt = item.ReceivedAt == default(DateTime) ? DateTime.Now : item.ReceivedAt;\n            var anchorText = NormalizeSemanticText(previous.AnchorText);\n            if (string.IsNullOrWhiteSpace(anchorText) || previous.AnchorReceivedAt == DateTime.MinValue) return;\n            var age = currentAt - previous.AnchorReceivedAt;\n            if (age < TimeSpan.Zero || age > TimeSpan.FromSeconds(SemanticContinuationWindowSeconds)) return;\n\n            var currentText = NormalizeSemanticText(item.DisplayText);\n            if (string.IsNullOrWhiteSpace(currentText)\n                || string.Equals(anchorText, currentText, StringComparison.OrdinalIgnoreCase)) return;\n\n            item.SemanticContinuationContext = anchorText;\n\n            // A dependent fragment is not an independent new topic. It supersedes only the previous\n            // generation in the same semantic chain; unrelated ordinary questions remain parallel.\n            var supersededGeneration = previous.LatestGeneration > 0\n                ? previous.LatestGeneration\n                : previous.AnchorGeneration;\n            if (supersededGeneration > 0 && supersededGeneration != item.SessionGeneration)\n            {\n                _sessionAgent.Cancel(\n                    item.SellerNick,\n                    item.BuyerNick,\n                    supersededGeneration,\n                    "semantic_continuation_superseded");\n            }\n            if (previous.LatestReceivedAt != DateTime.MinValue)\n            {\n                ResponseProgressTracker.MarkContextualContinuationMerged(\n                    item.SellerNick,\n                    item.BuyerNick,\n                    previous.LatestReceivedAt,\n                    currentText);\n            }\n            Log.Info("买家省略/催问续句已关联未解决主问题: seller=" + item.SellerNick\n                + ", buyer=" + item.BuyerNick\n                + ", previousGeneration=" + supersededGeneration\n                + ", generation=" + item.SessionGeneration\n                + ", anchorAgeMs=" + Math.Max(0, (long)age.TotalMilliseconds));\n        }\n\n        private void RememberRecentBuyerText(BuyerMessageBurstItem item)\n        {\n            if (item == null) return;\n            var text = NormalizeSemanticText(item.DisplayText);\n            if (string.IsNullOrWhiteSpace(text) || text.Length > 240) return;\n            var key = Key(item.SellerNick, item.BuyerNick);\n            var receivedAt = item.ReceivedAt == default(DateTime) ? DateTime.Now : item.ReceivedAt;\n            var dependent = LooksLikeSemanticContinuation(text);\n\n            if (dependent)\n            {\n                RecentBuyerText existing;\n                while (_recentBuyerTexts.TryGetValue(key, out existing) && existing != null)\n                {\n                    if (string.IsNullOrWhiteSpace(existing.AnchorText)\n                        || existing.AnchorReceivedAt == DateTime.MinValue\n                        || receivedAt - existing.AnchorReceivedAt > TimeSpan.FromSeconds(SemanticContinuationWindowSeconds))\n                    {\n                        break;\n                    }\n                    var updated = new RecentBuyerText\n                    {\n                        AnchorText = existing.AnchorText,\n                        AnchorReceivedAt = existing.AnchorReceivedAt,\n                        AnchorGeneration = existing.AnchorGeneration,\n                        LatestText = text,\n                        LatestReceivedAt = receivedAt,\n                        LatestGeneration = item.SessionGeneration\n                    };\n                    if (_recentBuyerTexts.TryUpdate(key, updated, existing)) return;\n                }\n\n                // A pure punctuation nudge has no standalone semantics and must never erase/create an anchor.\n                if (IsPunctuationOnlySemanticNudge(text)) return;\n            }\n\n            // A substantive question (or a short elliptical question with no usable predecessor) becomes\n            // the new anchor. Later punctuation/confirmation fragments can safely inherit it.\n            _recentBuyerTexts[key] = new RecentBuyerText\n            {\n                AnchorText = text,\n                AnchorReceivedAt = receivedAt,\n                AnchorGeneration = item.SessionGeneration,\n                LatestText = text,\n                LatestReceivedAt = receivedAt,\n                LatestGeneration = item.SessionGeneration\n            };\n        }\n\n        private static bool LooksLikeSemanticContinuation(string value)\n        {\n            var text = NormalizeSemanticText(value);\n            if (string.IsNullOrWhiteSpace(text) || text.Length > 32) return false;\n            if (IsPunctuationOnlySemanticNudge(text)) return true;\n\n            var compact = Regex.Replace(text.ToLowerInvariant(), @"[\\s，。！？!?、；;：:…~～]", string.Empty);\n            if (string.IsNullOrWhiteSpace(compact)) return true;\n\n            var prefixes = new[] { "这个", "这款", "这种", "这个版本", "这个型号", "那个", "那款", "那种", "它", "这", "那" };\n            if (prefixes.Any(x => compact.StartsWith(x, StringComparison.Ordinal)))\n            {\n                if (compact == "这个" || compact == "这个呢" || compact == "那个" || compact == "那个呢" || compact == "它呢") return true;\n                if (Regex.IsMatch(compact, @"支持|能用|可以|可用|适用|兼容|行吗|能不能|可不可以|怎么样|咋样|有吗|吗$|呢$")) return true;\n            }\n\n            // Predicate-only / interrogative-only short turns omit the subject by definition.\n            // They are dependent only when an anchor actually exists; RememberRecentBuyerText falls\n            // back to treating them as a new anchor when no predecessor is available.\n            return Regex.IsMatch(compact,\n                @"^(?:可以|可以吗|可以不|行|行吗|行不行|能|能吗|能用|能用吗|能不能|支持|支持吗|可用|可用吗|适用|适用吗|兼容|兼容吗|有|有吗|是吗|对吗|确定吗|真的吗|真的|好了吗|好了没|怎么样|咋样|多久|什么时候|多少钱|在哪|哪里|怎么弄|怎么用|呢)$");\n        }\n\n        private static bool IsPunctuationOnlySemanticNudge(string value)\n        {\n            var compact = Regex.Replace(NormalizeSemanticText(value), @"[\\s，。！？!?、；;：:…~～.\\-—_]+", string.Empty);\n            return compact.Length == 0;\n        }\n\n        private static string NormalizeSemanticText(string value)\n        {\n            value = (value ?? string.Empty).Replace("\\r", " ").Replace("\\n", " ").Trim();\n            value = Regex.Replace(value, @"\\s+", " ");\n            return value;\n        }\n\n'''
s = s[:start] + semantic_block + s[end:]
write(path, s)

# 2) The production streaming pipeline must actually use ModelQuestion for reasoning.
path = "src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs"
s = read(path)
s = replace_once(
    s,
    '''                    burst.CombinedQuestion,\n                    generationCts.Token,''',
    '''                    string.IsNullOrWhiteSpace(burst.ModelQuestion) ? burst.CombinedQuestion : burst.ModelQuestion,\n                    generationCts.Token,''',
    "streaming model question")
write(path, s)

# 3) The legacy fallback must use the same semantic frame.
path = "src/Bot/ChromeNs/QN.cs"
s = read(path)
s = replace_once(
    s,
    '''                    burst.CombinedQuestion,\n                    true));''',
    '''                    string.IsNullOrWhiteSpace(burst.ModelQuestion) ? burst.CombinedQuestion : burst.ModelQuestion,\n                    true));''',
    "legacy model question")
write(path, s)

# 4) Active conversation probe must not report a rejected non-buyer as a successful correction.
path = "src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs"
s = read(path)
s = replace_once(
    s,
    '''            var currentNick = qn.Buyer == null ? string.Empty : (qn.Buyer.Nick ?? string.Empty).Trim();\n            if (AreSameBuyer(seller, currentNick, firstNick))''',
    '''            var currentNick = qn.Buyer == null ? string.Empty : (qn.Buyer.Nick ?? string.Empty).Trim();\n            if (RejectNonBuyerProbe(qn, seller, first, currentNick, "first_read")) return;\n            if (AreSameBuyer(seller, currentNick, firstNick))''',
    "first probe guard")
s = replace_once(
    s,
    '''            if (!AreSameBuyer(seller, firstNick, secondNick))\n            {''',
    '''            if (RejectNonBuyerProbe(qn, seller, second, currentNick, "stable_read")) return;\n            if (!AreSameBuyer(seller, firstNick, secondNick))\n            {''',
    "second probe guard")
anchor = '''        private static bool HasVerifiedReceptionDesk(string seller)\n        {'''
helper = '''        private static bool RejectNonBuyerProbe(\n            QN qn,\n            string seller,\n            ConversationResponse response,\n            string cachedBuyer,\n            string stage)\n        {\n            if (qn == null || response == null || response.Result == null) return false;\n            string reason;\n            if (!NonBuyerConversationGuard.ShouldBlockConversation(qn.Seller, response.Result, out reason)) return false;\n\n            int failures;\n            ConsecutiveProbeFailures.TryRemove(qn, out failures);\n            BotConnectionDiagnostics.RecordCdpStatus(\n                true,\n                "当前选中的是非买家会话，保持已验证买家不变",\n                seller,\n                cachedBuyer);\n            Log.Info("当前买家主动探测识别到非买家会话，保持已验证buyer不变: seller="\n                + seller + ", cachedBuyer=" + (cachedBuyer ?? string.Empty)\n                + ", stage=" + stage + ", reason=" + reason);\n            return true;\n        }\n\n'''
s = replace_once(s, anchor, helper + anchor, "probe helper")
write(path, s)

# 5) Close stale progress cards when an unresolved turn is semantically folded into a later fragment.
path = "src/Bot/ChromeNs/ResponseProgressTracker.cs"
s = read(path)
anchor = '''        public static CtlConversation BeginAnswer(\n            string seller, string buyer, string combinedQuestion, DateTime detectedAt)'''
method = '''        public static void MarkContextualContinuationMerged(\n            string seller,\n            string buyer,\n            DateTime previousDetectedAt,\n            string currentFragment)\n        {\n            if (previousDetectedAt == DateTime.MinValue) return;\n            var turnKey = TurnKey(seller, buyer, NormalizeDetectedAt(previousDetectedAt));\n            Entry entry;\n            if (!Entries.TryGetValue(turnKey, out entry) || entry == null) return;\n\n            lock (entry.Sync)\n            {\n                // Once an answer is already ready it is historical evidence, not a pending card.\n                if (entry.AnswerReadyAt != DateTime.MinValue) return;\n                if (entry.Control != null)\n                {\n                    entry.Control.SetStatus(\n                        "买家后续发送了省略补充/催问，本条已合并到最新问题语义中，不再独立生成答案",\n                        false);\n                }\n            }\n            Entry removed;\n            Entries.TryRemove(turnKey, out removed);\n            Log.Info("未完成买家问题已并入后续省略/催问: seller=" + seller\n                + ", buyer=" + buyer + ", fragment=" + (currentFragment ?? string.Empty));\n        }\n\n'''
s = replace_once(s, anchor, method + anchor, "progress semantic merge")
write(path, s)

# 6) Generalized regression contracts.
test_path = ROOT / "tests/test_contextual_followup_coalescing_static.py"
test_path.write_text(r'''from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_contextual_followups_keep_substantive_anchor_and_support_ellipsis():
    source = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    assert "SemanticContinuationWindowSeconds = 180" in source
    assert "AnchorText" in source and "LatestGeneration" in source
    assert "IsPunctuationOnlySemanticNudge" in source
    assert '"可以吗"' in source and '"能用吗"' in source and '"多少钱"' in source
    assert "semantic_continuation_superseded" in source
    assert "previous.LatestGeneration" in source
    assert "MarkContextualContinuationMerged" in source
    assert "最近商品/图片/订单上下文" in source


def test_model_question_is_used_by_both_text_reasoning_paths():
    streaming = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    legacy = read("src/Bot/ChromeNs/QN.cs")
    assert "string.IsNullOrWhiteSpace(burst.ModelQuestion) ? burst.CombinedQuestion : burst.ModelQuestion" in streaming
    assert "string.IsNullOrWhiteSpace(burst.ModelQuestion) ? burst.CombinedQuestion : burst.ModelQuestion" in legacy


def test_premerge_has_one_authoritative_gate_and_hard_liveness_boundary():
    coordinator = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    deterministic = read("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")
    assert "_preMergeRuleGates" not in coordinator
    assert "PreMergeRuleExecutionDeadlineMilliseconds = 20000" in coordinator
    assert "Task.WhenAny(rulesTask, deadlineTask)" in coordinator
    assert "已fail-open继续普通合并链路" in coordinator
    assert "pre_merge_enqueue_exception" in coordinator
    assert "gate.WaitAsync(1800)" in deterministic


def test_non_buyer_runtime_probe_is_guarded_before_success_correction():
    monitor = read("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")
    first_guard = monitor.index('RejectNonBuyerProbe(qn, seller, first, currentNick, "first_read")')
    same = monitor.index("AreSameBuyer(seller, currentNick, firstNick)", first_guard)
    second_guard = monitor.index('RejectNonBuyerProbe(qn, seller, second, currentNick, "stable_read")')
    corrected = monitor.index('"当前买家由主动探测修正', second_guard)
    assert first_guard < same
    assert second_guard < corrected
    assert "保持已验证buyer不变" in monitor


def test_pending_progress_card_can_be_terminally_folded_into_contextual_followup():
    progress = read("src/Bot/ChromeNs/ResponseProgressTracker.cs")
    assert "public static void MarkContextualContinuationMerged" in progress
    assert "本条已合并到最新问题语义中" in progress
    assert "entry.AnswerReadyAt != DateTime.MinValue" in progress
''', encoding="utf-8")

print("contextual follow-up patch applied")
