from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_knowledge_remains_first_for_standalone_questions():
    code = read("src/Bot/ChromeNs/MyOpenAI.cs")
    assert "KnowledgeLearningService.TryFindLocalAnswer" in code
    assert "if (!contextDecision.IsFollowUp)" in code
    assert "命中本地知识库，未调用AI" in code
    assert "return localAnswer;" in code


def test_follow_up_knowledge_hit_is_contextualized_not_repeated():
    code = read("src/Bot/ChromeNs/MyOpenAI.cs")
    helper = read("src/Bot/ChromeNs/KnowledgeContextualReplyService.cs")
    assert "KnowledgeContextualReplyService.Analyze" in code
    assert "KnowledgeContextualReplyService.BuildPromptAddon" in code
    assert "不得原样重复上一条客服回复或整段知识库答案" in helper
    assert "当前买家消息是对上一轮客服回复的补充、确认或否定" in helper
    assert "上一条客服回复与本次命中的知识答案相同" in helper


def test_tv_karaoke_confirmation_has_safe_offline_fallback():
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
