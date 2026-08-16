from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_first_inquiry_guard_is_compiled_and_wraps_live_runtime_handler():
    props = read("src/Bot/Directory.Build.props")
    guard = read("src/Bot/ChromeNs/FirstInquiryStreamingGuard.cs")
    streaming = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")

    assert "ChromeNs\\FirstInquiryStreamingGuard.cs" in props
    assert "_firstInquiryStreamingGuardBootstrap" in guard
    assert "_buyerMessageBurstCoordinator" in guard
    assert '"_handler"' in guard
    assert "ReferenceEquals(current, state.Wrapped)" in guard
    assert "HandleAsync(qn, downstream, lease)" in guard

    # The bug existed because this runtime wrapper sends ordinary text directly into
    # ProcessTextBurstStreamingAsync instead of QN.ProcessTextBurstAsync.
    assert "ProcessTextBurstStreamingAsync(qn, lease)" in streaming
    assert "wrapped = lease => HandleAsync(qn, original, lease)" in streaming


def test_first_inquiry_is_resolved_before_any_ai_or_vision_downstream():
    guard = read("src/Bot/ChromeNs/FirstInquiryStreamingGuard.cs")

    resolve = guard.index("FirstInquiryFixedReplyService.TryResolve(")
    downstream_after_resolve = guard.index("await downstream(lease);", resolve)
    ownership_comment = guard.index("From this point the first-inquiry reply owns this burst", resolve)

    assert resolve < downstream_after_resolve < ownership_comment
    assert "FirstInquiryFixedReplyService.HasPending(" not in guard
    assert "burst.LatestVisionItem != null" not in guard[:resolve]
    assert "首条咨询固定回复已在AI路由前命中" in guard
    assert "未调用AI" in guard


def test_latest_burst_can_reacquire_after_an_older_burst_is_superseded():
    guard = read("src/Bot/ChromeNs/FirstInquiryStreamingGuard.cs")

    assert "Do not gate this with HasPending" in guard
    assert "latest burst must be able to reconstruct and acquire" in guard
    assert "FirstInquiryFixedReplyService.ReleaseReservation(" in guard
    assert "FirstInquiryFixedReplyService.TryResolve(" in guard


def test_first_inquiry_delivery_commits_only_after_send_and_releases_on_failure():
    guard = read("src/Bot/ChromeNs/FirstInquiryStreamingGuard.cs")

    send = guard.index("qn.SendTextWithRetryAsync(")
    mark = guard.index("FirstInquiryFixedReplyService.MarkDelivered(", send)
    remember = guard.index("ReplyDeduplicationService.RememberDelivered(", mark)
    release = guard.index("FirstInquiryFixedReplyService.ReleaseReservation(", remember)

    assert send < mark < remember < release
    assert "if (sendOk)" in guard[send:mark]
    assert "lease.ConfirmStableAsync(180)" in guard
    assert "if (!delivered)" in guard[remember:release]
    assert "买家补充新消息，释放旧首条咨询预留给最新一轮" in guard


def test_guard_keeps_rewrapping_after_other_runtime_pipelines_change_handler():
    guard = read("src/Bot/ChromeNs/FirstInquiryStreamingGuard.cs")

    assert "new Timer(_ => PatchExisting(), null, 80, 250)" in guard
    assert "var downstream = current;" in guard
    assert "handlerField.SetValue(coordinator, wrapped)" in guard
    assert "Guards[key] = new GuardState { Wrapped = wrapped };" in guard
    assert "当前消息处理链最外层" in guard
