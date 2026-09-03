from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_required_fields_priority_guard_is_compiled_with_v2_runtime():
    props = read("src/Directory.Build.props")
    bot_props = read("src/Bot/Directory.Build.props")

    # Bot has its own Directory.Build.props, so it must keep importing the repository-level
    # compile contract used by both Bot and WPF *_wpftmp projects.
    assert "..\\Directory.Build.props" in bot_props

    v2 = '<Compile Include="$(MSBuildProjectDirectory)\\ChromeNs\\OrderTemplateRequiredFieldsV2.cs" />'
    priority = 'ChromeNs\\OrderTemplateRequiredFieldsPriority.cs'
    assert v2 in props
    assert priority in props

    # Keep both files in the same legacy compile ItemGroup. A source file merely existing in the
    # repository is not enough for this old project layout; this guards the 1.1.1173 regression.
    v2_pos = props.index("OrderTemplateRequiredFieldsV2.cs")
    priority_pos = props.index("OrderTemplateRequiredFieldsPriority.cs", v2_pos)
    recharge_pos = props.index("RechargeStatusAutoQueryService.cs", priority_pos)
    assert v2_pos < priority_pos < recharge_pos


def test_priority_guard_keeps_required_field_owner_before_legacy_order_consumers():
    priority = read("src/Bot/ChromeNs/OrderTemplateRequiredFieldsPriority.cs")
    v2 = read("src/Bot/ChromeNs/OrderTemplateRequiredFieldsV2.cs")

    assert "OrderTemplateRequiredFieldsPriority.InitializeForApp()" in priority
    assert "EnsureRequiredOrderFieldsNotifyHandlerFirst" in priority
    assert "typeof(OrderTemplateRequiredFieldsV2)" in priority
    assert 'string.Equals(d.Method.Name, "OnMessageNotify", StringComparison.Ordinal)' in priority
    assert "EvMessageNotity = null;" in priority
    assert "EvMessageNotity += (EventHandler<MessageNotifyEventArgs>)handler;" in priority
    assert "订单模板字段 V2 已提升为 messageCenterNotify 第一消费者" in priority

    # The winning V2 consumer must synchronously reserve/complete the legacy plan before its
    # asynchronous trade enrichment starts, otherwise the old immediate renderer can still win.
    start = v2.index("private static void StartOwnedPlan")
    block = v2[start:v2.index("private static async Task EnrichValidateAndSendAsync", start)]
    complete = block.index("OrderPlacedAutoReplyService.Complete(plan, true)")
    background = block.index("Task.Run")
    assert complete < background


def test_v2_enrichment_waits_for_trade_fields_before_rendering_owned_plan():
    v2 = read("src/Bot/ChromeNs/OrderTemplateRequiredFieldsV2.cs")

    enrich_start = v2.index("private static async Task EnrichValidateAndSendAsync")
    process_call = v2.index("ProcessOrderTemplateRequiredFieldsPlanAsync", enrich_start)
    trade_call = v2.index("TryEnrichFromTradeApiAsync", enrich_start)
    assert trade_call < process_call

    # This bounded retry schedule is deliberate: configured dynamic fields get a chance to arrive
    # from the exact trade query, without making the ordinary no-field order path wait forever.
    assert "500" in v2 and "1000" in v2 and "2000" in v2 and "3000" in v2 and "5000" in v2 and "7000" in v2
    assert "TradeQueryAttempts" in v2
    assert "SkuFound" in v2
    assert "BuyerRemarkFound" in v2
