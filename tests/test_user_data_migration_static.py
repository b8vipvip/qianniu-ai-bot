from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_client_data_is_persisted_under_local_app_data():
    source = read("src/BotLib/Extensions/PathEx.cs")
    assert 'ClientDataRootFolderName = "QianniuAiBot"' in source
    assert "Environment.SpecialFolder.LocalApplicationData" in source
    assert 'Path.Combine(UserDataRoot, "data")' in source
    assert "LegacyDataDir" in source


def test_migration_runs_before_any_database_or_logging_initialization():
    source = read("src/Bot/StartUp/StartUp.cs")
    reset_guard = source.index("ResetMigrationMarkerWhenPersistentDataIsMissing()")
    migration = source.index("UserDataMigrationManager.PrepareBeforeAppStartup()")
    app_life = source.index("AppLife.Init()")
    app_create = source.index("App app = new App()")
    assert reset_guard < migration < app_life < app_create


def test_missing_persistent_data_reenters_discovery_instead_of_silent_empty_start():
    source = read("src/Bot/StartUp/StartUp.cs")
    assert 'Path.Combine(PathEx.UserDataRoot, "data-migration-v2.done")' in source
    assert "Directory.EnumerateFileSystemEntries(PathEx.DataDir).Any()" in source
    assert "File.Delete(markerPath)" in source


def test_first_run_requires_valid_params_db_to_treat_folder_as_old_user_data():
    source = read("src/Bot/StartUp/UserDataMigrationManager.cs")
    assert 'Path.Combine(directory, "params.db")' in source
    assert "IsValidSqliteFile(paramsDb)" in source
    assert 'Encoding.ASCII.GetBytes("SQLite format 3\\0")' in source
    assert "SeedFreshDataFromBundledTemplate" in source
    assert "fresh-install-user-selected" in source


def test_import_is_staged_backed_up_and_applied_before_database_open():
    source = read("src/Bot/StartUp/UserDataMigrationManager.cs")
    assert '".migration-staging-"' in source
    assert 'CreateBackupInternal(target, "pre-" + reason)' in source
    assert "Directory.Move(staging, target)" in source
    assert "rollbackBackup" in source
    assert "pending-data-import.txt" in source


def test_settings_exposes_data_management_actions_and_shared_build_includes():
    options = read("src/Bot/Options/WndOption.xaml.cs")
    control = read("src/Bot/Options/CtlDataManagement.cs")
    build_targets = read("src/Directory.Build.targets")

    assert 'CreateOpTab("数据管理", new CtlDataManagement(), style)' in options
    assert "打开数据目录" in control
    assert "安全备份数据" in control
    assert "恢复备份" in control
    assert "从旧版导入" in control
    assert "UserDataMigrationManager.cs" in build_targets
    assert "CtlDataManagement.cs" in build_targets
    assert "BuyerMessageBurstCoordinator.cs" in build_targets
    assert not (ROOT / "src/Bot/Directory.Build.targets").exists()
