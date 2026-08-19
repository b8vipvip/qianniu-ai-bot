from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_client_rejects_push_until_server_package_is_verified():
    source = read("src/Bot/Update/BotUpdateService.ServerPush.Fast.cs")

    assert 'json.Value<bool?>("mirror_ready") != true' in source
    assert 'json.Value<bool?>("package_verified_on_server") != true' in source
    assert "服务端安装包尚未完整下载并校验" in source


def test_server_push_auto_update_never_uses_github_asset_url():
    source = read("src/Bot/Update/BotUpdateService.ServerPush.Fast.cs")

    # The pushed release deliberately ignores download_url and uses the verified server mirror
    # for both URL fields. DownloadPackageAsync deduplicates equal URLs, leaving only 服务器.
    assert 'var mirrorUrl = (json.Value<string>("mirror_url") ?? string.Empty).Trim();' in source
    assert "PackageUrl = mirrorUrl" in source
    assert "MirrorUrl = mirrorUrl" in source
    assert 'json.Value<string>("download_url")' not in source
    assert "IsSameServerOrigin(serverBaseUrl, mirrorUrl)" in source
    assert "不会绕过服务器切换到GitHub" in source


def test_server_still_gates_notification_on_complete_cached_package():
    push = read("services/api-control-plane/bot_update_push.py")
    cache = read("services/api-control-plane/bot_update_cache.py")

    assert 'partial = destination.with_suffix(destination.suffix + ".partial")' in cache
    assert 'actual.lower() != expected_sha256.lower()' in cache
    assert 'copied != expected_size' in cache
    assert 'partial.replace(destination)' in cache
    assert 'if _mirror_ready(metadata):' in push
    assert 'public["mirror_ready"] = True' in push
    assert 'public["package_verified_on_server"] = True' in push
