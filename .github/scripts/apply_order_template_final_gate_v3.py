from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    if old not in text:
        raise RuntimeError(f"patch marker not found: {label}")
    return text.replace(old, new, 1)


direct_path = Path("src/Bot/ChromeNs/DirectOrderEventBridge.cs")
v2_path = Path("src/Bot/ChromeNs/OrderTemplateRequiredFieldsV2.cs")
service_path = Path("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")

direct = direct_path.read_text(encoding="utf-8-sig")
send_marker = "            await ProcessOrderPlacedReplyAsync(plan);"
insert = """            // 展开诊断桥接能够解析嵌套 messageCenterNotify，但不得绕过统一字段补全。
            // 计划已包含准确 seller、buyer 和 orderId，交给 V2 查询交易详情后再发送。
            if (OrderTemplateRequiredFieldsV2.TryOwnExistingPlan(this, plan, source)) return;

            await ProcessOrderPlacedReplyAsync(plan);"""
if direct.count(send_marker) != 1:
    raise RuntimeError(f"unexpected direct send marker count: {direct.count(send_marker)}")
direct_path.write_text(direct.replace(send_marker, insert, 1), encoding="utf-8-sig")

v2 = v2_path.read_text(encoding="utf-8-sig")
v2_marker = """        private static void StartOwnedPlan(QN qn, OrderPlacedReplyPlan plan, string source)
        {"""
v2_replacement = """        internal static bool TryOwnExistingPlan(
            QN qn,
            OrderPlacedReplyPlan plan,
            string source)
        {
            if (qn == null || plan == null || plan.Config == null || plan.Snapshot == null)
            {
                return false;
            }
            if (plan.IsBuyerFollowUp || !ShouldOwnConfiguredTemplate(plan.Config))
            {
                return false;
            }

            Log.Info("订单模板字段 V2 接收已解析计划: source=" + source
                + ", seller=" + plan.Seller
                + ", buyer=" + plan.Buyer
                + ", orderId=" + plan.OrderId);
            StartOwnedPlan(qn, plan, source + "->requiredFieldsV2");
            return true;
        }

        private static void StartOwnedPlan(QN qn, OrderPlacedReplyPlan plan, string source)
        {"""
v2 = replace_once(v2, v2_marker, v2_replacement, "V2 StartOwnedPlan")
v2_path.write_text(v2, encoding="utf-8-sig")

service = service_path.read_text(encoding="utf-8-sig")
if "using System.Collections.Generic;" not in service:
    service = replace_once(
        service,
        "using System.Collections.Concurrent;\n",
        "using System.Collections.Concurrent;\nusing System.Collections.Generic;\n",
        "generic using",
    )

resolve_marker = """        public static async Task<OrderPlacedReplyResolution> ResolveAsync(OrderPlacedReplyPlan plan)
        {
            if (plan == null || plan.Config == null)
            {
                return Fail("下单自动回复计划为空");
            }

            var cfg = plan.Config;
            var mode = string.IsNullOrWhiteSpace(cfg.OrderPlacedReplyMode)"""
resolve_replacement = """        internal static List<string> MissingRequiredTemplateFields(
            string template,
            OrderSnapshot snapshot)
        {
            var missing = new List<string>();
            template = template ?? string.Empty;

            if ((template.Contains("{sku}") || template.Contains("{规格}"))
                && (snapshot == null || string.IsNullOrWhiteSpace(snapshot.SkuText)))
            {
                missing.Add("sku");
            }
            if (template.Contains("{数量}") && (snapshot == null || snapshot.Quantity <= 0))
            {
                missing.Add("quantity");
            }
            if (template.Contains("{实付}") && (snapshot == null || !snapshot.PaidAmount.HasValue))
            {
                missing.Add("paid");
            }
            if (template.Contains("{金额}") && (snapshot == null || !snapshot.TotalAmount.HasValue))
            {
                missing.Add("total");
            }
            if (template.Contains("{商品}") && (snapshot == null || string.IsNullOrWhiteSpace(snapshot.ItemTitle)))
            {
                missing.Add("item");
            }
            if (template.Contains("{订单状态}") && (snapshot == null || string.IsNullOrWhiteSpace(snapshot.TradeStatus)))
            {
                missing.Add("status");
            }
            return missing;
        }

        private static OrderPlacedReplyResolution BlockIncompleteTemplate(
            OrderPlacedReplyPlan plan,
            string template,
            string source)
        {
            var missing = MissingRequiredTemplateFields(
                template,
                plan == null ? null : plan.Snapshot);
            if (missing.Count == 0) return null;

            Log.Info("order_template_final_gate blocked_blank_template=true"
                + " source=" + source
                + " orderId=" + (plan == null ? string.Empty : plan.OrderId)
                + " missing=" + string.Join(",", missing));
            return Fail("订单字段尚未完整：" + string.Join(",", missing));
        }

        public static async Task<OrderPlacedReplyResolution> ResolveAsync(OrderPlacedReplyPlan plan)
        {
            if (plan == null || plan.Config == null)
            {
                return Fail("下单自动回复计划为空");
            }

            var cfg = plan.Config;
            var mode = string.IsNullOrWhiteSpace(cfg.OrderPlacedReplyMode)"""
