from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_server_push_waits_for_mirror_before_advertising_server_download():
    push = read("services/api-control-plane/bot_update_push.py")

    assert "BOT_UPDATE_PUSH_MIRROR_GRACE_SECONDS" in push
    assert "_mirror_ready(metadata)" in push
    assert 'public["mirror_ready"] = bool(ready)' in push
    assert 'public["mirror_url"] = ""' in push
    assert "waited < MIRROR_READY_GRACE_SECONDS" in push
    assert "do not advertise a server download URL" in push


def test_repeated_same_version_push_does_not_restart_download_loop_immediately():
    code = read("src/Bot/Update/BotUpdateService.ServerPush.Fast.cs")

    assert "TryBeginAutoInstallAttempt" in code
    assert "_autoInstallInFlight" in code
    assert "_autoInstallRetryAfterUtc" in code
    assert "_autoInstallFailureCount" in code
    assert "ScheduleAutoInstallRetry" in code
    assert '"不会在服务器/GitHub之间反复切换。剩余约 "' in code
    assert "60" in code and "180" in code and "600" in code and "1800" in code
    assert "Do not open a second manual prompt" in code


def test_connecting_state_is_indeterminate_until_real_bytes_arrive():
    download = read("src/Bot/Update/BotUpdateService.Download.Fast.cs")
    window = read("src/Bot/Update/BotUpdateAutoProgressWindow.Fast.cs")
    core = read("src/Bot/Update/BotUpdateService.Core.Fast.cs")

    assert "CurrentDownloadPercent = -1" in download
    assert "normalizedPercent < 0" in download
    assert "正在连接下载通道" in download
    assert "收到首批安装包数据后显示实际百分比" in download
    assert "IsIndeterminate = true" in window
    assert "result.DownloadedBytes <= 0" in window
    assert "_progress.IsIndeterminate = false" in window
    assert "GitHubDownloadConnectTimeoutSeconds = 60" in core
    assert "DownloadReadTimeoutSeconds = 60" in core
    assert "connectTimeoutSeconds" in download
