from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_runtime_monitor_actively_polls_and_stably_repairs_current_buyer():
    source = text("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")

    assert "ConversationProbeIntervalMilliseconds = 2500" in source
    assert "ProbeCurrentConversationAsync" in source
    assert source.count("await qn.GetCurrentConversationID()") >= 2
    assert "await Task.Delay(220)" in source
    assert '"runtimeConversationProbe"' in source
    assert "BuyerIdentityAliasService.AreEquivalent" in source
    assert "当前买家由主动探测修正" in source
    assert "SetActiveConversationByNick" in source
    assert "人工回复只作为学习证据，不取消Bot任务" in source


def test_runtime_monitor_emits_periodic_liveness_heartbeat_and_failure_details():
    source = text("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")

    assert "HeartbeatIntervalSeconds = 60" in source
    assert "Bot运行心跳:" in source
    assert "当前买家主动探测失败" in source
    assert "当前买家主动探测已恢复" in source
    assert "diagnosticsAgeSeconds=" in source


def test_cdp_execute_requests_are_serialized_and_timeout_invalidates_session():
    source = text("src/Bot/ChromeNs/CDPClient.cs")

    assert "SemaphoreSlim _executeGate" in source
    assert "await _executeGate.WaitAsync().ConfigureAwait(false)" in source
    assert "_executeGate.Release()" in source
    assert "CDP调用超时" in source
    assert "InvalidateSession(\"调用超时:" in source
    assert "_webSocketSession.Close()" in source
    assert "CDP会话已失效并请求WebSocket重连" in source


def test_even_non_generic_execute_calls_consume_their_response_channel():
    source = text("src/Bot/ChromeNs/CDPClient.cs")

    assert "private string SendExecuteAndWait" in source
    assert 'SendExecuteAndWait(cmd, "Invoke:" + apiName)' in source
    assert 'SendExecuteAndWait(cmd, "InvokeMTop:" + apiName)' in source
    assert "旧实现允许多个异步调用并发" in source
