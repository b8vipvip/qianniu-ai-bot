from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_policy_sidecar_has_modes_conditions_and_reliability_evidence():
    code = read("src/Bot/ChromeNs/KnowledgePolicyProfileService.cs")
    assert 'knowledge-policy-profile.json' in code
    assert 'public const string Direct = "direct"' in code
    assert 'public const string Contextual = "contextual"' in code
    assert 'public const string Constraint = "constraint"' in code
    assert 'ApplyWhen' in code
    assert 'DoNotApplyWhen' in code
    assert 'RequiredContext' in code
    assert 'SellerCorrectionCount' in code
    assert 'SellerWithdrawCount' in code
    assert 'ReliabilityScore' in code
    assert 'evidenceType == "withdrawn_bot_then_manual"' in code
    assert 'evidenceType == "manual_correction"' in code


def test_router_applies_policy_before_direct_answer_and_records_route_selection():
    router = read("src/Bot/ChromeNs/SmartReplyRouterService.cs")
    assert 'public KnowledgePolicyEvaluation PolicyEvaluation' in router
    assert 'KnowledgePolicyProfileService.Evaluate' in router
    assert 'PolicyAllowsDirect(provisionalBest)' in router
    assert 'if (!PolicyAllowsDirect(best)) return false' in router
    assert 'candidate.PolicyEvaluation.ConstraintOnly' in router
    assert 'KnowledgePolicyProfileService.RecordRouteSelection(best.Entry, true)' in router
    assert 'KnowledgePolicyProfileService.RecordRouteSelection(best.Entry, false)' in router
    assert 'KnowledgePolicyProfileService.BuildPromptAddon' in router


def test_reviewed_human_corrections_feed_reliability_profile():
    code = read("src/Bot/ChromeNs/ReviewedKnowledgeLearningService.cs")
    assert 'KnowledgePolicyProfileService.RecordReviewEvidence' in code
    assert 'previousAnswer' in code
    assert 'KnowledgePolicyProfileService.RecordKnowledgeAccepted(question, answer)' in code
    assert '降低旧答案直答可靠度' in code


def test_knowledge_policy_editor_is_available_from_knowledge_manager():
    ui = read("src/Bot/Knowledge/KnowledgePolicyProfileUi.cs")
    app = read("src/Bot/App.xaml.cs")
    targets = read("src/Directory.Build.targets")
    assert 'Content = "知识策略"' in ui
    assert '知识策略与可靠度' in ui
    assert '优先直答' in ui
    assert '必须结合上下文' in ui
    assert '仅作为事实约束' in ui
    assert 'KnowledgePolicyProfileUi.Initialize()' in app
    assert 'KnowledgePolicyProfileService.cs' in targets
    assert 'KnowledgePolicyProfileUi.cs' in targets
