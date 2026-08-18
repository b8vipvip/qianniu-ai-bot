from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_installer_quarantines_target_before_process_shutdown_and_updater_handoff():
    actions = read("src/Bot/Update/BotUpdateService.Actions.Fast.cs")

    quarantine_call = actions.index("QuarantineVersionForUpdaterHandoff(release.Version);")
    expected_exit = actions.index('BotProcessWatchdog.MarkExpectedExit("auto-update:" + release.Version);')
    updater_start = actions.index("Process.Start(new ProcessStartInfo")
    app_shutdown = actions.index("Application.Current.Shutdown()")

    assert quarantine_call < expected_exit < updater_start < app_shutdown
    assert "settings.SkippedVersion = version;" in actions
    assert "SaveSettingsInternal(_settings);" in actions
    assert "若回滚不会再次自动循环安装" in actions


def test_server_push_respects_the_provisional_failed_version_quarantine():
    push = read("src/Bot/Update/BotUpdateService.ServerPush.Fast.cs")

    assert "settings.SkippedVersion" in push
    assert "if (skipped)" in push
    assert "当前版本已设置为跳过" in push
    assert push.index("if (skipped)") < push.index("if (settings.AutoInstall)")
