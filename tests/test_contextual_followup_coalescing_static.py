from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_contextual_followups_keep_substantive_anchor_and_support_ellipsis():
    source = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    assert "SemanticContinuationWindowSeconds = 180" in source
    assert "AnchorText" in source and "LatestGeneration" in source
    assert "IsPunctuationOnlySemanticNudge" in source
    assert '可以|可以吗|可以不' in source
    assert '能|能吗|能用|能用吗|能不能' in source
    assert '多久|什么时候|多少钱|在哪|哪里' in source
    assert "semantic_continuation_superseded" in source
    assert "previous.LatestGeneration" in source
    assert "MarkContextualContinuationMerged" in source
    assert "最近商品/图片/订单上下文" in source


def test_model_question_is_used_by_both_text_reasoning_paths():
    streaming = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    legacy = read("src/Bot/ChromeNs/QN.cs")
    assert "string.IsNullOrWhiteSpace(burst.ModelQuestion) ? burst.CombinedQuestion : burst.ModelQuestion" in streaming
    assert "string.IsNullOrWhiteSpace(burst.ModelQuestion) ? burst.CombinedQuestion : burst.ModelQuestion" in legacy


def test_premerge_has_authoritative_bounded_gates_and_no_late_send_ai_race():
    coordinator = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    deterministic = read("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")
    assert "_preMergeRuleGates" not in coordinator
    assert "PreMergeRuleExecutionDeadlineMilliseconds" not in coordinator
    assert "Task.WhenAny(rulesTask, deadlineTask)" not in coordinator
    assert "await DeterministicAutoReplyService.HandleBeforeMergeAsync(" in coordinator
    assert "pre_merge_enqueue_exception" in coordinator

    # Off-hours owns its own fail-closed gate before the ordinary work-hours gate. Normal fixed
    # rules retain one bounded per-buyer serialization gate so no late duplicate sender races AI.
    offhours = deterministic.index("TryResolveOffHours(out offHoursReply)")
    offhours_gate = deterministic.index("OffHoursGates.GetOrAdd", offhours)
    ordinary_gate = deterministic.index("BuyerGates.GetOrAdd")
    assert offhours < ordinary_gate
    assert "gate.WaitAsync(1800)" in deterministic
    assert "下班独占串行门等待超时，已fail-closed阻止Knowledge/AI链路" in deterministic
    assert "return false;" in deterministic[offhours_gate:deterministic.index("private static async Task<bool> HandleScopedBeforeMergeAsync")]


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