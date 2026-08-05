from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_windows_client_exposes_one_click_backup_and_restore():
    source = read("src/Bot/Knowledge/ClientDataCloudBackupService.cs")
    assert 'Content = "云备份/换机"' in source
    assert '"上传当前电脑数据"' in source
    assert '"从云端一键恢复"' in source
    assert "GetStatusAsync" in source
    assert "UploadAsync" in source
    assert "DownloadAndRestoreAsync" in source
    assert "RestartApplication" in source


def test_backup_is_encrypted_with_current_bot_token_and_integrity_checked():
    source = read("src/Bot/Knowledge/ClientDataCloudBackupService.cs")
    assert 'private const string Magic = "QABK1"' in source
    assert "Rfc2898DeriveBytes" in source
    assert "Aes.Create" in source
    assert "HMACSHA256" in source
    assert "ConstantTimeEquals" in source
    assert "Bot 令牌不一致或云备份已损坏" in source


def test_logs_transient_files_and_connection_credentials_are_excluded():
    source = read("src/Bot/Knowledge/ClientDataCloudBackupService.cs")
    for value in (
        '"log", "logs"',
        '"backup", "backups"',
        '"tmp", "temp"',
        '"cache", "caches"',
        '"crash", "crashes"',
        '"update", "updates"',
        'string.Equals(extension, ".log"',
        'string.Equals(name, "params.db"',
        'UrlKey + "#-#" + Scope',
        'TokenKey + "#-#" + Scope',
    ):
        assert value in source


def test_business_parameters_and_data_files_are_included_with_local_rollback():
    source = read("src/Bot/Knowledge/ClientDataCloudBackupService.cs")
    assert "ExportParameters" in source
    assert 'WriteTextEntry(zip, "params.json"' in source
    assert 'zip.CreateEntry("files/"' in source
    assert "CreateRollbackBackup" in source
    assert "before-cloud-restore-" in source
    assert "PersistentParams.TrySaveParam" in source


def test_build_includes_client_backup_service():
    props = read("src/Bot/Directory.Build.props")
    assert "Knowledge\\ClientDataCloudBackupService.cs" in props


def test_server_registers_binary_backup_storage_by_client_token():
    service = read("services/api-control-plane/client_data_backup.py")
    bootstrap = read("services/api-control-plane/bootstrap.py")
    dockerfile = read("services/api-control-plane/Dockerfile")
    assert '"/api/runtime/v1/client-data-backup/status"' in service
    assert '"/api/runtime/v1/client-data-backup"' in service
    assert "Depends(core._runtime_client)" in service
    assert "bot_client_data_backups" in service
    assert "client-data-backups" in service
    assert "hashlib.sha256" in service
    assert "client_data_backup.install(control_plane)" in bootstrap
    assert "client_data_backup.init_db()" in bootstrap
    assert "client_data_backup.py" in dockerfile
