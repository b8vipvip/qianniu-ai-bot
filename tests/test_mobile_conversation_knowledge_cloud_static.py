from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SERVER = ROOT / "services" / "api-control-plane" / "bot_web_conversation_knowledge.py"
BOOTSTRAP = ROOT / "services" / "api-control-plane" / "bootstrap.py"
DOCKERFILE = ROOT / "services" / "api-control-plane" / "Dockerfile"
HTML = ROOT / "services" / "api-control-plane" / "static" / "bot-web.html"
JS = ROOT / "services" / "api-control-plane" / "static" / "bot-web-v2.js"
CSS = ROOT / "services" / "api-control-plane" / "static" / "bot-web-v2.css"
CLIENT = ROOT / "src" / "Bot" / "Knowledge" / "KnowledgeCloudSyncService.cs"
PROPS = ROOT / "src" / "Bot" / "Directory.Build.props"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def test_server_registers_conversation_and_knowledge_extension():
    bootstrap = read(BOOTSTRAP)
    dockerfile = read(DOCKERFILE)
    server = read(SERVER)

    assert "import bot_web_conversation_knowledge" in bootstrap
    assert "bot_web_conversation_knowledge.install(control_plane)" in bootstrap
    assert "bot_web_conversation_knowledge.init_db()" in bootstrap
    assert "bot_web_conversation_knowledge.py" in dockerfile
    assert "CREATE TABLE IF NOT EXISTS bot_conversation_reads" in server
    assert "CREATE TABLE IF NOT EXISTS bot_knowledge_state" in server


def test_conversation_list_is_sorted_by_latest_message_and_supports_read_state():
    source = read(SERVER)

    assert '@router.get("/api/bot-web/conversations")' in source
    assert "SELECT seller,buyer,MAX(id) AS max_id" in source
    assert "ORDER BY m.occurred_at DESC,m.id DESC" in source
    assert "unread_count" in source
    assert '@router.get("/api/bot-web/conversation/messages")' in source
    assert '@router.post("/api/bot-web/conversation/read")' in source


def test_web_knowledge_crud_and_runtime_cloud_sync_are_client_isolated():
    source = read(SERVER)

    assert '@router.get("/api/bot-web/knowledge")' in source
    assert '@router.post("/api/bot-web/knowledge")' in source
    assert '@router.put("/api/bot-web/knowledge/{knowledge_id}")' in source
    assert '@router.delete("/api/bot-web/knowledge/{knowledge_id}")' in source
    assert '@router.post("/api/runtime/v1/bot-web/knowledge-sync")' in source
    assert "client_id = int(client[\"id\"])" in source
    assert "updated_by" in source
    assert "content_hash" in source


def test_mobile_page_has_buyer_list_detail_long_press_and_knowledge_management():
    html = read(HTML)
    js = read(JS)
    css = read(CSS)

    assert 'id="conversationList"' in html
    assert 'id="chatMessageList"' in html
    assert 'data-page="knowledge"' in html
    assert 'id="knowledgeList"' in html
    assert 'id="messageActionSheet"' in html
    assert "ORDER BY" not in js
    assert "bindLongPress" in js
    assert "saveKnowledgeAction" in js
    assert "inferKnowledgePair" in js
    assert "/api/bot-web/conversations" in js
    assert "/api/bot-web/knowledge" in js
    assert ".conversation-item" in css
    assert ".knowledge-card" in css


def test_windows_client_requires_opt_in_and_backs_up_before_cloud_apply():
    source = read(CLIENT)
    props = read(PROPS)

    assert "启用知识库云同步" in source
    assert "KnowledgeCloudSyncEnabled" in source
    assert "/api/runtime/v1/bot-web/knowledge-sync" in source
    assert "BotFeatureStore.GetKnowledgeBase()" in source
    assert "BotFeatureStore.SaveKnowledgeBase(cloud)" in source
    assert "knowledge-cloud-before-apply-" in source
    assert "KnowledgeCloudSyncService.cs" in props
    assert "客户端令牌" not in source.split('Log.Info("知识库云同步')[0]