service = replace_once(service, resolve_marker, resolve_replacement, "ResolveAsync")

fixed_marker = """            var reply = RenderTemplate(cfg.OrderPlacedReplyText, plan);
            if (string.IsNullOrWhiteSpace(reply)) return Fail("下单固定预设答案为空");"""
fixed_replacement = """            var fixedGate = BlockIncompleteTemplate(
                plan,
                cfg.OrderPlacedReplyText,
                "fixed-preset");
            if (fixedGate != null) return fixedGate;

            var reply = RenderTemplate(cfg.OrderPlacedReplyText, plan);
            if (string.IsNullOrWhiteSpace(reply)) return Fail("下单固定预设答案为空");"""
service = replace_once(service, fixed_marker, fixed_replacement, "fixed preset")

fallback_marker = """                var fallback = RenderTemplate(cfg.OrderPlacedReplyText, plan);
                if (!string.IsNullOrWhiteSpace(fallback))"""
fallback_replacement = """                var fallbackGate = BlockIncompleteTemplate(
                    plan,
                    cfg.OrderPlacedReplyText,
                    "http-fallback");
                if (fallbackGate != null) return fallbackGate;

                var fallback = RenderTemplate(cfg.OrderPlacedReplyText, plan);
                if (!string.IsNullOrWhiteSpace(fallback))"""
service = replace_once(service, fallback_marker, fallback_replacement, "HTTP fallback")
service_path.write_text(service, encoding="utf-8-sig")

Path("tests/test_order_template_final_gate_v3_static.py").write_text(
    r'''from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DIRECT = ROOT / "src" / "Bot" / "ChromeNs" / "DirectOrderEventBridge.cs"
V2 = ROOT / "src" / "Bot" / "ChromeNs" / "OrderTemplateRequiredFieldsV2.cs"
SERVICE = ROOT / "src" / "Bot" / "ChromeNs" / "OrderPlacedAutoReplyService.cs"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def test_expanded_notification_plan_is_routed_to_v2_before_direct_send():
    direct = read(DIRECT)
    claim = direct.index("OrderTemplateRequiredFieldsV2.TryOwnExistingPlan(this, plan, source)")
    send = direct.index("await ProcessOrderPlacedReplyAsync(plan)", claim)
    assert claim < send
    assert "展开诊断桥接能够解析嵌套 messageCenterNotify" in direct


def test_v2_accepts_an_already_parsed_plan_and_runs_existing_enrichment():
    source = read(V2)
    method = source.index("internal static bool TryOwnExistingPlan")
    check = source.index("ShouldOwnConfiguredTemplate(plan.Config)", method)
    start = source.index("StartOwnedPlan(qn, plan", check)
    enrich = source.index("TryEnrichFromTradeApiAsync", start)
    assert method < check < start < enrich


def test_final_fixed_template_gate_blocks_missing_sku_quantity_and_paid():
    source = read(SERVICE)
    gate = source.index("internal static List<string> MissingRequiredTemplateFields")
    fixed = source.index('"fixed-preset"', gate)
    render = source.index("var reply = RenderTemplate", fixed)
    assert gate < fixed < render
    assert 'missing.Add("sku")' in source
    assert 'missing.Add("quantity")' in source
    assert 'missing.Add("paid")' in source
    assert "order_template_final_gate blocked_blank_template=true" in source


def test_http_fallback_is_also_guarded_before_rendering():
    source = read(SERVICE)
    gate = source.index('"http-fallback"')
    render = source.index("var fallback = RenderTemplate", gate)
    assert gate < render


def test_missing_snapshot_values_are_not_treated_as_renderable():
    source = read(SERVICE)
    assert 'snapshot == null || string.IsNullOrWhiteSpace(snapshot.SkuText)' in source
    assert 'snapshot == null || snapshot.Quantity <= 0' in source
    assert 'snapshot == null || !snapshot.PaidAmount.HasValue' in source
''',
    encoding="utf-8",
)
