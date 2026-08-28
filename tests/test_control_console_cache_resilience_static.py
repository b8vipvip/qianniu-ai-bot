from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def text(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_console_assets_are_forced_to_revalidate_after_server_update():
    guard = text("services/api-control-plane/console_cache_guard.py")
    assert 'path == "/" or path.startswith("/static/")' in guard
    assert '"no-store, no-cache, must-revalidate, max-age=0"' in guard
    assert 'response.headers["Pragma"] = "no-cache"' in guard
    assert 'response.headers["Expires"] = "0"' in guard
    assert 'response.headers["X-Qianniu-Console-Cache"] = "no-store"' in guard


def test_cache_guard_is_installed_and_packaged():
    bootstrap = text("services/api-control-plane/bootstrap.py")
    dockerfile = text("services/api-control-plane/Dockerfile")
    assert "import console_cache_guard" in bootstrap
    assert "console_cache_guard.install(control_plane)" in bootstrap
    assert "console_cache_guard.py" in dockerfile


def test_cache_guard_does_not_disable_runtime_api_caching_globally():
    guard = text("services/api-control-plane/console_cache_guard.py")
    assert 'path.startswith("/api/")' not in guard
    assert 'path.startswith("/v1/")' not in guard
