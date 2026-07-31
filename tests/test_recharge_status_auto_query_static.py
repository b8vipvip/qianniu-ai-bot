from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SERVICE = ROOT / "src" / "Bot" / "ChromeNs" / "RechargeStatusAutoQueryService.cs"
SAFETY = ROOT / "src" / "Bot" / "ChromeNs" / "IncomingMessageSafety.cs"
TRADE_MODEL = ROOT / "src" / "DbEntity" / "Response" / "ZnkfTradeQueryResponse.cs"
PROPS = ROOT / "src" / "Directory.Build.props"
BOOTSTRAP = ROOT / "services" / "api-control-plane" / "bootstrap.py"
DOCKERFILE = ROOT / "services" / "api-control-plane" / "Dockerfile"
PAGE = ROOT / "services" / "api-control-plane" / "static" / "recharge-query.html"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def test_trade_model_preserves_unknown_qianniu_fields_and_recovers_sku():
    source = read(TRADE_MODEL)

    assert "[JsonExtensionData]" in source
    assert "IDictionary<string, JToken> ExtensionData" in source
    assert "[OnDeserialized]" in source
    assert "QianniuTradeSkuRecovery.Resolve(ExtensionData" in source
    assert "skutext" in source
    assert "skupropertiesname" in source
    assert "pname" in source and "vname" in source
    assert "propertyname" in source and "propertyvalue" in source
    assert "专辑名称|套餐名称" in source
    assert 'value = known.Groups[1].Value.Trim() + ":" + known.Groups[2].Value.Trim()' in source


def test_trade_sku_recovery_covers_nested_and_json_encoded_fields():
    source = read(TRADE_MODEL)

    assert "Walk(root, string.Empty, flat, 0)" in source
    assert "JToken.Parse(text)" in source
    assert "ResolveDirect(flat)" in source
    assert "ResolvePairs(flat)" in source
    assert "IsSkuPath" in source
    assert "IdentifierOnly" in source


def test_recharge_service_only_claims_explicit_progress_questions_with_labeled_code():
    source = read(SERVICE)

    assert "ProgressIntentRegex" in source
    assert "充值进度|充值状态" in source
    assert '@"(?:会员)?兑换码\\s*[:：]' in source
    assert "ConversationContextStore.GetRecentTurns" in source
    assert 'x.Role == "assistant"' in source
    assert "if (!TryFindRecentRedeemCode" in source
    assert "HandledMessages.TryAdd" in source


def test_claimed_recharge_question_is_blocked_before_normal_ai():
    safety = read(SAFETY)
    service = read(SERVICE)

    refresh = safety.index("ConversationContextStore.RefreshAndRecord")
    consume = safety.index("RechargeStatusAutoQueryService.TryConsumeHandled")
    normal = safety.index("return new IncomingMessageDecision", consume)
    assert refresh < consume < normal
    assert 'return Skip("[充值进度查询]"' in safety
    assert "qn.EvRecieveNewMessage += OnReceiveNewMessage" in service


def test_windows_bot_uses_control_plane_without_receiving_admin_key():
    source = read(SERVICE)

    assert 'serverUrl + "/api/runtime/v1/recharge-query/config"' in source
    assert 'serverUrl + "/api/runtime/v1/recharge-query/status"' in source
    assert 'request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token)' in source
    assert "X-Admin-Key" not in source
    assert "auth_key" not in source.lower()


def test_dynamic_replies_are_not_added_to_knowledge_and_handoff_is_deduplicated():
    source = read(SERVICE)

    assert "KnowledgeLearningService.QueueLearn" not in source
    assert "HumanNotifyDedup" in source
    assert "DateTime.Now.AddMinutes(15)" in source
    assert "WeComAppBridgeClient.SendNotificationAsync" in source
    assert "ReplyDeduplicationService.RememberDelivered" in source
    assert "BotOutboundMessageFormatter.EnsureAiMarker" in source


def test_recharge_module_is_packaged_and_settings_page_exists():
    props = read(PROPS)
    bootstrap = read(BOOTSTRAP)
    dockerfile = read(DOCKERFILE)
    page = read(PAGE)

    assert "RechargeStatusAutoQueryService.cs" in props
    assert "import recharge_status_query" in bootstrap
    assert "include_router(recharge_status_query.router)" in bootstrap
    assert "recharge_status_query.py" in dockerfile
    assert "启用自动查询充值结果" in page
    assert "admin/index.html 鉴权密钥 key" in page
    assert "/api/admin/recharge-query/settings" in page
    assert "/api/admin/recharge-query/test" in page


def test_sensitive_values_are_not_logged_verbatim():
    source = read(SERVICE)

    assert "codeHash=" in source
    assert "兑换码尾号=" in source
    assert 'Log.Info("充值进度问题已接管' in source
    assert '" + code + "' not in source
    assert "question=" not in source
    assert '"****" + code.Substring(code.Length - 4)' in source
