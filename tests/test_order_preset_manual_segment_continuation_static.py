from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_order_fixed_preset_uses_segment_aware_sender():
    source = text("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")

    assert "SendOrderPresetAnswerAsync" in source
    assert "ShouldSuppressOrderPresetBeforeSend" in source
    assert "TryFindRecentEquivalentSellerReply" in source
    assert "OrderGuidanceDeliveryGuard.EquivalentGuidance(manualAnswer, segment)" in source
    assert "KnowledgeLearningService.TryTakeSendBlock" in source
    assert "KnowledgeLearningService.AllowNextManualSend(plan.Seller, plan.Buyer, segment)" in source
    assert "人工回复与当前固定预设分段不同，继续发送本段" in source
    assert "人工客服已发送相同固定预设分段，跳过本段并继续剩余分段" in source


def test_partial_manual_match_does_not_suppress_whole_segmented_preset():
    source = text("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")

    helper = source.index("private bool ShouldSuppressOrderPresetBeforeSend")
    sender = source.index("private async Task<OrderPresetSendResult> SendOrderPresetAnswerAsync", helper)
    block = source[helper:sender]

    assert "segments.Count <= 1" in block
    assert "stateProbe" in block
    assert "matchedSegments == segments.Count" in block
    assert "matchedSegments > 0" in block
    assert "部分分段已由人工客服完成，剩余分段继续发送" in block


def test_manual_intervention_still_blocks_ordinary_ai_replies():
    rpa = text("src/Bot/ChromeNs/QNRpa.cs")

    assert "KnowledgeLearningService.TryBlockForManualReply(_qn, buyer, text" in rpa
    assert "return false;" in rpa[rpa.index("KnowledgeLearningService.TryBlockForManualReply(_qn, buyer, text"):][:300]
