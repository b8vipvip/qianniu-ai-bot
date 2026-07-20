from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def between(text: str, start: str, end: str) -> str:
    i = text.index(start)
    j = text.index(end, i)
    return text[i:j]


def test_reliable_send_uses_stable_automation_ids_and_exact_draft_check():
    source = read("src/Bot/ChromeNs/QNRpa.ReliableSend.cs")
    qnrpa = read("src/Bot/ChromeNs/QNRpa.cs")
    assert "chatInputArea.plainTextEdit" in source
    assert "enterAreaKeyWidget.sendMsg" in source
    assert "RefreshChatControlsAsync" in source
    assert "EditorMatchesExpectedText" in source
    assert "TryGetEditorText" in source
    assert "LastSendFailureReason" in source
    assert "await RefreshChatControlsAsync(true)" in qnrpa
    assert "SetSendFailure" in qnrpa


def test_background_notification_only_recovers_when_detailed_event_is_missing():
    source = read("src/Bot/ChromeNs/QN.MessageRecovery.cs")
    qn = read("src/Bot/ChromeNs/QN.cs")
    assert "Task.Delay(1800)" in source
    assert "_latestBuyerMessageObserved" in source
    assert "im.singlemsg.GetRemoteHisMsg" in source
    assert "await _sendGate.WaitAsync()" in source
    assert "await ProcessIncomingMessageAsync(message)" in source
    assert "ScheduleBackgroundMessageRecovery(e)" in qn
    assert "MarkBuyerMessageObserved(sellerNick, buyerNick)" in qn


def test_settings_have_separate_notification_tab_and_order_section():
    source = read("src/Bot/Options/CtlRobotOptions.xaml.cs")
    assert 'Header = "消息通知"' in source
    rules = between(source, "private UIElement BuildRulesTab()", "private UIElement BuildNotificationTab()")
    notifications = between(source, "private UIElement BuildNotificationTab()", "private TextBlock SectionTitle")
    assert "买家下单后自动发送" in rules
    assert "人工客服工作时间与下班回复" not in rules
    assert "转人工通知" not in rules
    assert "人工客服工作时间与下班回复" in notifications
    assert "转人工通知" in notifications
    assert "调用HTTP接口" in rules
    assert "OrderPlacedReplyText" in source
    assert "OrderPlacedApiUrl" in source
    assert "OrderPlacedApiToken" in source


def test_order_reply_detects_order_card_and_supports_http_contract():
    source = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    qn = read("src/Bot/ChromeNs/QN.cs")
    assert "buyer_order_created" in source
    assert 'payload["orderId"]' not in source  # object initializer is used, not mutable shared payload
    assert '["orderId"] = plan.OrderId' in source
    assert 'token["reply"]' in source
    assert 'token["answer"]' in source
    assert 'token["message"]' in source
    assert "{订单号}" in source
    assert "OrderPlacedAutoReplyService.TryCreatePlan" in qn
    order_index = qn.index("OrderPlacedAutoReplyService.TryCreatePlan")
    safety_index = qn.index("IncomingMessageSafety.Evaluate", order_index)
    assert order_index < safety_index


def test_new_partial_files_are_in_windows_build():
    targets = read("src/Directory.Build.targets")
    assert "QNRpa.ReliableSend.cs" in targets
    assert "QN.MessageRecovery.cs" in targets
    assert "OrderPlacedAutoReplyService.cs" in targets
