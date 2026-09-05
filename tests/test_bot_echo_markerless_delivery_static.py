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


def test_human_observation_still_requires_bot_delivery_checks_to_fail_first():
    monitor = text("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")

    confirm = monitor.index("TryConfirmBotDelivery(seller, buyer, texts)")
    marker = monitor.index("texts.FirstOrDefault(IsExplicitBotAuthoredReply)", confirm)
    manual = monitor.index("ResponseProgressTracker.MarkManualIntervention", marker)

    assert confirm < marker < manual
    assert "CancelActiveBuyerGeneration" not in monitor
    assert "Bot任务继续" in monitor


def test_first_inquiry_and_segmented_order_replies_share_the_reliable_send_path():
    deterministic = text("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")
    qn = text("src/Bot/ChromeNs/QN.cs")

    assert "FirstInquiryFixedReplyService.TryResolve" in deterministic
    assert "await qn.SendTextWithRetryAsync(item.BuyerNick, answer, 3, generationToken)" in deterministic
    assert 'const string segmentToken = "{分段符}"' in qn
    assert "await SendTextWithRetryAsync(buyer, segment, retryCount, cancellationToken)" in qn
