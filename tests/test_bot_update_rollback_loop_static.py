from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_installer_quarantine_is_kept_separate_from_user_skip_state():
    actions = read("src/Bot/Update/BotUpdateService.Actions.Fast.cs")
    core = read("src/Bot/Update/BotUpdateService.Core.Fast.cs")
    state = read("src/Bot/Update/BotUpdateService.State.Fast.cs")
    models = read("src/Bot/Update/BotUpdateModels.Fast.cs")

    updater_start = actions.index("Process.Start(new ProcessStartInfo")
    handoff_wait = actions.index("WaitForUpdaterHandoff(")
    quarantine_call = actions.index("QuarantineVersionForUpdaterHandoff(release.Version);")
    expected_exit = actions.index('BotProcessWatchdog.TryMarkExpectedExit(')
    go_commit = actions.index("File.WriteAllText(\n                    handoffGoPath")
    app_shutdown = actions.index("Application.Current.Shutdown()")

    assert updater_start < handoff_wait < quarantine_call < expected_exit < go_commit < app_shutdown
    assert "settings.SkippedVersion = version;" in actions
    assert "UserSkippedVersion" in models
    assert "FailedInstallVersion" in models
    assert "two-phase handoff" in state
    assert "settings.FailedInstallVersion = compatibilitySkip;" in state
    assert "settings.UserSkippedVersion = requestedSkip;" in core
    assert "settings.FailedInstallVersion = string.Empty;" in core
    assert "BotProcessWatchdog.CancelExpectedExit();" in actions
    assert "Bot 已保持运行，不会退出" in actions


def test_cancel_skip_clears_failure_quarantine_and_reconnects_server_push():
    core = read("src/Bot/Update/BotUpdateService.Core.Fast.cs")
    state = read("src/Bot/Update/BotUpdateService.State.Fast.cs")

    assert "settings.UserSkippedVersion = string.Empty;" in core
    assert "settings.FailedInstallVersion = string.Empty;" in core
    assert "settings.FailedInstallAt = string.Empty;" in core
    assert "RestartServerPushListener();" in core
    assert "CheckNowAsync(false)" not in state


def test_server_push_still_respects_effective_failure_quarantine():
    push = read("src/Bot/Update/BotUpdateService.ServerPush.Fast.cs")
    state = read("src/Bot/Update/BotUpdateService.State.Fast.cs")

    assert "settings.SkippedVersion" in push
    assert "if (skipped)" in push
    assert "当前版本已设置为跳过" in push
    assert push.index("if (skipped)") < push.index("if (settings.AutoInstall)")
    assert "settings.SkippedVersion = GetCanonicalSkippedVersion(settings);" in state
    assert "return CleanVersionText(settings.FailedInstallVersion);" in state


def test_successful_target_start_clears_provisional_failure_quarantine():
    state = read("src/Bot/Update/BotUpdateService.State.Fast.cs")

    assert "NormalizeVersion(settings.FailedInstallVersion)" in state
    assert "settings.FailedInstallVersion = string.Empty;" in state
    assert "quarantine has served its purpose" in state
