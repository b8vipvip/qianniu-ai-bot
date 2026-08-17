from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_update_package_download_is_server_first_then_github_fallback():
    download = read("src/Bot/Update/BotUpdateService.Download.Fast.cs")

    server = 'AddDownloadSource(sources, "服务器", release.MirrorUrl)'
    github = 'AddDownloadSource(sources, "GitHub", release.PackageUrl)'
    assert server in download
    assert github in download
    assert download.index(server) < download.index(github)

    # A server-side timeout must be treated as a source failure, not as a user cancel,
    # so the foreach loop can continue to the GitHub fallback source.
    assert "connectTimeout.CancelAfter" in download
    assert "if (cancellationToken.IsCancellationRequested) throw;" in download
    assert "准备尝试下一来源" in download
    assert "CurrentDownloadChannel = source.Key" in download
    assert "CurrentDownloadPercent = -1" in download
    assert "正在连接下载通道" in download
    assert "RaiseDownloadStatus" in download


def test_update_prompt_matches_server_first_runtime_strategy_and_shows_channel():
    prompt = read("src/Bot/Update/BotUpdatePromptWindow.Fast.cs")

    assert "优先从服务器下载" in prompt
    assert "服务器不可用时自动切换 GitHub" in prompt
    assert "下载通道：" in prompt
    assert "CurrentDownloadChannel" in prompt
    assert "优先从 GitHub 下载；失败时自动切换服务端镜像" not in prompt
