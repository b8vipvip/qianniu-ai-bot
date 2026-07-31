from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "Bot" / "ChromeNs" / "OrderTemplateReceiveNewMessageTradeDetailBridge.cs"
PROPS = ROOT / "src" / "Directory.Build.props"
DIRECT = ROOT / "src" / "Bot" / "ChromeNs" / "DirectOrderEventBridge.cs"
NOTIFY = ROOT / "src" / "Bot" / "ChromeNs" / "OrderTemplateTradeDetailBridge.cs"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def test_receive_new_msg_enrichment_bridge_is_compiled_and_early_bootstrapped():
    source = read(SOURCE)
    props = read(PROPS)

    assert "OrderTemplateReceiveNewMessageTradeDetailBridge.cs" in props
    assert "private static readonly object OrderTemplateReceiveNewMessageTradeDetailBootstrap" in source
    assert "OrderTemplateReceiveNewMessageTradeDetailBridge.InitializeForApp()" in source
    assert "new Timer(_ => Attach(), null, 0, 10)" in source
    assert "qn.EvRecieveNewMessage += OnReceiveNewMessage" in source


def test_real_receive_new_msg_order_card_is_owned_before_legacy_direct_send():
    source = read(SOURCE)
    direct = read(DIRECT)

    assert "JsonConvert.DeserializeObject<ChatResponse>(raw)" in source
    assert "OrderPlacedAutoReplyService.TryCreatePlan" in source
    assert "OrderPlacedAutoReplyService.Complete(plan, true)" in source
    assert "Task.Run(async () => await EnrichAndSendAsync" in source
    assert "qn.EvRecieveNewMessage += OnReceiveNewMessage" in direct
    assert "new Timer(_ => Attach(), null, 0, 750)" in direct

    own = source.index("OrderPlacedAutoReplyService.TryCreatePlan")
    query = source.index("await qn.GetBuyerTrades")
    send = source.index("ProcessOrderTemplateTradeDetailPlanAsync")
    assert own < query < send


def test_receive_new_msg_path_keeps_strict_order_evidence_and_avoids_followup_hijack():
    source = read(SOURCE)

    assert "if (qn == null || raw.Length < 8 || !LooksPotential(raw)) return" in source
    assert "if (!MessageLooksPotential(message)) continue" in source
    assert "if (plan == null || plan.IsBuyerFollowUp) return" in source
    assert "订单号|订单编号|主订单号|子订单号|交易号|订单" in source
    assert "件商品" in source and "合计" in source and "交易时间" in source


def test_receive_new_msg_path_queries_exact_trade_and_recovers_template_fields():
    source = read(SOURCE)

    assert "await qn.GetBuyerTrades(securityBuyerUid ?? string.Empty, plan.OrderId)" in source
    assert "await qn.SearchBuyerUser(buyer)" in source
    assert "FindExactTrade(response, plan.OrderId)" in source
    assert ".Select(x => NormalizeSku(x.sku))" in source
    assert "x.buyAmount > 0 ? x.buyAmount" in source
    assert "ParseMoney(trade.orderPrice)" in source
    assert "trade.payTime ?? itemPayTime" in source
    assert "snapshot.PaidAmount = total" in source


def test_receive_new_msg_and_message_center_paths_cover_both_qianniu_order_sources():
    source = read(SOURCE)
    notify = read(NOTIFY)

    assert "qn.EvRecieveNewMessage += OnReceiveNewMessage" in source
    assert "qn.EvMessageNotity += OnMessageNotify" in notify
    assert "ProcessOrderTemplateTradeDetailPlanAsync" in source
    assert "ProcessOrderTemplateTradeDetailPlanAsync" in notify


def test_enriched_snapshot_reuses_existing_dedup_and_safe_send_pipeline():
    source = read(SOURCE)

    assert "OrderEventHub.Publish(plan.Snapshot)" in source
    assert "OrderGuidanceDeliveryGuard.ObserveOrder(plan.Snapshot)" in source
    assert "qn.EnqueueNewOrderAttention(plan.Snapshot)" in source
    assert 'BotActivityCoordinator.Begin("receiveNewMsg下单交易字段补全"' in source
    assert "await qn.ProcessOrderTemplateTradeDetailPlanAsync(plan)" in source
    assert "OrderPlacedAutoReplyService.Complete(plan, false)" in source


def test_receive_new_msg_bridge_does_not_log_or_persist_raw_order_payload():
    source = read(SOURCE)

    assert "File.WriteAllText" not in source
    assert "Log.Info(raw" not in source
    assert "+ raw" not in source
    assert "payload" not in source.lower() or "raw order payload" not in source.lower()
