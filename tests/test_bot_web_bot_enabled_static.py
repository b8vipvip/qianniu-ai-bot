from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SERVER = ROOT / "services" / "api-control-plane" / "bot_web_bot_enabled.py"
BOOTSTRAP = ROOT / "services" / "api-control-plane" / "bootstrap.py"
DOCKERFILE = ROOT / "services" / "api-control-plane" / "Dockerfile"
PAGE = ROOT / "services" / "api-control-plane" / "static" / "bot-web.html"
SCRIPT = ROOT / "services" / "api-control-plane" / "static" / "bot-web-bot-enabled.js"
WINDOWS = ROOT / "src" / "Bot" / "ChromeNs" / "BotWebBotEnabledSyncService.cs"
PROPS = ROOT / "src" / "Bot" / "Directory.Build.props"
WORKFLOW = ROOT / ".github" / "workflows" / "api-control-plane-ci.yml"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def test_web_page_exposes_independent_bot_master_switch():
    page = read(PAGE)
    script = read(SCRIPT)
    assert 'id="botEnabled"' in page
    assert 'id="botEnabledHint"' in page
    assert "启用 Bot" in page
    assert "关闭后 Bot 不再参与消息处理" in page
    assert 'src="/static/bot-web-bot-enabled.js?v=1"' in page
    assert 'api("/api/bot-web/bot-enabled")' in script
    assert 'method: "PUT"' in script
    assert 'enabled: $("botEnabled").checked' in script
    assert "Bot 总开关" in script
    assert "Windows 当前实际状态" in script


def test_server_persists_desired_and_current_bot_state_per_client_token():
    source = read(SERVER)
    assert '@router.get("/api/bot-web/bot-enabled")' in source
    assert '@router.put("/api/bot-web/bot-enabled")' in source
    assert '@router.post("/api/runtime/v1/bot-web/bot-enabled-sync")' in source
    assert "Depends(bot_web_console._web_client)" in source
    assert "Depends(bot_web_console._runtime_client)" in source
    assert 'request.headers.get("x-shop-key")' in source
    assert 'desired["bot_enabled"] = bool(data.enabled)' in source
    assert 'current["bot_enabled"] = current_enabled' in source
    assert 'if "bot_enabled" not in desired' in source
    assert 'desired["bot_enabled"] = current_enabled' in source
    assert "last_seen_at" in source


def test_windows_sync_applies_web_value_in_shop_scope_and_reports_current_value():
    source = read(WINDOWS)
    props = read(PROPS)
    assert "BotWebBotEnabledSyncService.InitializeForApp" in source
    assert "ShopSettingsScope.Enter(shop)" in source
    assert "ShopControlPlaneConnectionStore" in source
    assert '"/api/runtime/v1/bot-web/bot-enabled-sync"' in source
    assert 'request.Headers.TryAddWithoutValidation("X-Shop-Key", shop.ShopKey)' in source
    assert '["current_enabled"] = currentEnabled' in source
    assert 'root.Value<bool?>("desired_enabled")' in source
    assert "Params.Robot.CanUseRobot = desired.Value" in source
    assert "ControlPlaneClientToken" not in source
    assert "token=" not in source.lower()
    assert "ChromeNs\\BotWebBotEnabledSyncService.cs" in props


def test_server_bootstrap_container_and_ci_package_new_bridge():
    bootstrap = read(BOOTSTRAP)
    dockerfile = read(DOCKERFILE)
    workflow = read(WORKFLOW)
    assert "import bot_web_bot_enabled" in bootstrap
    assert "bot_web_bot_enabled.install(control_plane)" in bootstrap
    assert "bot_web_bot_enabled.py" in dockerfile
    assert "python -m py_compile app.py bootstrap.py bot_web_bot_enabled.py" in workflow
    assert "node --check static/bot-web-bot-enabled.js" in workflow
    assert "services/api-control-plane/bot_web_bot_enabled.py" in workflow
