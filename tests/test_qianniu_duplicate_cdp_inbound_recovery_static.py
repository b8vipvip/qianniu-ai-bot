from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_qianniu_system_message_window_is_never_treated_as_reception_desk():
    finder = read("src/Bot/Automation/ChatDeskNs/Automators/QnAccountFinder.cs")

    assert "IsSystemNotificationTitle" in finder
    assert 'value.Equals("千牛系统消息"' in finder
    assert 'value.Equals("千牛系统通知"' in finder
    assert 'if (IsSystemNotificationTitle(title)) return false;' in finder
    assert 'if (IsSystemNotificationTitle(nativeWindowTitle)) return string.Empty;' in finder
    assert 'if (string.IsNullOrWhiteSpace(seller)) return;' in finder

    # The explicit exclusion must happen before the broad fallback that accepts titles
    # containing “千牛/接待/客服”.
    reject = finder.index("if (IsSystemNotificationTitle(title)) return false;")
    broad = finder.index('title.IndexOf("千牛"')
    assert reject < broad


def test_duplicate_cdp_pages_forward_only_inbound_buyer_events_to_authoritative_session():
    bridge = read("src/Bot/ChromeNs/DuplicateCdpInboundRecoveryBridge.cs")
    client = read("src/Bot/ChromeNs/CDPClient.cs")
    props = read("src/Bot/Directory.Build.props")

    assert "DuplicateCdpInboundRecoveryBridge.InitializeForApp()" in bridge
    assert "MyWebSocketServer.WSocketSvrInst.OnRecieveMessage += OnWebSocketMessage" in bridge
    assert 'string.Equals(type, "receiveNewMsg"' in bridge
    assert 'string.Equals(type, "onShopRobotReceriveNewMsgs"' in bridge
    assert "onChatDlgActive/onConversationChange" in bridge
    assert "target.DispatchInboundEvent(item.Type, item.Response);" in bridge
    assert "重复千牛CDP入站消息已转交权威会话" in bridge
    assert "已补发初始化期间暂存的千牛入站消息" in bridge
    assert "ConcurrentQueue<PendingInboundEvent>" in bridge
    assert "TimeSpan.FromSeconds(15)" in bridge

    assert "internal void DispatchInboundEvent(string type, string response)" in client
    assert 'if (type == "receiveNewMsg")' in client
    assert 'else if (type == "onShopRobotReceriveNewMsgs")' in client

    assert "ChromeNs\\DuplicateCdpInboundRecoveryBridge.cs" in props


def test_duplicate_cdp_bridge_never_replaces_outbound_cdp_ownership():
    bridge = read("src/Bot/ChromeNs/DuplicateCdpInboundRecoveryBridge.cs")
    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")

    assert "qn.CDP =" not in bridge
    assert "TryClaimSellerSession" not in bridge
    assert "OpenChat(" not in bridge
    assert "SetActiveConversationByNick" not in bridge

    # Existing server still owns the one-authoritative-CDP decision.
    assert "TryClaimSellerSession" in server
    assert "重复千牛CDP会话已完成识别但不接管卖家运行通道" in server
    assert "qn.CDP = cdp;" in server
