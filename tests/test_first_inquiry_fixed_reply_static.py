from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_auto_reply_rules_expose_custom_first_inquiry_reply():
    source = read("src/Bot/Options/FeatureSettingsOptionsControl.cs")
    assert "AddFirstInquiryFixedReplyCard" in source
    assert '"自动回复规则"' in source
    assert '"启用首条咨询固定回复"' in source
    assert '"固定答案"' in source
    assert "FirstInquiryFixedReplyService.Load(Seller)" in source
    assert "FirstInquiryFixedReplyService.Save(" in source
    assert "_firstInquiryFixedReplyAnswer.Text" in source


def test_first_inquiry_reply_is_shop_scoped_and_customizable():
    source = read("src/Bot/ChromeNs/QN.RuntimeSafety.cs")
    assert 'SettingsScope = "feature"' in source
    assert 'EnabledKey = "FirstInquiryFixedReplyEnabled"' in source
    assert 'AnswerKey = "FirstInquiryFixedReplyAnswer"' in source
    assert "ShopSettingsScope.Current" in source
    assert "ShopContextLocator.ResolveRuntimeBySellerNick" in source
    assert "PersistentParams.TrySaveParam2Key" in source
    assert "PersistentParams.GetParam2Key" in source


def test_first_inquiry_defaults_to_enabled_and_expected_answer():
    source = read("src/Bot/ChromeNs/QN.RuntimeSafety.cs")
    assert 'DefaultAnswer = "在的，亲！"' in source
    assert 'EnabledKey,\n                SettingsScope,\n                "true"' in source
    assert 'AnswerKey,\n                SettingsScope,\n                DefaultAnswer' in source


def test_first_inquiry_is_once_per_30_minute_consultation_session():
    source = read("src/Bot/ChromeNs/QN.RuntimeSafety.cs")
    assert "SessionResetMinutes = 30" in source
    assert "ConversationContextStore.GetRecentTurns(" in source
    assert "currentQuestion" in source
    assert "latestPrior.Timestamp == DateTime.MinValue" in source
    assert "latestPrior.Timestamp >= now.AddMinutes(-SessionResetMinutes)" in source
    assert "TriggeredAt" in source
    assert "PendingReplies" in source


def test_any_fresh_buyer_or_system_message_can_prepare_fixed_reply():
    service = read("src/Bot/ChromeNs/QN.RuntimeSafety.cs")
    router = read("src/Bot/ChromeNs/VisionMessageDecision.cs")
    assert "public static bool TryPrepare(" in service
    assert 'decision.MessageLabel, "历史消息"' in service
    assert "FirstInquiryFixedReplyService.TryPrepare(" in router
    prepare = router.index("FirstInquiryFixedReplyService.TryPrepare(")
    ordinary_text = router.index("if (safetyDecision.ShouldCallAi)", prepare)
    image_route = router.index('if (!string.Equals(safetyDecision.MessageLabel, "[图片]"', ordinary_text)
    assert prepare < ordinary_text < image_route
    assert "IncomingMessageSafety.GetDisplayText(message, text)" in router
    assert "Kind = VisionDecisionKind.Text" in router[prepare:ordinary_text]


def test_platform_system_tips_are_eligible_before_normal_skip_routing():
    safety = read("src/Bot/ChromeNs/IncomingMessageSafety.cs")
    router = read("src/Bot/ChromeNs/VisionMessageDecision.cs")
    assert 'Skip("[淘宝系统提示]"' in safety
    prepare = router.index("FirstInquiryFixedReplyService.TryPrepare(")
    normal_skip = router.index("return Skip(safetyDecision.MessageLabel, safetyDecision.Note);", prepare)
    assert prepare < normal_skip


def test_fixed_first_reply_skips_ai_and_uses_normal_send_pipeline():
    source = read("src/Bot/ChromeNs/QN.cs")
    fixed = source.index("FirstInquiryFixedReplyService.TryResolve(")
    ai = source.index("MyOpenAI.GetAnswer(", fixed)
    send = source.index("SendTextWithRetryAsync(burst.BuyerNick, answer, 1)", ai)
    assert fixed < ai < send
    assert "if (usedFirstInquiryFixedReply)" in source[fixed:ai]
    assert '"首条咨询固定回复"' in source
    assert "BotOutboundMessageFormatter.EnsureAiMarker(answer)" in source
    assert "if (!usedFirstInquiryFixedReply)" in source
    assert "ReplyDeduplicationService.RememberDelivered" in source[send:]


def test_fixed_reply_does_not_enter_ai_learning_path():
    source = read("src/Bot/ChromeNs/QN.cs")
    assert 'if (string.Equals(answerSource, "AI生成", StringComparison.Ordinal))' in source
    assert 'KnowledgeLearningService.RegisterAnswerSource(' in source
    assert '"首条咨询固定回复"' in source
