from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_qianniu_system_message_and_workbench_shells_are_never_treated_as_reception_desk():
    finder = read("src/Bot/Automation/ChatDeskNs/Automators/QnAccountFinder.cs")

    assert "IsSystemNotificationTitle" in finder
    assert 'value.Equals("千牛系统消息"' in finder
    assert 'value.Equals("千牛系统通知"' in finder
    assert "IsNonReceptionWorkbenchTitle" in finder
    assert 'value.IndexOf("千牛登录"' in finder
    assert 'value.Equals("千牛工作台"' in finder

    resolver = finder[finder.index("public static string ResolveSellerNameForWindow"):finder.index("private static IList<QN> GetRuntimeQns")]
    assert "IsSystemNotificationTitle(nativeWindowTitle)" in resolver
    assert "IsNonReceptionWorkbenchTitle(nativeWindowTitle)" in resolver
    assert "return string.Empty;" in resolver

    candidate = finder[finder.index("private static bool IsReceptionCandidate"):finder.index("private static int GetReceptionCandidateCount")]
    assert "IsSystemNotificationTitle(title)" in candidate
    assert "IsNonReceptionWorkbenchTitle(title)" in candidate
    assert 'title.IndexOf("千牛"' not in candidate
    assert 'title.IndexOf("接待"' in candidate
    assert 'title.IndexOf("客服"' in candidate
    assert 'if (string.IsNullOrWhiteSpace(seller)) return;' in finder


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
