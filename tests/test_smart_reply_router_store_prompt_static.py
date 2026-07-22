from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_smart_reply_router_has_three_routes_and_two_stage_small_retrieval():
    code = read("src/Bot/ChromeNs/SmartReplyRouterService.cs")
    assert "DirectKnowledge" in code
    assert "ContextualKnowledge" in code
    assert "AiGeneral" in code
    assert "RetrievalPoolSize = 10" in code
    assert "PromptCandidateCount = 3" in code
    assert "CalculateContextDependency" in code
    assert "CanDirectReply" in code
    assert "DetectIntent" in code
    assert "候选知识" in code
    assert "这些知识是候选事实依据，不是必须原样发送的固定答案" in code


def test_streaming_pipeline_uses_router_before_ai_and_keeps_manual_rules_first():
    code = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    manual = code.index("var manualDecision = BotFeatureStore.EvaluateAutoReplyRule(question)")
    plan = code.index("var plan = SmartReplyRouterService.BuildPlan(seller, buyer, question)")
    direct = code.index("plan.Route == SmartReplyRouteKind.DirectKnowledge", plan)
    prompt = code.index("SmartReplyRouterService.BuildPromptAddon(plan)", direct)
    assert manual < plan < direct < prompt
    assert "StorePromptProfileService.BuildPromptAddon()" in code
    assert "ConversationSessionLearningService.BuildReplyStylePromptAddon(seller)" in code
    assert "plan.ContextDigest" in code
    assert "plan.RecentTurns" in code
    assert "智能路由-知识上下文" in code


def test_legacy_non_streaming_local_hit_is_gated_by_same_smart_router():
    helper = read("src/Bot/ChromeNs/KnowledgeContextualReplyService.cs")
    assert "SmartReplyRouterService.BuildPlan" in helper
    assert "decision.SmartPlan.Route != SmartReplyRouteKind.DirectKnowledge" in helper
    assert "SmartReplyRouterService.BuildPromptAddon(decision.SmartPlan)" in helper
    assert "StorePromptProfileService.BuildPromptAddon()" in helper


def test_store_prompt_can_be_generated_saved_and_injected():
    service = read("src/Bot/ChromeNs/StorePromptProfileService.cs")
    ui = read("src/Bot/Knowledge/StorePromptProfileUi.cs")
    app = read("src/Bot/App.xaml.cs")
    targets = read("src/Directory.Build.targets")

    assert "store-prompt-profile.json" in service
    assert "GenerateStandardPromptAsync" in service
    assert "MyOpenAI.CallStructuredChat(messages, 4000, 0.05, 240" in service
    assert "店铺固定事实与服务边界" in service
    assert "不得自行扩大链接服务范围" in service
    assert 'Content = "店铺提示词"' in ui
    assert 'Content = "AI生成标准提示词"' in ui
    assert "StorePromptProfileUi.Initialize()" in app
    assert "StorePromptProfileService.cs" in targets
    assert "StorePromptProfileUi.cs" in targets
