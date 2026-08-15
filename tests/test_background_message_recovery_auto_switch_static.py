from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_background_recovery_waits_are_bounded_and_retryable():
    source = read("src/Bot/ChromeNs/QN.MessageRecovery.cs")

    assert "BackgroundRecoverySendGateWaitMs" in source
    assert "BackgroundRecoveryGateWaitMs" in source
    assert "BackgroundRecoveryMaxAttempts" in source
    assert "_sendGate.WaitAsync(BackgroundRecoverySendGateWaitMs)" in source
    assert "_backgroundRecoveryGate.WaitAsync(BackgroundRecoveryGateWaitMs)" in source
    assert "后台消息补偿暂未取得安全切换机会，将重试" in source
    assert "await _backgroundRecoveryGate.WaitAsync();" not in source
    assert "await _sendGate.WaitAsync();" not in source


def test_missing_detailed_event_auto_switches_and_refetches_history():
    source = read("src/Bot/ChromeNs/QN.MessageRecovery.cs")

    assert "BackgroundRecoveryInitialDelayMs = 1000" in source
    assert "Bot准备自动切换目标买家并补抓历史" in source
    assert "OpenChat(buyer);" in source
    assert '"backgroundRecoveryAutoSwitch"' in source
    assert 'cdp.Invoke<JObject>("im.singlemsg.GetRemoteHisMsg"' in source
    assert "后台消息补偿已自动切换到目标买家" in source


def test_recovery_does_not_hold_navigation_locks_while_processing_answers():
    source = read("src/Bot/ChromeNs/QN.MessageRecovery.cs")

    release_pos = source.index("if (recoveryGateAcquired) _backgroundRecoveryGate.Release();")
    process_pos = source.index("ProcessRecoveredMessageWithKnownBuyerAsync")
    assert release_pos < process_pos
