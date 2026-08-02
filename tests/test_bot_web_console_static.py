from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SERVICE = ROOT / "services" / "api-control-plane" / "bot_web_console.py"
ADMIN = ROOT / "services" / "api-control-plane" / "bot_web_admin.py"
BOOTSTRAP = ROOT / "services" / "api-control-plane" / "bootstrap.py"
DOCKERFILE = ROOT / "services" / "api-control-plane" / "Dockerfile"
INDEX = ROOT / "services" / "api-control-plane" / "static" / "index.html"
PAGE = ROOT / "services" / "api-control-plane" / "static" / "bot-web.html"
SCRIPT = ROOT / "services" / "api-control-plane" / "static" / "bot-web.js"
ADMIN_SCRIPT = ROOT / "services" / "api-control-plane" / "static" / "client-token-copy.js"
WINDOWS = ROOT / "src" / "Bot" / "ChromeNs" / "BotWebConsoleSyncService.cs"
PROPS = ROOT / "src" / "Directory.Build.props"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def test_server_packages_and_registers_mobile_console():
    bootstrap = read(BOOTSTRAP)
    dockerfile = read(DOCKERFILE)
    assert "import bot_web_console" in bootstrap
    assert "import bot_web_admin" in bootstrap
    assert "bot_web_console.install(control_plane)" in bootstrap
    assert "bot_web_admin.install(control_plane)" in bootstrap
    assert "bot_web_console.init_bot_web_db()" in bootstrap
    assert "bot_web_console.py" in dockerfile
    assert "bot_web_admin.py" in dockerfile


def test_web_login_is_bound_to_client_token_and_session():
    source = read(SERVICE)
    assert '@router.post("/api/bot-web/login")' in source
    assert "_client_by_token(token, capture_cipher=True)" in source
    assert 'request.session["bot_web_client_id"]' in source
    assert '@router.get("/api/bot-web/snapshot")' in source
    assert "Depends(_web_client)" in source
    assert '@router.post("/api/runtime/v1/bot-web/sync")' in source
    assert "Depends(_runtime_client)" in source


def test_tokens_are_encrypted_for_admin_copy_and_can_be_rotated():
    service = read(SERVICE)
    admin = read(ADMIN)
    script = read(ADMIN_SCRIPT)
    assert "token_cipher" in service
    assert "_cp.encrypt_secret(token)" in admin
    assert "_cp.decrypt_secret(row[\"token_cipher\"])" in admin
    assert '"/api/admin/mobile-bot/clients"' in script
    assert "copyClientToken" in script
    assert "rotateClientToken" in script
    assert "旧令牌会立即失效" in script


def test_mobile_page_has_status_messages_settings_and_manual_reply():
    page = read(PAGE)
    script = read(SCRIPT)
    assert "实时运行状态" in page
    assert 'id="messageList"' in page
    assert 'id="autoReplyEnabled"' in page
    assert 'id="replyForm"' in page
    assert "/api/bot-web/snapshot" in script
    assert "/api/bot-web/settings" in script
    assert "/api/bot-web/messages/send" in script
    assert "setInterval" in script


def test_windows_client_syncs_state_and_recent_conversation_without_logging_token():
    source = read(WINDOWS)
    props = read(PROPS)
    assert "BotWebConsoleSyncService.cs" in props
    assert "InitializeForApp" in source
    assert '"/api/runtime/v1/bot-web/sync"' in source
    assert "ConversationContextStore.GetRecentTurns" in source
    assert "effective_auto_reply_enabled" in source
    assert "ApplyDesiredSettings" in source
    assert "ExecuteSendTextAsync" in source
    assert "SendTextWithRetryAsync" in source
    assert "ControlPlaneClientToken" in source
    assert "token=" not in source.lower()


def test_sensitive_command_state_is_idempotent_and_not_persisted_in_server_logs():
    source = read(WINDOWS)
    server = read(SERVICE)
    assert "ProcessedCommandsKey" in source
    assert "WasProcessed(id)" in source
    assert "MarkProcessed(id)" in source
    assert "UNIQUE(client_id, message_key)" in server
    assert "INSERT OR IGNORE INTO bot_messages" in server
    assert "payload_json" in server
    assert "print(" not in server


def test_admin_console_links_mobile_page_and_copy_extension():
    index = read(INDEX)
    assert 'href="/bot/"' in index
    assert "Bot Web端" in index
    assert 'src="/static/client-token-copy.js"' in index
    assert "管理员可复制对应令牌" in index
