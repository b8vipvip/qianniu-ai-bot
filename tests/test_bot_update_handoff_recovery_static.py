from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_bootstrap_requires_ready_then_go_before_inner_updater_runs():
    actions = read("src/Bot/Update/BotUpdateService.Actions.Fast.cs")

    assert "BuildUpdaterBootstrapScript()" in actions
    assert "QianniuAiBotUpdaterBootstrap-" in actions
    assert "WaitForUpdaterHandoff(" in actions
    assert "handoffGoPath" in actions
    assert "handoff ready; waiting for Bot commit signal before updater may proceed." in actions
    assert "handoff commit received; starting inner updater." in actions
    assert "bootstrap exited before acknowledgement" in actions
    assert "Bot 已保持运行，不会退出" in actions

    ready_write = actions.index(") | Set-Content -LiteralPath $HandoffPath -Encoding UTF8")
    go_wait = actions.index("$goDeadline = (Get-Date).AddSeconds(20)")
    child_start = actions.index("$child = Start-Process -FilePath 'powershell.exe'")
    assert ready_write < go_wait < child_start


def test_bootstrap_validates_package_before_acknowledging_handoff():
    actions = read("src/Bot/Update/BotUpdateService.Actions.Fast.cs")

    hash_check = actions.index("Bootstrap SHA256 verification failed")
    ready_write = actions.index(") | Set-Content -LiteralPath $HandoffPath -Encoding UTF8")
    assert hash_check < ready_write
    assert "bootstrapLog = $HandoffPath + '.bootstrap.log'" in actions
    assert "preflight validated; package=" in actions
    assert "handoff was not committed; current Bot should remain running." in actions


def test_bootstrap_recovers_bot_when_inner_updater_fails_or_times_out():
    actions = read("src/Bot/Update/BotUpdateService.Actions.Fast.cs")

    assert "Inner updater failed with exit code" in actions
    assert "Inner updater timed out after 5 minutes and was terminated." in actions
    assert "Start-BotIfNeeded" in actions
    assert "bootstrap recovery ensured Bot is running." in actions
    assert "Inner updater returned success but Bot is not running; invoking recovery start." in actions


def test_watchdog_preserves_auto_update_reason_and_has_bounded_handoff_recovery():
    watchdog = read("src/Bot/Update/BotUpdateProcessWatchdog.Fast.cs")

    assert "TryMarkExpectedExit" in watchdog
    assert "CancelExpectedExit" in watchdog
    assert '_expectedExitReason.StartsWith(' in watchdog
    assert '"auto-update:"' in watchdog
    assert "auto-update expected exit pid=" in watchdog
    assert "$softDeadline = (Get-Date).AddSeconds(90)" in watchdog
    assert "$hardDeadline = (Get-Date).AddMinutes(5)" in watchdog
    assert "Get-RelatedUpdaterProcesses" in watchdog
    assert "auto-update handoff updater disappeared before Bot returned" in watchdog
    assert "auto-update timeout recovery" in watchdog


def test_bootstrap_script_stays_windows_powershell_51_compatible():
    actions = read("src/Bot/Update/BotUpdateService.Actions.Fast.cs")

    # Windows Server 2022 uses Windows PowerShell 5.1 in the affected deployment.
    assert "$Value ?? ''" not in actions
    assert "ForEach-Object -Parallel" not in actions
    assert "pwsh.exe" not in actions
    assert "$q = [char]34" in actions
