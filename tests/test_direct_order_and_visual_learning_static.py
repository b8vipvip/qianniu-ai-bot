from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_direct_order_recovery_accepts_system_order_cards_for_known_buyer():
    recovery = read("src/Bot/ChromeNs/QN.MessageRecovery.cs")

    assert "IsPotentialRecoveredOrderCard" in recovery
    assert "ProcessRecoveredMessageWithKnownBuyerAsync" in recovery
    assert "OrderPlacedAutoReplyService.TryCreatePlan" in recovery
    assert "ProcessOrderPlacedReplyAsync(orderPlan)" in recovery
    assert "详细新消息事件未到或可能为系统订单卡片" in recovery
    assert "IsBuyerMessage(m)" in recovery
    assert "|| IsPotentialRecoveredOrderCard(m)" in recovery


def test_vision_request_returns_semantic_signature_and_reuses_human_visual_knowledge():
    vision = read("src/Bot/ChromeNs/VisionRequestService.cs")

    assert '"visual_question"' in vision
    assert '"visual_summary"' in vision
    assert '"visual_tags"' in vision
    assert "VisualKnowledgeLearningService.TryFindMatch" in vision
    assert "VisualKnowledgeLearningService.RecordVisionAnalysis" in vision
    assert 'source = string.IsNullOrWhiteSpace(result.MatchedVisualKnowledgeId) ? "AI生成" : "视觉知识"' in vision


def test_visual_knowledge_is_persistent_human_confirmed_and_conservative():
    service = read("src/Bot/ChromeNs/VisualKnowledgeLearningService.cs")
    targets = read("src/Directory.Build.targets")

    assert "VisualKnowledgeObservationEntity" in service
    assert "VisualKnowledgeEntryEntity" in service
    assert "BuyerQuietMinutes = 5" in service
    assert "SellerQuietSeconds = 30" in service
    assert "IsBotReply" in service
    assert 'EndsWith("[AI]"' in service
    assert "ContainsHighRisk" in service
    assert "MatchThreshold = 0.74" in service
    assert 'SourceType = "视觉人工学习"' in service
    assert "create table if not exists VisualKnowledgeEntryEntity" in service
    assert "VisualKnowledgeLearningService.cs" in targets


def test_visual_learning_stores_semantics_not_raw_image_payloads():
    service = read("src/Bot/ChromeNs/VisualKnowledgeLearningService.cs")

    assert "ImageUrl" not in service
    assert "ImageBytes" not in service
    assert "VisualSummary" in service
    assert "VisualTags" in service
