from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_exact_verified_send_button_invoke_is_safe_and_non_physical():
    source = read("src/Bot/ChromeNs/QNRpa.cs")
    assert "private bool TryInvokeExactVerifiedSendButtonNow()" in source
    block = source.split("private bool TryInvokeExactVerifiedSendButtonNow()", 1)[1].split(
        "private bool TryInvokeCachedSendButtonNow()", 1
    )[0]
    assert "SendButtonAutomationId" in block
    assert "IsSendButtonName(name)" in block
    assert "_sendMessageButton.AsButton().Invoke()" in block
    assert "arrow" in block
    assert "dropdown" in block
    assert "下拉" in block
    fallback = source.split("private bool TryInvokeCachedSendButtonNow()", 1)[1].split(
        "private bool TryInvokeSafeMainSendCandidate", 1
    )[0]
    assert "if (TryInvokeExactVerifiedSendButtonNow()) return true;" in fallback


def test_unchanged_failed_bot_draft_does_not_become_fake_human_activity():
    rpa = read("src/Bot/ChromeNs/QNRpa.cs")
    queue = read("src/Bot/ChromeNs/NewOrderAttentionQueue.cs")
    assert "internal bool IsKnownBotOwnedDraftText(string currentText)" in rpa
    assert "EditorMatchesExpectedText(currentText, expected)" in rpa
    assert "internal async Task<bool> IsKnownBotOwnedDraftAsync()" in rpa
    first_guard = queue.split("if (!input.Empty)", 1)[1].split(
        "var current = await TryGetCurrentBuyerAsync", 1
    )[0]
    assert "IsKnownBotOwnedDraftAsync" in first_guard
    assert first_guard.index("IsKnownBotOwnedDraftAsync") < first_guard.index("MarkHumanInteraction")
    assert "不计为人工操作" in first_guard


def test_failed_generation_is_terminal_after_deterministic_and_ai_paths():
    source = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    deterministic = source.split("deterministicSnapshot", 1)[1].split(
        "private bool HasPendingBuyerMessages", 1
    )[0]
    assert "BuyerSessionAgentState.Failed" in deterministic
    assert "固定规则发送失败后保留Failed终态" in deterministic

    dispatch = source.split("var failed = snapshot != null", 1)[1].split(
        "catch (OperationCanceledException)", 1
    )[0]
    assert "BuyerSessionAgentState.Failed" in dispatch
    failed_branch = dispatch.split("if (failed)", 1)[1].split("else if", 1)[0]
    assert "MarkCompleted" not in failed_branch
    assert "回复管线返回时会话已是Failed" in failed_branch


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
