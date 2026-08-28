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


def test_console_login_is_visible_even_if_main_javascript_never_starts():
    html = text("services/api-control-plane/static/index.html")
    assert '<div id="loginView" class="login-shell">' in html
    assert '<div id="appView" class="app-shell hidden">' in html
    assert '/static/console-startup-guard.js?v=1' in html
    assert '/static/app.js?v=3' in html


def test_console_startup_guard_surfaces_script_failures_instead_of_blank_page():
    guard = text("services/api-control-plane/static/console-startup-guard.js")
    assert 'window.addEventListener("error"' in guard
    assert 'window.addEventListener("unhandledrejection"' in guard
    assert 'login.classList.remove("hidden")' in guard
    assert 'target.tagName==="SCRIPT"' in guard
