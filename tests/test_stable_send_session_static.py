from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_transient_empty_current_buyer_is_retried_without_relaxing_cross_buyer_guard():
    source = text("src/Bot/ChromeNs/QNRpa.cs")

    assert "for (var attempt = 0; attempt < 7; attempt++)" in source
    assert "会话确认暂时为空，等待稳定" in source
    assert "会话持续为空，重新打开目标买家后再次确认" in source
    assert "for (var attempt = 0; attempt < 5; attempt++)" in source
    assert "if (!string.IsNullOrWhiteSpace(currentNick))" in source
    assert "目标买家=" in source
    assert "BuyerIdentityAliasService.AreEquivalent" in source


def test_stale_answer_retry_is_cancelled_and_draft_is_cleared():
    qnrpa = text("src/Bot/ChromeNs/QNRpa.cs")
    runtime = text("src/Bot/ChromeNs/QN.RuntimeSafety.cs")

    assert "HasBuyerMessageAfter" in runtime
    assert "AnswerAttemptStartedAt" in qnrpa
    assert "VerifyAnswerFreshness" in qnrpa
    assert "旧答案发送/重试已取消" in qnrpa
    assert "发送前答案时效检查" in qnrpa
    assert "ClearExpectedDraft" in qnrpa


def test_mandatory_order_preset_keeps_priority_when_buyer_sends_follow_up():
    qnrpa = text("src/Bot/ChromeNs/QNRpa.cs")
    tracker = text("src/Bot/ChromeNs/ResponseProgressTracker.cs")

    assert "IsMandatoryOrderAnswer" in tracker
    assert 'IndexOf("下单自动回复"' in tracker
    assert "ResponseProgressTracker.IsMandatoryOrderAnswer" in qnrpa
    assert "下单固定预设受保护" in qnrpa
    assert qnrpa.index("ResponseProgressTracker.IsMandatoryOrderAnswer") < qnrpa.index("HasBuyerMessageAfter")


def test_delivery_watchdog_starts_only_after_session_and_draft_checks():
    qnrpa = text("src/Bot/ChromeNs/QNRpa.cs")
    watchdog = text("src/Bot/ChromeNs/SendDeliveryWatchdog.cs")

    ensure_index = qnrpa.index("SendDeliveryWatchdog.EnsurePending")
    session_index = qnrpa.index('VerifyCurrentBuyerAsync(buyer, "发送前会话确认")')
    draft_index = qnrpa.index("if (!HasExpectedDraft(text))")
    click_index = qnrpa.index("sendResult = TryClickSendButton")

    assert session_index < ensure_index < click_index
    assert draft_index < ensure_index
    assert "已准备真实发送回显监控（尚未开始计时）" in watchdog
    assert "Interlocked.CompareExchange(ref pending.Started, 1, 0)" in watchdog
    assert "pair.Value.Started == 0" in watchdog


def test_late_seller_echo_recovers_original_reply_card():
    watchdog = text("src/Bot/ChromeNs/SendDeliveryWatchdog.cs")
    tracker = text("src/Bot/ChromeNs/ResponseProgressTracker.cs")

    assert "ResponseProgressTracker.MarkDeliveryConfirmed" in watchdog
    assert "DeliveryUi" in tracker
    assert "回复卡片已按卖家回显恢复为发送成功" in tracker
    assert "BotConnectionDiagnostics.RecordSendAttempt(true" in tracker


def test_send_diagnostic_timeout_is_non_fatal():
    source = text("src/Bot/ChromeNs/SendFailureAnomalyService.cs")

    assert "catch (TaskCanceledException)" in source
    assert "AI诊断超时，已保留本地规则诊断" in source
    assert "发送失败AI诊断超时，不影响Bot运行" in source
