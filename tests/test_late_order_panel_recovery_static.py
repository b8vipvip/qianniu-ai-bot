from pathlib import Path


SOURCE = Path("src/Bot/ChromeNs/FirstInquiryDeliveryBridge.cs")


def _source() -> str:
    return SOURCE.read_text(encoding="utf-8-sig")


def test_background_order_panel_recovery_is_bootstrapped():
    text = _source()
    assert "BackgroundOrderPanelRecoveryBridge.InitializeForApp()" in text
    assert "EvShopRobotReceriveNewMessage += Qn_EvShopRobotReceriveNewMessage" in text
    assert "TryRecoverVisibleOrderPanelForBackgroundProbeAsync" in text


def test_probe_window_covers_late_created_and_paid_orders():
    text = _source()
    # Production incident 2026-08-18: buyer activity was at ~13:34:00,
    # order creation at 13:35:58 and payment at 13:36:01. Keep probes
    # beyond two minutes so a missing Qianniu order push can still be
    # recovered from the visible order panel.
    for delay in (60000, 90000, 120000, 125000, 135000, 150000, 180000):
        assert str(delay) in text
    assert "后台订单面板延迟补扫结束：180秒内未发现可确认的新订单" in text


def test_late_probes_never_steal_conversation_focus():
    text = _source()
    assert "private const int LastAutoFocusProbeMs = 36000;" in text
    assert "targetDelay >= 3200 && targetDelay <= LastAutoFocusProbeMs" in text
    # The QN helper must only call OpenChat behind mayActivateBuyer.
    guard = text.index("if (!targetActive && mayActivateBuyer)")
    open_chat = text.index("OpenChat(openNick)", guard)
    assert open_chat > guard


def test_recovered_panel_order_reenters_normal_order_hub():
    text = _source()
    assert 'Source = "千牛右侧订单面板后台延迟兜底"' in text
    assert "var publish = OrderEventHub.Publish(snapshot);" in text
    assert "candidate.PaidAt.HasValue" in text
    assert "OrderEventType.Paid : OrderEventType.Created" in text
