from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_generation_deadline_watchdog_uses_dedicated_background_thread():
    source = text("src/Bot/ChromeNs/BuyerSessionAgentRuntimeBridge.cs")

    assert "AbsoluteGenerationAgeSeconds = 55" in source
    assert "DeadlineWatchdogSleepMilliseconds = 250" in source
    assert "new Thread(GenerationDeadlineWatchdogLoop)" in source
    assert "IsBackground = true" in source
    assert 'Name = "Qianniu.GenerationDeadlineWatchdog"' in source
    assert "Thread.Sleep(DeadlineWatchdogSleepMilliseconds)" in source


def test_generation_watchdog_tracks_each_generation_and_hard_cancels_late_work():
    source = text("src/Bot/ChromeNs/BuyerSessionAgentRuntimeBridge.cs")

    assert "BuyerSessionEventKind.BuyerActionAccepted" in source
    assert "Agent.TryGetGenerationState" in source
    assert "state == BuyerSessionAgentState.Generating" in source
    assert "GenerationGeneratingSinceUtc.TryAdd(watchKey, now)" in source
    assert "elapsed.TotalSeconds <= AbsoluteGenerationAgeSeconds" in source
    assert '"absolute_generation_age_exceeded"' in source
    assert "Agent.Cancel(" in source
    assert "禁止迟到结果进入Ready/Sending" in source


def test_generation_watchdog_is_not_triggered_by_human_reply_and_drops_terminal_watches():
    source = text("src/Bot/ChromeNs/BuyerSessionAgentRuntimeBridge.cs")

    assert "WatchSession(seller, buyer);" in source
    assert "SellerHumanReply" in source
    assert "false);" in source
    assert "state == BuyerSessionAgentState.Completed" in source
    assert "state == BuyerSessionAgentState.Cancelled" in source
    assert "state == BuyerSessionAgentState.Failed" in source
    assert "GenerationGeneratingSinceUtc.TryRemove" in source
