from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_account_purchase_exception_does_not_create_handoff_ticket():
    service = read("src/Bot/ChromeNs/HandoffRuleRemoteConfigService.cs")
    notification = read("src/Bot/ChromeNs/HandoffNotificationService.cs")

    assert "另一个账号" in service
    assert "给朋友" in service
    assert "再拍" in service
    assert "月卡" in service
    assert "可以给朋友或其他账号充值" in service
    assert "TryApplySafeAutoReply" in notification
    assert notification.index("TryApplySafeAutoReply") < notification.index("EnableHandoffNotification")
    assert "return;" in notification[notification.index("TryApplySafeAutoReply"):notification.index("var cfg")]


def test_account_security_terms_still_take_priority_over_purchase_exceptions():
    service = read("src/Bot/ChromeNs/HandoffRuleRemoteConfigService.cs")

    for term in ["密码", "验证码", "登录", "找回", "被盗", "绑定", "实名"]:
        assert term in service
    risk = service.index("if (contextual && hasRisk)")
    exception = service.index("if (hasException)", risk)
    assert risk < exception
    assert "UpdateReason(decision, rule)" in service[risk:exception]


def test_server_rules_are_cached_and_synced_to_local_rule_keywords():
    service = read("src/Bot/ChromeNs/HandoffRuleRemoteConfigService.cs")

    assert "/api/runtime/v1/handoff/rules" in service
    assert "HandoffRemoteRulesJson" in service
    assert "LoadCachedRules" in service
    assert "SyncKeywordsToLocalConfig" in service
    assert "cfg.ManualKeywords = manual" in service
    assert "cfg.NoAutoReplyKeywords = confirm" in service
    assert "BotFeatureStore.SaveAutoReplyRules(cfg)" in service
    assert "TimeSpan.FromMinutes(1)" in service


def test_rule_sync_is_initialized_and_compiled():
    app = read("src/Bot/App.xaml.cs")
    targets = read("src/Directory.Build.targets")

    assert "HandoffRuleRemoteConfigService.Initialize();" in app
    assert "ChromeNs\\HandoffRuleRemoteConfigService.cs" in targets


def test_wecom_page_supports_view_edit_add_and_delete_rules():
    page = read("services/api-control-plane/static/wecom.html")
    settings = read("services/api-control-plane/wecom_settings.py")

    assert "转人工触发规则" in page
    assert "新增规则" in page
    assert "删除" in page
    assert "保存转人工规则" in page
    assert "/api/admin/wecom/handoff-rules" in page
    assert "sensitive_context" in page
    assert "wecom_handoff_rules" in settings
    assert "default_handoff_rules" in settings
    assert "/api/runtime/v1/handoff/rules" in settings
