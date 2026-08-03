import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BOT = ROOT / "src" / "Bot"


def read(path):
    return path.read_text(encoding="utf-8-sig")


def policy():
    return json.loads(read(BOT / "default-business-policy.json"))


def test_business_phrases_are_client_json_not_progress_guard_constants():
    source = read(BOT / "ChromeNs" / "ConversationProgressGuardService.cs")
    assert "BusinessPolicyProfileService.GetRegex" in source
    assert "BusinessPolicyProfileService.GetString" in source
    assert "OrderEvidenceRegex" not in source
    assert "CompatibilityQuestionRegex" not in source
    assert "ScreenshotRequestRegex" not in source
    assert "先下单|下单后联系客服" not in source


def test_default_policy_has_all_runtime_sections_and_regexes_compile():
    data = policy()
    assert data["schema"] == "qianniu-ai-bot.business-policy"
    assert data["version"] >= 2
    for key in ("patterns", "stages", "facts", "buyerGoals", "prompts", "validationIssues", "handoffOverrides"):
        assert key in data
    for name, pattern in data["patterns"].items():
        re.compile(pattern)
    for item in data["handoffOverrides"]:
        for name in ("strongRiskPattern", "allowAiPattern", "fixedReplyPattern"):
            if item.get(name):
                re.compile(item[name])


def test_normal_own_account_question_is_not_forced_to_handoff():
    data = policy()
    account = next(x for x in data["handoffOverrides"] if x["keyword"] == "账号")
    question = "电视端的有吗 能登自己账号吗"
    assert not re.search(account["strongRiskPattern"], question, re.I)
    assert re.search(account["allowAiPattern"], question, re.I)


def test_account_security_question_remains_manual():
    data = policy()
    account = next(x for x in data["handoffOverrides"] if x["keyword"] == "账号")
    assert re.search(account["strongRiskPattern"], "账号密码忘了还收不到验证码", re.I)


def test_pai_object_disambiguates_order_link_from_screenshot_page():
    data = policy()
    p = data["patterns"]

    order_question = "那拍哪个链接啊"
    assert not re.search(p["screenshotTargetQuestion"], order_question, re.I)
    assert re.search(p["purchaseTargetQuestion"], order_question, re.I)

    for question in ("拍哪个商品", "选哪个SKU下单", "这个链接怎么拍"):
        assert re.search(p["purchaseTargetQuestion"], question, re.I)

    for question in ("拍哪个页面", "要拍哪个界面", "拍照哪个页面", "拍哪里"):
        assert re.search(p["screenshotTargetQuestion"], question, re.I)

    good = "拍电视端/大屏VIP这个商品链接，选择需要的时长或规格下单即可。"
    wrong = "不是拍商品链接，请拍电视上的酷狗账号绑定页面。"
    assert re.search(p["purchaseTargetAnswer"], good, re.I)
    assert not re.search(p["badPurchaseTargetAnswer"], good, re.I)
    assert re.search(p["badPurchaseTargetAnswer"], wrong, re.I)
    assert "这里的“拍”表示购买下单" in data["prompts"]["purchaseTarget"]


def test_progress_guard_routes_purchase_target_as_contextual_order_intent():
    source = read(BOT / "ChromeNs" / "ConversationProgressGuardService.cs")
    assert "AsksPurchaseTarget" in source
    assert "!asksScreenshotTarget && R(\"purchaseTargetQuestion\")" in source
    assert "purchase_target_question" in source
    assert "prompts.purchaseTarget" in source
    assert "validationIssues.purchaseTargetScreenshot" in source
    assert "validationIssues.purchaseTargetMissingSelection" in source


def test_handoff_json_override_runs_before_remote_compatibility_layer():
    source = read(BOT / "ChromeNs" / "HandoffNotificationService.cs")
    client = source.index("BusinessPolicyProfileService.TryOverrideHandoff")
    remote = source.index("HandoffRuleRemoteConfigService.TryApplySafeAutoReply")
    assert client < remote


def test_business_policy_editor_and_package_wiring_exist():
    ui = read(BOT / "Knowledge" / "BusinessPolicyProfileUi.cs")
    props = read(BOT / "Directory.Build.props")
    assert "运行策略JSON" in ui
    assert "BusinessPolicyProfileService.SaveJson" in ui
    assert "BusinessPolicyProfileUi.cs" in props
    assert "BusinessPolicyProfileService.cs" in props
    assert "default-business-policy.json" in props
    assert "CopyToOutputDirectory" in props
