from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "Bot" / "ChromeNs" / "OrderTemplateTradeDetailBridge.cs"
PROPS = ROOT / "src" / "Directory.Build.props"
TEMPLATE = ROOT / "src" / "Bot" / "ChromeNs" / "OrderPlacedAutoReplyService.cs"
TRADE_MODEL = ROOT / "src" / "DbEntity" / "Response" / "ZnkfTradeQueryResponse.cs"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def test_trade_detail_bridge_is_compiled_and_bootstraps_before_constructor_bridges():
    source = read(SOURCE)
    props = read(PROPS)

    assert "OrderTemplateTradeDetailBridge.cs" in props
    assert "private static readonly object OrderTemplateTradeDetailBootstrap" in source
    assert "OrderTemplateTradeDetailBridge.InitializeForApp()" in source
    assert "new Timer(_ => Attach(), null, 0, 10)" in source
    assert "qn.EvMessageNotity += OnMessageNotify" in source


def test_sparse_event_is_owned_before_any_async_trade_query_or_old_bridge_send():
    source = read(SOURCE)

    publish = source.index("var published = OrderEventHub.Publish(snapshot)")
    task_run = source.index("Task.Run(async () => await EnrichAndSendAsync")
    trade_query = source.index("await qn.GetBuyerTrades")

    assert publish < task_run < trade_query
    assert "旧的稀疏桥接随后看到相同事件会直接去重" in source
    assert "OrderPlacedAutoReplyService.Complete(plan, true)" in source


def test_configured_placeholders_trigger_trade_detail_enrichment():
    source = read(SOURCE)

    for placeholder in ("{规格}", "{数量}", "{实付}", "{金额}", "{商品}"):
        assert placeholder in source

    assert "NeedsTradeEnrichment(cfg, snapshot)" in source
    assert "string.IsNullOrWhiteSpace(snapshot.SkuText)" in source
    assert "snapshot.Quantity <= 0" in source
    assert "!snapshot.PaidAmount.HasValue" in source


def test_trade_query_recovers_sku_quantity_and_paid_amount():
    source = read(SOURCE)
    trade_model = read(TRADE_MODEL)

    assert "await qn.GetBuyerTrades(securityBuyerUid ?? string.Empty, plan.OrderId)" in source
    assert "await qn.SearchBuyerUser(buyer)" in source
    assert "EncryptAccountId" in source
    assert "FindExactTrade(response, plan.OrderId)" in source

    assert ".Select(x => NormalizeSku(x.sku))" in source
    assert "x.buyAmount > 0 ? x.buyAmount" in source
    assert "ParseMoney(trade.orderPrice)" in source
    assert "trade.payTime.HasValue" in source
    assert "snapshot.PaidAmount = total" in source

    for model_field in ("public string sku", "public int buyAmount", "public string orderPrice", "public DateTime? payTime"):
        assert model_field in trade_model


def test_query_waits_for_late_payment_event_and_reuses_original_safe_send_pipeline():
    source = read(SOURCE)

    assert "new[] { 0, 500, 1000, 2000, 3000, 4000, 5000 }" in source
    assert 'BotActivityCoordinator.Begin("下单交易字段补全"' in source
    assert "ProcessOrderTemplateTradeDetailPlanAsync" in source
    assert "return ProcessOrderPlacedReplyAsync(plan)" in source
    assert "OrderGuidanceDeliveryGuard.ObserveOrder" in source
    assert "qn.EnqueueNewOrderAttention" in source


def test_sku_normalization_covers_the_visible_qianniu_format():
    source = read(SOURCE)

    assert ".Replace('：', ':')" in source
    assert "专辑名称|套餐名称|套餐|期限|时长|会员类型" in source
    assert 'value = known.Groups[1].Value.Trim() + ":" + known.Groups[2].Value.Trim()' in source


def test_existing_template_renderer_consumes_the_enriched_snapshot():
    template = read(TEMPLATE)

    assert '.Replace("{规格}", snapshot == null ? string.Empty : snapshot.SkuText ?? string.Empty)' in template
    assert '.Replace("{数量}", snapshot == null || snapshot.Quantity <= 0 ? string.Empty : snapshot.Quantity.ToString())' in template
    assert '.Replace("{实付}", snapshot == null || !snapshot.PaidAmount.HasValue ? string.Empty : snapshot.PaidAmount.Value.ToString("0.00"))' in template


def test_raw_order_payload_is_hashed_but_never_logged_or_persisted_verbatim():
    source = read(SOURCE)

    assert "RawCardHash = Hash(raw)" in source
    assert "File.WriteAllText" not in source
    assert "+ raw" not in source
    assert "Log.Info(raw" not in source
