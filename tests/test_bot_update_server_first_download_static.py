from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_update_package_download_is_server_only_after_prepare():
    download = read("src/Bot/Update/BotUpdateService.Download.Fast.cs")
    assert "EnsureServerPackageReadyAsync" in download
    assert '"/api/public/v1/bot-update/ensure/"' in download
    assert '"/api/public/v1/bot-update/status/"' in download
    assert "release.PackageUrl" not in download
    assert '"GitHub", release.PackageUrl' not in download
    assert 'CurrentDownloadChannel = "服务器"' in download
    assert "connectTimeout.CancelAfter" in download
    assert "if (cancellationToken.IsCancellationRequested) throw;" in download
    assert "客户端不会回退到 GitHub" in download
    assert "正在连接下载通道" in download
    assert "RaiseDownloadStatus" in download


def test_update_prompt_matches_server_only_runtime_strategy_and_shows_channel():
    prompt = read("src/Bot/Update/BotUpdatePromptWindow.Fast.cs")
    assert "只从服务端下载" in prompt
    assert "服务器不可用时自动切换 GitHub" not in prompt
    assert "下载通道：" in prompt
    assert "CurrentDownloadChannel" in prompt
