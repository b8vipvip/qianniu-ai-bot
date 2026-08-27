from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")

def test_strict_classifier_does_not_treat_generic_tid_or_status_as_order():
    code = read("src/Bot/ChromeNs/OrderGuidanceDeliveryGuard.cs")
    order_keys = code.split("StrongOrderIdKeyRegex", 1)[1].split("RegexOptions", 1)[0].lower()
    assert "orderid" in order_keys; assert "bizorderid" in order_keys; assert "tradeid" in order_keys; assert "|tid" not in order_keys
    assert "普通消息 tid 不作为订单号" in code; assert "structureScore >= 2" in code; assert "StrongSystemCueRegex" in code

def test_order_plan_requires_strict_evidence_before_parser():
    code = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    strict_at = code.index("OrderMessageClassifier.IsConfirmedOrderEvent"); followup_at = code.index("TryCreateBuyerFollowUpPlan", strict_at); parse_at = code.index("OrderCardParser.TryParse")
    assert strict_at < followup_at < parse_at
    helper = code[code.index("private static bool TryCreateBuyerFollowUpPlan"):]
    assert "OrderGuidanceDeliveryGuard.CanCreateFollowUp" in helper; assert "return false;" in helper

def test_initial_guidance_is_persisted_once_across_created_paid_and_restart():
    guard = read("src/Bot/ChromeNs/OrderGuidanceDeliveryGuard.cs"); service = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    assert "order-guidance-delivery-state.json" in guard; assert "InitialDeliveredAt" in guard; assert "该订单的充值流程已经发送过一次" in guard
    assert "OrderGuidanceDeliveryGuard.ObserveOrder(snapshot)" in service; assert "OrderGuidanceDeliveryGuard.MarkDelivered" in service
    assert "Bot强制订单规则发送" in service

def test_manual_equivalent_reply_does_not_suppress_mandatory_order_rule():
    guard = read("src/Bot/ChromeNs/OrderGuidanceDeliveryGuard.cs"); service = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    assert "ConversationContextStore.GetRecentTurns" in guard; assert "EquivalentGuidance" in guard
    process = service[service.index("private async Task ProcessOrderPlacedReplyAsync"):]
    assert "OrderGuidanceDeliveryGuard.ShouldSuppressBeforeSend" not in process
    assert "ResponseProgressTracker.HasActiveManualIntervention" not in process
    assert "SendMandatoryOrderTextAsync" in process

def test_only_explicit_followups_allow_one_extra_guidance_message():
    guard = read("src/Bot/ChromeNs/OrderGuidanceDeliveryGuard.cs"); service = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    for phrase in ["拍了", "下单了", "怎么充", "怎么充值"]: assert phrase in guard
    for negative in ["没下单", "还没付款", "怎么下单"]: assert negative in guard
    assert "FollowUpDeliveredAt.HasValue" in guard; assert "该订单已经补发过一次充值流程" in guard
    assert "#guidance-followup" in service; assert "订单规则强制补发一次" in service

def test_build_includes_guard():
    targets = read("src/Directory.Build.targets")
    assert "ChromeNs\\OrderGuidanceDeliveryGuard.cs" in targets
