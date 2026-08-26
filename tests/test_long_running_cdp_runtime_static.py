from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_cdp_execute_wait_does_not_block_thread_pool_workers():
    source = read("src/Bot/ChromeNs/CDPClient.cs")

    assert "TaskCompletionSource<string>" in source
    assert "TaskCreationOptions.RunContinuationsAsynchronously" in source
    assert "Task.WhenAny(requestCompletion.Task, timeoutTask)" in source
    assert "Task.Run(() => requestResetEvent.Wait" not in source
    assert "ManualResetEventSlim" not in source


def test_websocket_dispatch_is_singleton_and_does_not_root_closed_clients():
    source = read("src/Bot/ChromeNs/CDPClient.cs")

    assert "ConcurrentDictionary<string, WeakReference> SessionClients" in source
    assert "EnsureDispatcherInstalled" in source
    assert "OnRecieveMessage += DispatchWebSocketMessage" in source
    assert "OnRecieveMessage += OnWSocketRecieveMessage" not in source
    assert "weak.Target as CDPClient" in source


def test_precise_conversation_change_selects_runtime_command_webview():
    source = read("src/Bot/ChromeNs/CDPClient.cs")

    assert 'PreferRuntimeSession(sellerNick, SessionId, buyerNick, "onConversationChange")' in source
    assert "ResolvePreferredRuntimeClient" in source
    assert 'desc + "@runtime-active-session"' in source
    assert "活动CDP会话失效，已撤销会话偏好并回退权威通道" in source


def test_duplicate_status_cannot_overwrite_logical_current_buyer():
    source = read("src/Bot/ChromeNs/CDPClient.cs")

    assert "RepairDuplicateStatusDiagnostics" in source
    assert "PreferredSellerSessions" in source
    assert "BotConnectionDiagnostics.RecordBuyerSeller(sellerNick, logicalBuyer)" in source


def test_forwarded_conversation_change_does_not_revert_physical_route():
    bridge = read("src/Bot/ChromeNs/DuplicateCdpInboundRecoveryBridge.cs")

    assert "TryApplyConversationChange" in bridge
    assert 'string.Equals(item.Type, "onConversationChange", StringComparison.Ordinal)' in bridge
    assert 'qn.SetActiveConversationByNick(item.Seller, buyer, "duplicateCdpConversationChange")' in bridge
    conversation_block = bridge[bridge.index("private static bool TryDeliverLive"):bridge.index("private static void DrainPending")]
    assert "return TryApplyConversationChange(qn, item);" in conversation_block
    assert "target.DispatchInboundEvent(item.Type, item.Response);" in conversation_block
