from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
RECOVERY = ROOT / "src" / "Bot" / "Common" / "DatabaseRecoveryService.cs"
STARTUP = ROOT / "src" / "Bot" / "StartUp" / "StartUp.cs"


def test_corrupt_bot_database_is_timestamped_and_recreated_without_touching_user_root():
    text = RECOVERY.read_text(encoding="utf-8-sig")
    assert '"PRAGMA integrity_check"' in text
    assert '".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")' in text
    assert "File.Move(databasePath, backupPath)" in text
    assert "new SQLiteHelper(databasePath, tableTypes)" in text
    assert "Directory.Delete" not in text
    assert "QianniuAiBot" not in text


def test_database_recovery_is_forced_before_wpf_app_construction():
    text = STARTUP.read_text(encoding="utf-8-sig")
    recovery = text.index("DbHelper.EnsureInitialized();")
    app = text.index("App app = new App();")
    assert recovery < app


def test_database_health_log_contract_is_observable():
    text = RECOVERY.read_text(encoding="utf-8-sig")
    for message in ("数据库健康检查开始", "数据库完整性: OK", "数据库完整性: FAILED", "自动恢复:", "备份路径:"):
        assert message in text
