from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_legacy_non_streaming_path_only_directs_when_smart_router_allows_it():
    code = read("src/Bot/ChromeNs/MyOpenAI.cs")
    helper = read("src/Bot/ChromeNs/KnowledgeContextualReplyService.cs")
    assert "KnowledgeLearningService.TryFindLocalAnswer" in code
    assert "if (!contextDecision.IsFollowUp)" in code
    assert "SmartReplyRouterService.BuildPlan" in helper
    assert "decision.SmartPlan.Route != SmartReplyRouteKind.DirectKnowledge" in helper


def test_contextual_knowledge_hit_uses_top_candidates_and_context_instead_of_fixed_copy():
    helper = read("src/Bot/ChromeNs/KnowledgeContextualReplyService.cs")
    router = read("src/Bot/ChromeNs/SmartReplyRouterService.cs")
    assert "SmartReplyRouterService.BuildPromptAddon(decision.SmartPlan)" in helper
    assert "不得原样重复上一条客服回复或整段知识库答案" in helper
    assert "当前消息不应机械套用固定答案" in helper
    assert "PromptCandidateCount = 3" in router
    assert "这些知识是候选事实依据，不是必须原样发送的固定答案" in router


def test_tv_karaoke_confirmation_keeps_safe_offline_fallback_for_legacy_path():
    helper = read("src/Bot/ChromeNs/KnowledgeContextualReplyService.cs")
    assert "ExtractConfirmedFeature" in helper
    assert "那就可以使用" in helper
    assert "功能" in helper
    assert '"有"' in helper
    assert '"支持"' in helper
    assert '"不支持"' in helper


def test_contextual_replies_and_cancelled_drafts_do_not_pollute_learning():
    code = read("src/Bot/ChromeNs/MyOpenAI.cs")
    assert 'var answerSource = contextualKnowledge == null ? "AI生成" : "本地知识库上下文"' in code
    local_branch = code.index("if (contextualKnowledge == null)")
    deferred_guard = code.index("if (!deferLearningUntilDelivered)", local_branch)
    queued = code.index("KnowledgeLearningService.QueueLearn", deferred_guard)
    contextual_branch = code.index("else", queued)
    contextual_log = code.index("上下文知识回复生成成功", contextual_branch)
    assert local_branch < deferred_guard < queued < contextual_branch < contextual_log
