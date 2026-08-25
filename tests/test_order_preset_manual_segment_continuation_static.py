from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_order_fixed_preset_stops_all_remaining_segments_after_manual_takeover():
    source = text("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")

    assert "SendOrderPresetAnswerAsync" in source
    assert "ShouldSuppressOrderPresetBeforeSend" in source
    assert "TryFindRecentEquivalentSellerReply" in source
    assert "KnowledgeLearningService.TryTakeSendBlock" in source
    assert "OrderPresetSegmentOutcome.CancelledByManual" in source
    assert "ResponseProgressTracker.HasActiveManualIntervention" in source
    assert "停止本段及全部剩余分段" in source
    assert "KnowledgeLearningService.AllowNextManualSend(plan.Seller, plan.Buyer, segment)" not in source
    assert "人工回复与当前固定预设分段不同，继续发送本段" not in source


def test_active_manual_takeover_is_checked_before_order_duplicate_suppression_and_send():
    source = text("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")

    process = source.index("private async Task ProcessOrderPlacedReplyAsync")
    manual = source.index("ResponseProgressTracker.HasActiveManualIntervention", process)
    suppress = source.index("ShouldSuppressOrderPresetBeforeSend", manual)
    assert manual < suppress
    assert "已停止：检测到人工客服已回复" in source
    assert "CancelledByManual" in source


def test_manual_intervention_still_blocks_ordinary_ai_replies():
    rpa = text("src/Bot/ChromeNs/QNRpa.cs")
    reliable = text("src/Bot/ChromeNs/QNRpa.ReliableSend.cs")
    qn = text("src/Bot/ChromeNs/QN.cs")

    assert "KnowledgeLearningService.TryBlockForManualReply(_qn, buyer, text" in rpa
    block = rpa[rpa.index("KnowledgeLearningService.TryBlockForManualReply(_qn, buyer, text"):][:500]
    assert 'SetSendCancellation("人工接管"' in block
    assert "LastSendWasCancelled" in reliable
    assert "自动发送因人工接管取消，禁止重试" in qn
