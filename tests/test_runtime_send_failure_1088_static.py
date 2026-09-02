from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_hwnd_safe_send_accepts_only_process_owned_verified_point():
    source = read("src/Bot/ChromeNs/QNRpa.cs")
    native = read("src/Bot/ChromeNs/QNRpa.NativeSend.cs")
    assert "_sendMessageButton.AsButton().Invoke()" not in source
    assert "GetWindowThreadProcessId(target, out targetPid)" in native
    assert "targetPid != expectedPid" in native
    assert "安全点窗口不属于当前卖家千牛进程" in native
    assert "允许同一千牛进程的独立根窗口" not in native
    assert "HWND安全发送已阻止跨根窗口点击" in native
    process_guard = native.split("GetWindowThreadProcessId(target, out targetPid)", 1)[1].split(
        "ScreenToClient", 1
    )[0]
    assert process_guard.index("targetPid != expectedPid") < process_guard.index("root != expectedRoot")
    assert "return false;" in process_guard.split("targetPid != expectedPid", 1)[1].split("var root", 1)[0]
    sibling_root_guard = process_guard.split("root != expectedRoot", 1)[1]
    assert "拒绝向未知根窗口投递点击" in sibling_root_guard
    assert "return false;" in sibling_root_guard


def test_unchanged_failed_bot_draft_does_not_become_fake_human_activity():
    rpa = read("src/Bot/ChromeNs/QNRpa.cs")
    queue = read("src/Bot/ChromeNs/NewOrderAttentionQueue.cs")
    assert "internal bool IsKnownBotOwnedDraftText(string currentText)" in rpa
    assert "EditorMatchesExpectedText(currentText, expected)" in rpa
    assert "!string.IsNullOrWhiteSpace(LastSendFailureReason)" in rpa
    assert "!LastSendWasCancelled" in rpa
    assert "internal async Task<bool> IsKnownBotOwnedDraftAsync()" in rpa
    first_guard = queue.split("if (!input.Empty)", 1)[1].split(
        "var current = await TryGetCurrentBuyerAsync", 1
    )[0]
    assert "IsKnownBotOwnedDraftAsync" in first_guard
    assert first_guard.index("IsKnownBotOwnedDraftAsync") < first_guard.index("MarkHumanInteraction")
    assert "不计为人工操作" in first_guard


def test_failed_generation_is_terminal_after_deterministic_and_ai_paths():
    source = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    agent = read("src/Bot/ChromeNs/BuyerSessionAgent.cs")
    deterministic = source.split("BuyerSessionAgentState deterministicState", 1)[1].split(
        "private void AttachSemanticContinuation", 1
    )[0]
    assert "TryGetGenerationState" in deterministic
    assert "deterministicState == BuyerSessionAgentState.Failed" in deterministic
    assert "固定规则发送失败后保留Failed终态" in deterministic

    dispatch = source.split("BuyerSessionAgentState generationState", 1)[1].split(
        "catch (OperationCanceledException)", 1
    )[0]
    assert "TryGetGenerationState" in dispatch
    assert "generationState == BuyerSessionAgentState.Failed" in dispatch
    failed_branch = dispatch.split("if (failed)", 1)[1].split("else if", 1)[0]
    assert "MarkCompleted" not in failed_branch
    assert "回复管线返回时会话已是Failed" in failed_branch
    assert "Dictionary<long, BuyerSessionAgentState> GenerationStates" in agent
    assert "SetGenerationStateLocked(state, generation, next)" in agent


def test_duplicate_conversation_change_cannot_take_outbound_cdp_route():
    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    client = read("src/Bot/ChromeNs/CDPClient.cs")
    assert "internal bool IsAuthoritativeSellerSession" in server
    switched = client.split("private void BuyerSwitched(string response)", 1)[1].split(
        "private void SellerSwitched", 1
    )[0]
    assert "server.IsAuthoritativeSellerSession(sellerNick, SessionId)" in switched
    assert "PreferRuntimeSession" in switched
    assert "不接管CDP命令路由" in switched
