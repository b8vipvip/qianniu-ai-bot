from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_returning_buyer_over_10_minutes_gets_first_reply_again():
    source = read("src/Bot/ChromeNs/ReturningBuyerFirstReplyBridge.cs")
    assert "ReturningBuyerIdleMinutes = 10" in source
    assert "ExistingSessionResetMinutes = 30" in source
    assert "ConversationContextStore.GetRecentTurns" in source
    assert "idle.TotalMinutes > ReturningBuyerIdleMinutes" in source
    assert "idle.TotalMinutes < ExistingSessionResetMinutes" in source
    assert "FirstInquiryFixedReplyService.Load(seller)" in source
    assert "qn.SendTextWithRetryAsync(buyer, answer, 1)" in source
    assert "FirstInquiryFixedReplyService.MarkDelivered(seller, buyer)" in source


def test_returning_bridge_only_handles_buyer_to_seller_messages_and_deduplicates():
    source = read("src/Bot/ChromeNs/ReturningBuyerFirstReplyBridge.cs")
    assert "!string.Equals(to, seller, StringComparison.Ordinal)" in source
    assert "string.Equals(from, seller, StringComparison.Ordinal)" in source
    assert "Reservations.TryAdd(key, DateTime.Now)" in source
    assert "FirstInquiryFixedReplyService.HasPending(seller, buyer)" in source
    assert "Reservations.TryRemove(reservationKey" in source


def test_returning_bridge_is_bootstrapped_into_app():
    source = read("src/Bot/ChromeNs/ReturningBuyerFirstReplyBridge.cs")
    assert "public partial class App" in source
    assert "ReturningBuyerFirstReplyBridge.InitializeForApp()" in source
    assert "EvRecieveNewMessage += Qn_EvRecieveNewMessage" in source
