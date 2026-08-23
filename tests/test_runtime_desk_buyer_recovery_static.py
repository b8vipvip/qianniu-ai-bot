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


def test_active_conversation_event_can_promote_its_source_cdp():
    bridge = read("src/Bot/ChromeNs/DuplicateCdpInboundRecoveryBridge.cs")
    client = read("src/Bot/ChromeNs/CDPClient.cs")

    assert 'string.Equals(type, "onChatDlgActive"' in bridge
    assert 'string.Equals(type, "onConversationChange"' in bridge
    assert "CDPClient.FindBySessionId(sessionId)" in bridge
    assert "qn.CDP = source;" in bridge
    assert '"activeConversationSession:" + type' in bridge
    assert "sourceSeller.Length > 0" in bridge
    assert "!string.Equals(sourceSeller, seller, StringComparison.Ordinal)" in bridge

    assert "ClientsBySession" in client
    assert "internal static CDPClient FindBySessionId" in client
    assert "ClientsBySession[session.SessionID] = this" in client


def test_status_only_pages_are_not_used_as_active_chat_evidence_by_bridge():
    bridge = read("src/Bot/ChromeNs/DuplicateCdpInboundRecoveryBridge.cs")

    status_pos = bridge.index('string.Equals(e.Type, "qnbotStatus"')
    active_pos = bridge.index("IsActiveConversationType(e.Type)")
    assert status_pos < active_pos
    status_block = bridge[status_pos:active_pos]
    assert "TryPromoteActiveConversationSession" not in status_block
