from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_delivery_matching_uses_buyer_visible_body_without_internal_ai_marker():
    watchdog = text("src/Bot/ChromeNs/SendDeliveryWatchdog.cs")

    normalize = watchdog.index("private static string Normalize(string value)")
    strip_marker = watchdog.index("BotOutboundMessageFormatter.StripAiMarker", normalize)
    whitespace = watchdog.index("Regex.Replace(value.Trim()", strip_marker)

    assert normalize < strip_marker < whitespace
    assert "Bot echo cannot" in watchdog
    assert "manual-intervention guard" in watchdog


def test_rpa_registers_pending_delivery_before_real_send_action():
    rpa = text("src/Bot/ChromeNs/QNRpa.cs")

    pending = rpa.index("SendDeliveryWatchdog.EnsurePending(SellerNick, buyer, text)")
    real_send = rpa.index("TrySendTextNativeFirstAsync(buyer, text, sendStart)", pending)

    assert pending < real_send


def test_manual_intervention_still_requires_bot_delivery_checks_to_fail_first():
    monitor = text("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")

    confirm = monitor.index("TryConfirmBotDelivery(seller, buyer, texts)")
    marker = monitor.index("texts.FirstOrDefault(IsExplicitBotAuthoredReply)", confirm)
    cancel = monitor.index("CancelActiveBuyerGeneration", marker)
    manual = monitor.index("ResponseProgressTracker.MarkManualIntervention", cancel)

    assert confirm < marker < cancel < manual


def test_first_inquiry_and_segmented_order_replies_share_the_reliable_send_path():
    bridge = text("src/Bot/ChromeNs/FirstInquiryDeliveryBridge.cs")
    qn = text("src/Bot/ChromeNs/QN.cs")

    assert "await qn.SendTextWithRetryAsync(buyer, greeting, 1)" in bridge
    assert 'const string segmentToken = "{分段符}"' in qn
    assert "await SendTextWithRetryAsync(buyer, segment, retryCount)" in qn
