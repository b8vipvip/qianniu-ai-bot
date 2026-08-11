from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_order_event_hub_fallback_bootstraps_from_existing_compiled_source():
    code = read("src/Bot/ChromeNs/BotActivityCoordinator.cs")
    assert "_orderEventAutoReplyFallbackBootstrap" in code
    assert "OrderEventAutoReplyFallback.InitializeForApp()" in code
    assert "order-event-state.json" in code
    assert 'root["Events"] as JArray' in code
    assert 'item["Snapshot"].ToObject<OrderSnapshot>()' in code


def test_fallback_only_consumes_new_created_or_paid_events():
    code = read("src/Bot/ChromeNs/BotActivityCoordinator.cs")
    assert "seenAt < StartedAt.AddSeconds(-2)" in code
    assert "snapshot.EventType != OrderEventType.Created" in code
    assert "snapshot.EventType != OrderEventType.Paid" in code
    assert "Scheduled.TryAdd(key, DateTime.Now)" in code
    assert "CleanupScheduled()" in code


def test_normal_order_pipeline_keeps_priority_and_fallback_does_not_race_it():
    code = read("src/Bot/ChromeNs/BotActivityCoordinator.cs")
    assert "await Task.Delay(1200)" in code
    assert "BotActivityCoordinator.GetSnapshot(snapshot.Seller)" in code
    for marker in ["下单自动回复", "订单模板", "订单交易", "下单交易"]:
        assert marker in code
    assert "等待现有订单发送链路超过30秒，未并发抢发" in code


def test_fallback_resolves_exact_shop_scope_and_never_guesses_cross_shop():
    code = read("src/Bot/ChromeNs/BotActivityCoordinator.cs")
    assert "QN.FindExistingBySellerNick(seller)" in code
    assert "DirectOrderIdentityResolver.IdentityEquals(x.Seller.Nick, seller)" in code
    assert "ShopContextLocator.ResolveBySellerNick(snapshot.Seller)" in code
    assert "using (ShopSettingsScope.Enter(shop))" in code
    assert "拒绝跨店猜测" in code


def test_accepted_hub_event_builds_plan_without_republishing_order_event():
    code = read("src/Bot/ChromeNs/BotActivityCoordinator.cs")
    method = code[code.index("ProcessAcceptedOrderEventFallbackAsync"):]
    assert "_messageSafetyStartedAt.AddSeconds(-8)" in method
    assert "BotFeatureStore.GetAutoReplyRules()" in method
    assert "cfg.EnableOrderPlacedReply" in method
    assert "BuyerIdentityAliasService.ResolveInternalNick" in method
    assert "OrderGuidanceDeliveryGuard.ObserveOrder(snapshot)" in method
    assert "EnqueueNewOrderAttention(snapshot)" in method
    assert "new OrderPlacedReplyPlan" in method
    assert "OrderEventHub.Publish(snapshot)" not in method


def test_fallback_reuses_existing_enrichment_guard_and_safe_send_pipeline():
    code = read("src/Bot/ChromeNs/BotActivityCoordinator.cs")
    method = code[code.index("ProcessAcceptedOrderEventFallbackAsync"):]
    assert 'OrderTemplateRequiredFieldsV2.TryOwnExistingPlan(this, plan, "OrderEventHub统一兜底")' in method
    assert "await ProcessOrderPlacedReplyAsync(plan);" in method

    order = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    assert "OrderGuidanceDeliveryGuard.ShouldSuppressBeforeSend(this, plan, answer" in order
    assert "KnowledgeLearningService.AllowNextManualSend(plan.Seller, plan.Buyer, answer);" in order
    assert "SendTextWithRetryAsync(plan.Buyer, answer, 1)" in order
    assert "OrderGuidanceDeliveryGuard.MarkDelivered(" in order


def test_fallback_logs_explicit_no_send_reasons_for_operator_diagnostics():
    code = read("src/Bot/ChromeNs/BotActivityCoordinator.cs")
    for text in [
        "未发送原因=Bot已停用",
        "未发送原因=本店下单自动发送关闭",
        "卖家身份不匹配",
        "缺少可验证买家身份",
        "Hub兜底跳过历史订单",
        "Hub兜底接管已确认订单",
    ]:
        assert text in code
