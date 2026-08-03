import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def policy() -> dict:
    return json.loads(read("src/Bot/default-business-policy.json"))


def test_import_export_bootstrap_is_explicitly_initialized():
    app = read("src/Bot/App.xaml.cs")
    assert "RulePolicyImportExportUi.InitializeForApp();" in app
    assert app.index("KnowledgePolicyProfileUi.Initialize();") < app.index("RulePolicyImportExportUi.InitializeForApp();")


def test_progress_guard_tracks_completed_order_steps_without_sensitive_storage():
    source = read("src/Bot/ChromeNs/ConversationProgressGuardService.cs")
    config = policy()
    assert "HasOrderEvidence" in source
    assert "DeviceAccountConfirmed" in source
    assert "HasPhoneNumber" in source
    assert "HasVerificationCode" in source
    assert "CurrentInputKind = \"phone_number\"" in source
    assert "CurrentInputKind = \"verification_code\"" in source
    assert "回复中不得复述完整手机号" in config["prompts"]["phone"]
    assert "不得在回复中复述验证码" in config["prompts"]["code"]


def test_router_filters_regressive_knowledge_and_forces_contextual_flow():
    router = read("src/Bot/ChromeNs/SmartReplyRouterService.cs")
    assert "ConversationProgressGuardService.AllowKnowledge" in router
    assert "ConversationProgressGuardService.RequiresContextualHandling" in router
    assert "禁止固定知识直答" in router


def test_validator_blocks_repeated_screenshot_and_order_requests():
    validator = read("src/Bot/ChromeNs/PreSendAnswerValidator.cs")
    guard = read("src/Bot/ChromeNs/ConversationProgressGuardService.cs")
    issues = policy()["validationIssues"]
    assert "ConversationProgressGuardService.AddValidationIssues" in validator
    assert "回复却再次索要相同截图或照片" in issues["repeatScreenshot"]
    assert "买家已经下单，回复却再次要求下单" in issues["repeatOrder"]
    assert "当前消息是在承接代充手机号/验证码流程" in issues["flowRegression"]
    assert "validationIssues.repeatScreenshot" in guard
    assert "validationIssues.repeatOrder" in guard


def test_preorder_general_question_does_not_force_device_photo():
    guard = read("src/Bot/ChromeNs/ConversationProgressGuardService.cs")
    config = policy()
    assert "!p.HasOrderEvidence && !p.ExplicitCompatibilityQuestion" in guard
    assert "一般售前咨询未询问设备兼容性" in config["validationIssues"]["preSaleScreenshot"]
    assert "不要主动强制先发设备截图" in config["prompts"]["preSaleGeneral"]
    assert "不要无关地要求先下单" in config["prompts"]["preSaleGeneral"]


def test_progress_guard_is_in_shared_build_targets():
    targets = read("src/Directory.Build.targets")
    assert "ConversationProgressGuardService.cs" in targets
