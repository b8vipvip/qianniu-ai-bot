from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
UPDATER = (ROOT / "src" / "Bot" / "Update" / "BotAutoUpdater.ps1").read_text(encoding="utf-8-sig")
REPORTER = (ROOT / "src" / "Bot" / "Update" / "UpdateStartupHealthService.cs").read_text(encoding="utf-8-sig")
TRAY = (ROOT / "src" / "Bot" / "AssistWindow" / "NotifyIcon" / "WndNotifyIcon.xaml.cs").read_text(encoding="utf-8-sig")


def test_updater_requires_explicit_multistage_health_instead_of_process_survival():
    assert "function Test-BotHealthy" in UPDATER
    assert "Test-BotStarted" not in UPDATER
    assert "database_initialized" in UPDATER
    assert "configuration_loaded" in UPDATER
    assert "services_started" in UPDATER
    assert "8 second survival" not in UPDATER
    assert "Automatic rollback will start" in UPDATER


def test_health_is_published_only_after_bootstrap_completes():
    bootstrap = TRAY.index("await BootStrap.Init();")
    report = TRAY.index("UpdateStartupHealthService.ReportReady();")
    assert bootstrap < report
    assert 'status = "OK"' in REPORTER
    assert "DbHelper.EnsureInitialized();" in REPORTER


def test_only_one_global_updater_can_run_and_temp_health_is_cleaned():
    assert "Global\\QianniuAiBotUpdater" in UPDATER
    assert "Another Qianniu AI Bot updater is already running" in UPDATER
    finally_block = UPDATER[UPDATER.index("finally {"):]
    assert "Remove-Item Env:\\QIANNIU_BOT_UPDATE_HEALTH_FILE" in finally_block
    assert "$updaterMutex.ReleaseMutex()" in finally_block
