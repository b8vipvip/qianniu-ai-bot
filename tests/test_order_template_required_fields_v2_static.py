from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "Bot" / "ChromeNs" / "OrderTemplateRequiredFieldsV2.cs"
PROPS = ROOT / "src" / "Directory.Build.props"
RENDERER = ROOT / "src" / "Bot" / "ChromeNs" / "OrderPlacedAutoReplyService.cs"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def test_unified_v2_bridge_replaces_the_two_racing_runtime_bridges():
    source = read(SOURCE)
    props = read(PROPS)

    assert "OrderTemplateRequiredFieldsV2.cs" in props
    assert '<Compile Include="$(MSBuildProjectDirectory)\\ChromeNs\\OrderTemplateRequiredFieldsV2.cs"' in props
    assert '<Compile Include="$(MSBuildProjectDirectory)\\ChromeNs\\OrderTemplateTradeDetailBridge.cs"' not in props
    assert '<Compile Include="$(MSBuildProjectDirectory)\\ChromeNs\\OrderTemplateReceiveNewMessageTradeDetailBridge.cs"' not in props
    assert "supersedes" in props
    assert "qn.EvRecieveNewMessage += OnReceiveNewMessage" in source
    assert "qn.EvMessageNotity += OnMessageNotify" in source


def test_new_templates_and_settings_ui_use_sku_while_legacy_alias_remains_compatible():
    source = read(SOURCE)
    renderer = read(RENDERER)

    assert 'template.Contains("{sku}")' in source
    assert 'template.Contains("{规格}")' in source
    assert 'box.Text.Replace("{规格}", "{sku}")' in source
    assert 'button.Content = content.Replace("{规格}", "{sku}")' in source
    assert 'button.Tag = tag.Replace("{规格}", "{sku}")' in source
    assert "新模板统一使用 {sku}；旧 {规格} 仍兼容" in source
    assert '.Replace("{sku}", snapshot == null ? string.Empty : snapshot.SkuText ?? string.Empty)' in renderer
    assert '.Replace("{规格}", snapshot == null ? string.Empty : snapshot.SkuText ?? string.Empty)' in renderer


def test_both_sku_aliases_trigger_trade_enrichment_and_required_field_validation():
    source = read(SOURCE)

    assert 'template.Contains("{sku}")\n                || template.Contains("{规格}")' in source
    assert '(template.Contains("{sku}") || template.Contains("{规格}"))' in source
    assert 'missing.Add("sku")' in source
    assert 'missing.Add("quantity")' in source
    assert 'missing.Add("paid")' in source
    assert 'missing.Add("total")' in source
    assert 'missing.Add("item")' in source
    assert 'missing.Add("status")' in source


def test_missing_configured_fields_block_send_and_release_reservation_first():
    source = read(SOURCE)

    validate = source.index("var missing = MissingRequiredFields")
    release = source.index("OrderPlacedAutoReplyService.Complete(plan, false)", validate)
    blocked_log = source.index('blocked_blank_template=true', release)
    send = source.index("await qn.ProcessOrderTemplateRequiredFieldsPlanAsync(plan)", blocked_log)

    assert validate < release < blocked_log < send
    assert "if (blocked)" in source[validate:send]
    assert "绝不发送“订单：”空模板" in source
    assert "后续付款通知可重新创建计划并再次查询" in source


def test_enrichment_logs_each_requested_diagnostic_without_raw_order_payload():
    source = read(SOURCE)

    for field in (
        "trade_found=",
        "buyer_security_id_found=",
        "sku_found=",
        "quantity_found=",
        "paid_found=",
        "total_found=",
        "blocked_blank_template=",
    ):
        assert field in source

    assert "RawCardHash = Hash(raw)" in source
    assert "Log.Info(raw" not in source
    assert "Log.Error(raw" not in source
    assert "File.WriteAllText" not in source


def test_query_retries_are_bounded_for_structured_fields_and_payment_can_arrive_later():
    source = read(SOURCE)

    assert 'missingAtStart.Contains("sku") || missingAtStart.Contains("buyer_remark")' in source
    assert "new[] { 0, 250, 500, 1000, 1500 }" in source
    assert "new[] { 0, 500, 1000, 2000, 3000, 5000, 7000 }" not in source
    assert 'remaining.Contains("sku")' in source
    assert 'remaining.Contains("buyer_remark")' in source
    assert "trade.payTime ?? itemPayTime" in source
    assert "snapshot.PaidAmount = total" in source
    assert "snapshot.EventType = OrderEventType.Paid" in source
    assert "Inflight.TryRemove(inflightKey" in source
    assert "OrderPlacedAutoReplyService.Complete(plan, false)" in source


def test_required_fields_v2_reuses_structured_sku_parser_before_render_and_after_trade_query():
    source = read(SOURCE)
    assert "SkuText = OrderSkuPayloadRecoveryBridge.ResolveSkuTextFromPayload(raw)" in source
    assert "JObject.FromObject(trade).ToString(Formatting.None)" in source
    assert "OrderSkuPayloadRecoveryBridge.ResolveSkuTextFromPayload(" in source


def test_success_path_reuses_existing_safe_order_reply_pipeline():
    source = read(SOURCE)

    publish = source.index("OrderEventHub.Publish(snapshot)")
    observe = source.index("OrderGuidanceDeliveryGuard.ObserveOrder(snapshot)", publish)
    attention = source.index("qn.EnqueueNewOrderAttention(snapshot)", observe)
    send = source.index("await qn.ProcessOrderTemplateRequiredFieldsPlanAsync(plan)", attention)

    assert publish < observe < attention < send
    assert "return ProcessOrderPlacedReplyAsync(plan)" in source
    assert "BotActivityCoordinator.Begin" in source
