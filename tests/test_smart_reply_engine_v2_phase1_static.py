from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_conversation_state_tracks_topic_entity_goal_pending_question_and_stage():
    code = read("src/Bot/ChromeNs/ConversationStateService.cs")
    assert "class ConversationStateSnapshot" in code
    assert "CurrentTopic" in code
    assert "CurrentEntity" in code
    assert "BuyerGoal" in code
    assert "PendingQuestion" in code
    assert "ConversationStage" in code
    assert "ConfirmedFacts" in code
    assert "ExtractKnownEntities" in code
    assert "FindPendingQuestion" in code
    assert "DetectStage" in code


def test_contextual_query_rewrite_is_local_and_only_runs_for_dependent_queries():
    code = read("src/Bot/ChromeNs/ConversationStateService.cs")
    assert "class ContextualQueryResolution" in code
    assert "ContextualQueryRewriteService" in code
    assert "contextDependencyScore < 0.30" in code
    assert "买家回答/追问" in code
    assert "买家当前追问" in code
    assert "已继承最近主题" in code
    assert "MyOpenAI" not in code


def test_router_uses_rewritten_query_and_structured_state_before_top3_prompt():
    router = read("src/Bot/ChromeNs/SmartReplyRouterService.cs")
    assert "ConversationStateService.Build" in router
    assert "ContextualQueryRewriteService.Resolve" in router
    assert "QueryResolution" in router
    assert "ConversationState" in router
    assert "ResolvedQueryScore" in router
    assert "EntityScore" in router
    assert "originalScore * 0.38" in router
    assert "resolvedScore * 0.28" in router
    assert "keywordScore * 0.12" in router
    assert "entityScore * 0.10" in router
    assert "PromptCandidateCount = 3" in router
    assert "上下文问题还原" in router
    assert "当前会话状态" in read("src/Bot/ChromeNs/ConversationStateService.cs")


def test_rewritten_queries_cannot_take_direct_fixed_answer_path():
    router = read("src/Bot/ChromeNs/SmartReplyRouterService.cs")
    assert "resolution != null && resolution.Rewritten" in router
    assert "if (resolution != null && resolution.Rewritten) return false;" in router
    assert "plan.QueryResolution == null || !plan.QueryResolution.Rewritten" in router


def test_new_source_is_included_in_wpf_temp_and_normal_builds():
    targets = read("src/Directory.Build.targets")
    assert "ChromeNs\\ConversationStateService.cs" in targets
