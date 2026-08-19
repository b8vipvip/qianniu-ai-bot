from pathlib import Path


SOURCE = Path("src/Bot/ChromeNs/OrderPaymentNotificationFallback.cs")


def _source() -> str:
    return SOURCE.read_text(encoding="utf-8-sig")


def test_background_order_panel_recovery_is_bootstrapped():
    text = _source()
    assert "internal static class OrderAutomationCoordinator" in text
    assert "OrderAutomationCoordinator.Attach(qn)" in text
    assert "EvShopRobotReceriveNewMessage += OnShopRobotNewMessage" in text
    assert "EvBuyerSwitched += OnBuyerSwitched" in text
    assert "TryRecoverVisibleOrderPanelForCoordinatorAsync" in text


def test_probe_window_covers_late_created_and_paid_orders():
    text = _source()
    for delay in (60000, 90000, 120000, 150000, 180000):
        assert str(delay) in text
    assert "订单入站统一协调器补扫结束：180秒内未发现可确认的新订单" in text
    assert "probeStartedAt" in text
    assert "AddSeconds(-20)" in text


def test_late_probes_never_steal_conversation_focus():
    text = _source()
    assert "private const int LastSafeAutoFocusProbeMs = 36000;" in text
    assert "targetDelay >= 3200 && targetDelay <= LastSafeAutoFocusProbeMs" in text
    # Only the bounded early window may ask the coordinator to focus a target buyer.
    assert "if (!mayFocus) return false;" in text
    guard = text.index("if (!mayFocus) return false;")
    open_chat = text.index("qn.OpenChat(openNick)", guard)
    assert open_chat > guard
    assert "BotActivityCoordinator.IsSafeToAutoFocus" in text[guard:open_chat]


def test_recovered_panel_order_reenters_normal_order_hub():
    text = _source()
    assert 'Source = "千牛右侧订单面板统一协调补偿"' in text
    assert "var publish = OrderEventHub.Publish(snapshot);" in text
    assert "candidate.PaidAt.HasValue" in text
    assert "OrderEventType.Paid : OrderEventType.Created" in text
    probe = text[text.index("internal async Task<bool> TryRecoverVisibleOrderPanelForCoordinatorAsync"):]
    assert "ProcessOrderPlacedReplyAsync" not in probe
    assert "SendTextWithRetryAsync" not in probe
