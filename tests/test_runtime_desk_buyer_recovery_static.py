from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_qianniu_window_scan_does_not_use_cross_process_wm_gettext():
    finder = read("src/Bot/Automation/ChatDeskNs/Automators/QnAccountFinder.cs")
    scanner = read("src/Bot/ControllerNs/DeskScanner.cs")

    assert "GetWindowTextW" in finder
    assert "GetWindowTextLengthW" in finder
    assert "ReadNativeWindowTitle" in finder
    assert "WinApi.GetText(" not in finder

    assert "QnAccountFinder.ReadNativeWindowTitle" in scanner
    assert "WinApi.GetText(" not in scanner
    assert "string.IsNullOrWhiteSpace(title)) return true" in scanner


def test_precise_conversation_change_is_forwarded_without_taking_cdp_ownership():
    bridge = read("src/Bot/ChromeNs/DuplicateCdpInboundRecoveryBridge.cs")
    client = read("src/Bot/ChromeNs/CDPClient.cs")

    assert 'string.Equals(type, "onConversationChange"' in bridge
    assert 'string.Equals(type, "onChatDlgActive"' not in bridge.split("return string.Equals(type, \"receiveNewMsg\"")[1]
    assert "target.DispatchInboundEvent(item.Type, item.Response);" in bridge
    assert "qn.CDP =" not in bridge
    assert "OpenChat(" not in bridge
    assert "SetActiveConversationByNick" not in bridge

    # The authoritative CDP already knows how to turn the forwarded state event into BuyerSwitched.
    assert 'else if (type == "onConversationChange")' in client
    assert "BuyerSwitched(response);" in client


def test_status_and_polled_chat_active_events_cannot_override_current_buyer():
    bridge = read("src/Bot/ChromeNs/DuplicateCdpInboundRecoveryBridge.cs")

    assert 'string.Equals(e.Type, "qnbotStatus"' in bridge
    assert "ObserveStatusSeller(sessionId, e.Value);" in bridge
    assert "onChatDlgActive may be synthesized by periodic page polling" in bridge
    recoverable = bridge[bridge.index("private static bool IsRecoverableInboundType"):bridge.index("private static void ObserveStatusSeller")]
    assert 'string.Equals(type, "onChatDlgActive"' not in recoverable
