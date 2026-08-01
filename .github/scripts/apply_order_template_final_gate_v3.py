from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    if old not in text:
        raise RuntimeError(f"patch marker not found: {label}")
    return text.replace(old, new, 1)


direct_path = Path("src/Bot/ChromeNs/DirectOrderEventBridge.cs")
v2_path = Path("src/Bot/ChromeNs/OrderTemplateRequiredFieldsV2.cs")
service_path = Path("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")

# 1. The expanded messageCenterNotify path must not bypass trade-detail enrichment.
direct = direct_path.read_text(encoding="utf-8-sig")
send_marker = "            await ProcessOrderPlacedReplyAsync(plan);"
insert = """            // 展开诊断桥接能够解析嵌套 messageCenterNotify，但不得绕过统一字段补全。
            // 计划已包含准确 seller、buyer 和 orderId，交给 V2 查询交易详情后再发送。
            if (OrderTemplateRequiredFieldsV2.TryOwnExistingPlan(this, plan, source)) return;

            await ProcessOrderPlacedReplyAsync(plan);"""
if direct.count(send_marker) != 1:
    raise RuntimeError(f"unexpected direct send marker count: {direct.count(send_marker)}")
direct_path.write_text(direct.replace(send_marker, insert, 1), encoding="utf-8-sig")

# 2. Let V2 take ownership of a plan that another parser has already built.
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

# Record how the enrichment self-check was performed.
probe_marker = """            public bool TotalFound;
            public string Error;"""
probe_replacement = """            public bool TotalFound;
            public bool BuyerSearchAttempted;
            public int TradeQueryAttempts;
            public string Error;"""
v2 = replace_once(v2, probe_marker, probe_replacement, "EnrichmentProbe diagnostics")

# Missing fields no longer block the useful fields that were successfully recovered.
block_marker = """                    var missing = MissingRequiredFields(plan.Config, snapshot);
                    blocked = missing.Count > 0;
                    LogProbe(plan, probe, blocked, missing, source);

                    if (blocked)
                    {
                        // 释放占位，后续付款通知可重新创建计划并再次查询；绝不发送“订单：”空模板。
                        OrderPlacedAutoReplyService.Complete(plan, false);
                        Log.Info("blocked_blank_template=true, orderId=" + plan.OrderId
                            + ", missing=" + string.Join(",", missing));
                        return;
                    }

                    if (snapshot != null)"""
block_replacement = """                    var missing = MissingRequiredFields(plan.Config, snapshot);
                    var present = PresentRequiredFields(plan.Config, snapshot);
                    var missingReasons = BuildMissingReasons(plan.Config, snapshot, probe);

                    // 部分字段缺失时仍发送已经取得的字段；只有模板要求的订单字段全部缺失时，
                    // 才阻止只剩“订单：”之类的空壳消息。
                    blocked = missing.Count > 0 && present.Count == 0;
                    LogProbe(plan, probe, blocked, missing, present, missingReasons, source);

                    if (blocked)
                    {
                        OrderPlacedAutoReplyService.Complete(plan, false);
                        Log.Info("blocked_blank_template=true, orderId=" + plan.OrderId
                            + ", missing=" + string.Join(",", missing)
                            + ", missing_reason=" + string.Join("|", missingReasons));
                        return;
                    }

                    if (missing.Count > 0)
                    {
                        Log.Info("order_template_partial_send=true, orderId=" + plan.OrderId
                            + ", present=" + string.Join(",", present)
                            + ", missing=" + string.Join(",", missing)
                            + ", missing_reason=" + string.Join("|", missingReasons));
                    }

                    if (snapshot != null)"""
v2 = replace_once(v2, block_marker, block_replacement, "partial field send behavior")

# Count trade-query attempts and buyer-security-id self-checks.
attempt_marker = """            for (var attempt = 0; attempt < delays.Length; attempt++)
            {
                if (delays[attempt] > 0) await Task.Delay(delays[attempt]);
                try
                {
                    var response = await qn.GetBuyerTrades(securityBuyerUid ?? string.Empty, plan.OrderId);"""
attempt_replacement = """            for (var attempt = 0; attempt < delays.Length; attempt++)
            {
                if (delays[attempt] > 0) await Task.Delay(delays[attempt]);
                probe.TradeQueryAttempts++;
                try
                {
                    var response = await qn.GetBuyerTrades(securityBuyerUid ?? string.Empty, plan.OrderId);"""
v2 = replace_once(v2, attempt_marker, attempt_replacement, "trade query attempts")

buyer_marker = """                    if (trade == null && string.IsNullOrWhiteSpace(securityBuyerUid))
                    {
                        securityBuyerUid = await ResolveBuyerSecurityIdAsync(qn, plan.Seller, plan.Buyer);"""
buyer_replacement = """                    if (trade == null && string.IsNullOrWhiteSpace(securityBuyerUid))
                    {
                        probe.BuyerSearchAttempted = true;
                        securityBuyerUid = await ResolveBuyerSecurityIdAsync(qn, plan.Seller, plan.Buyer);"""
v2 = replace_once(v2, buyer_marker, buyer_replacement, "buyer security id self-check")

# Expand the structured diagnostic log and add deterministic per-field reasons.
log_signature = """        private static void LogProbe(
            OrderPlacedReplyPlan plan,
            EnrichmentProbe probe,
            bool blocked,
            IList<string> missing,
            string source)"""
log_signature_replacement = """        private static void LogProbe(
            OrderPlacedReplyPlan plan,
            EnrichmentProbe probe,
            bool blocked,
            IList<string> missing,
            IList<string> present,
            IList<string> missingReasons,
            string source)"""
v2 = replace_once(v2, log_signature, log_signature_replacement, "LogProbe signature")

log_tail = """                + " total_found=" + probe.TotalFound.ToString().ToLowerInvariant()
                + " blocked_blank_template=" + blocked.ToString().ToLowerInvariant()
                + " missing=" + string.Join(",", missing ?? new List<string>())
                + (string.IsNullOrWhiteSpace(probe.Error) ? string.Empty : " error=" + probe.Error));
        }

        private static bool ShouldOwnConfiguredTemplate"""
log_tail_replacement = """                + " total_found=" + probe.TotalFound.ToString().ToLowerInvariant()
                + " buyer_search_attempted=" + probe.BuyerSearchAttempted.ToString().ToLowerInvariant()
                + " trade_query_attempts=" + probe.TradeQueryAttempts
                + " blocked_blank_template=" + blocked.ToString().ToLowerInvariant()
                + " present=" + string.Join(",", present ?? new List<string>())
                + " missing=" + string.Join(",", missing ?? new List<string>())
                + " missing_reason=" + string.Join("|", missingReasons ?? new List<string>())
                + " snapshot_source=" + Safe(plan == null || plan.Snapshot == null ? string.Empty : plan.Snapshot.Source, 100)
                + " event_type=" + (plan == null || plan.Snapshot == null ? string.Empty : plan.Snapshot.EventType.ToString())
                + (string.IsNullOrWhiteSpace(probe.Error) ? string.Empty : " error=" + probe.Error));
        }

        private static List<string> PresentRequiredFields(
            AutoReplyRuleConfig cfg,
            OrderSnapshot snapshot)
        {
            var present = new List<string>();
            if (cfg == null || snapshot == null) return present;
            var template = cfg.OrderPlacedReplyText ?? string.Empty;
            if ((template.Contains("{sku}") || template.Contains("{规格}"))
                && !string.IsNullOrWhiteSpace(snapshot.SkuText)) present.Add("sku");
            if (template.Contains("{数量}") && snapshot.Quantity > 0) present.Add("quantity");
            if (template.Contains("{实付}") && snapshot.PaidAmount.HasValue) present.Add("paid");
            if (template.Contains("{金额}") && snapshot.TotalAmount.HasValue) present.Add("total");
            if (template.Contains("{商品}") && !string.IsNullOrWhiteSpace(snapshot.ItemTitle)) present.Add("item");
            if (template.Contains("{订单状态}") && !string.IsNullOrWhiteSpace(snapshot.TradeStatus)) present.Add("status");
            return present;
        }

        private static List<string> BuildMissingReasons(
            AutoReplyRuleConfig cfg,
            OrderSnapshot snapshot,
            EnrichmentProbe probe)
        {
            var reasons = new List<string>();
            probe = probe ?? new EnrichmentProbe();
            foreach (var field in MissingRequiredFields(cfg, snapshot))
            {
                string reason;
                if (snapshot == null)
                {
                    reason = "snapshot_null";
                }
                else if (!string.IsNullOrWhiteSpace(probe.Error))
                {
                    reason = "trade_query_error";
                }
                else if (!probe.TradeFound)
                {
                    reason = probe.BuyerSearchAttempted && !probe.BuyerSecurityIdFound
                        ? "buyer_security_id_not_found_trade_not_found"
                        : "trade_not_found_after_" + probe.TradeQueryAttempts + "_attempts";
                }
                else
                {
                    switch (field)
                    {
                        case "sku": reason = "trade_found_but_sku_empty"; break;
                        case "quantity": reason = "trade_found_but_quantity_zero"; break;
                        case "paid": reason = "trade_found_but_paid_amount_null"; break;
                        case "total": reason = "trade_found_but_total_amount_null"; break;
                        case "item": reason = "trade_found_but_item_title_empty"; break;
                        case "status": reason = "trade_found_but_status_empty"; break;
                        default: reason = "field_unavailable"; break;
                    }
                }
                reasons.Add(field + ":" + reason);
            }
            return reasons;
        }

        private static bool ShouldOwnConfiguredTemplate"""
v2 = replace_once(v2, log_tail, log_tail_replacement, "expanded enrichment diagnostics")
v2_path.write_text(v2, encoding="utf-8-sig")

# 3. Final rendering is partial-field tolerant and logs every missing field on every path.
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

            var cfg = plan.Config;"""
resolve_replacement = """        private static List<string> MissingTemplateFields(string template, OrderPlacedReplyPlan plan)
        {
            var missing = new List<string>();
            var snapshot = plan == null ? null : plan.Snapshot;
            template = template ?? string.Empty;
            if (template.Contains("{客服}") && (plan == null || string.IsNullOrWhiteSpace(plan.Seller))) missing.Add("seller");
            if (template.Contains("{买家}") && (plan == null || string.IsNullOrWhiteSpace(plan.Buyer))) missing.Add("buyer");
            if (template.Contains("{订单号}") && (plan == null || string.IsNullOrWhiteSpace(plan.OrderId))) missing.Add("order_id");
            if ((template.Contains("{sku}") || template.Contains("{规格}"))
                && (snapshot == null || string.IsNullOrWhiteSpace(snapshot.SkuText))) missing.Add("sku");
            if (template.Contains("{数量}") && (snapshot == null || snapshot.Quantity <= 0)) missing.Add("quantity");
            if (template.Contains("{金额}") && (snapshot == null || !snapshot.TotalAmount.HasValue)) missing.Add("total");
            if (template.Contains("{实付}") && (snapshot == null || !snapshot.PaidAmount.HasValue)) missing.Add("paid");
            if (template.Contains("{商品}") && (snapshot == null || string.IsNullOrWhiteSpace(snapshot.ItemTitle))) missing.Add("item");
            if (template.Contains("{订单状态}") && (snapshot == null || string.IsNullOrWhiteSpace(snapshot.TradeStatus))) missing.Add("status");
            return missing;
        }

        private static List<string> PresentTemplateFields(string template, OrderPlacedReplyPlan plan)
        {
            var present = new List<string>();
            var snapshot = plan == null ? null : plan.Snapshot;
            template = template ?? string.Empty;
            if (template.Contains("{客服}") && plan != null && !string.IsNullOrWhiteSpace(plan.Seller)) present.Add("seller");
            if (template.Contains("{买家}") && plan != null && !string.IsNullOrWhiteSpace(plan.Buyer)) present.Add("buyer");
            if (template.Contains("{订单号}") && plan != null && !string.IsNullOrWhiteSpace(plan.OrderId)) present.Add("order_id");
            if ((template.Contains("{sku}") || template.Contains("{规格}"))
                && snapshot != null && !string.IsNullOrWhiteSpace(snapshot.SkuText)) present.Add("sku");
            if (template.Contains("{数量}") && snapshot != null && snapshot.Quantity > 0) present.Add("quantity");
            if (template.Contains("{金额}") && snapshot != null && snapshot.TotalAmount.HasValue) present.Add("total");
            if (template.Contains("{实付}") && snapshot != null && snapshot.PaidAmount.HasValue) present.Add("paid");
            if (template.Contains("{商品}") && snapshot != null && !string.IsNullOrWhiteSpace(snapshot.ItemTitle)) present.Add("item");
            if (template.Contains("{订单状态}") && snapshot != null && !string.IsNullOrWhiteSpace(snapshot.TradeStatus)) present.Add("status");
            return present;
        }

        private static List<string> BuildRenderMissingReasons(
            IList<string> missing,
            OrderPlacedReplyPlan plan)
        {
            var reasons = new List<string>();
            var snapshot = plan == null ? null : plan.Snapshot;
            foreach (var field in missing ?? new List<string>())
            {
                string reason;
                if (plan == null) reason = "plan_null";
                else if (snapshot == null && field != "seller" && field != "buyer" && field != "order_id") reason = "snapshot_null";
                else
                {
                    switch (field)
                    {
                        case "seller": reason = "seller_empty"; break;
                        case "buyer": reason = "buyer_empty"; break;
                        case "order_id": reason = "order_id_empty"; break;
                        case "sku": reason = "snapshot_sku_empty"; break;
                        case "quantity": reason = "snapshot_quantity_zero"; break;
                        case "total": reason = "snapshot_total_amount_null"; break;
                        case "paid": reason = "snapshot_paid_amount_null"; break;
                        case "item": reason = "snapshot_item_title_empty"; break;
                        case "status": reason = "snapshot_trade_status_empty"; break;
                        default: reason = "field_unavailable"; break;
                    }
                }
                reasons.Add(field + ":" + reason);
            }
            return reasons;
        }

        public static async Task<OrderPlacedReplyResolution> ResolveAsync(OrderPlacedReplyPlan plan)
        {
            if (plan == null || plan.Config == null)
            {
                return Fail("下单自动回复计划为空");
            }

            var cfg = plan.Config;"""
service = replace_once(service, resolve_marker, resolve_replacement, "ResolveAsync diagnostics helpers")

# Label every render source for easier incident tracing.
service = service.replace(
    "var fallback = RenderTemplate(cfg.OrderPlacedReplyText, plan);",
    "var fallback = RenderTemplate(cfg.OrderPlacedReplyText, plan, \"http-fallback\");",
    1,
)
service = service.replace(
    "var reply = RenderTemplate(cfg.OrderPlacedReplyText, plan);",
    "var reply = RenderTemplate(cfg.OrderPlacedReplyText, plan, \"fixed-preset\");",
    1,
)
service = service.replace(
    "Reply = RenderTemplate(reply, plan),",
    "Reply = RenderTemplate(reply, plan, \"http-response\"),",
    1,
)

render_marker = """        private static string RenderTemplate(string template, OrderPlacedReplyPlan plan)
        {
            var snapshot = plan == null ? null : plan.Snapshot;
            return (template ?? string.Empty)
                .Replace("{客服}", plan == null ? string.Empty : plan.Seller ?? string.Empty)
                .Replace("{买家}", plan == null ? string.Empty : plan.Buyer ?? string.Empty)
                .Replace("{订单号}", plan == null ? string.Empty : plan.OrderId ?? string.Empty)
                .Replace("{时间}", plan == null ? string.Empty : plan.EventTime.ToString("yyyy-MM-dd HH:mm:ss"))
                .Replace("{商品}", snapshot == null ? string.Empty : snapshot.ItemTitle ?? string.Empty)
                .Replace("{sku}", snapshot == null ? string.Empty : snapshot.SkuText ?? string.Empty)
                .Replace("{规格}", snapshot == null ? string.Empty : snapshot.SkuText ?? string.Empty)
                .Replace("{数量}", snapshot == null || snapshot.Quantity <= 0 ? string.Empty : snapshot.Quantity.ToString())
                .Replace("{金额}", snapshot == null || !snapshot.TotalAmount.HasValue ? string.Empty : snapshot.TotalAmount.Value.ToString("0.00"))
                .Replace("{实付}", snapshot == null || !snapshot.PaidAmount.HasValue ? string.Empty : snapshot.PaidAmount.Value.ToString("0.00"))
                .Replace("{订单状态}", snapshot == null ? string.Empty : snapshot.TradeStatus ?? string.Empty)
                .Trim();
        }"""
render_replacement = """        private static string RenderTemplate(
            string template,
            OrderPlacedReplyPlan plan,
            string source)
        {
            var snapshot = plan == null ? null : plan.Snapshot;
            var missing = MissingTemplateFields(template, plan);
            var present = PresentTemplateFields(template, plan);
            var missingReasons = BuildRenderMissingReasons(missing, plan);

            var rendered = (template ?? string.Empty)
                .Replace("{客服}", plan == null ? string.Empty : plan.Seller ?? string.Empty)
                .Replace("{买家}", plan == null ? string.Empty : plan.Buyer ?? string.Empty)
                .Replace("{订单号}", plan == null ? string.Empty : plan.OrderId ?? string.Empty)
                .Replace("{时间}", plan == null ? string.Empty : plan.EventTime.ToString("yyyy-MM-dd HH:mm:ss"))
                .Replace("{商品}", snapshot == null ? string.Empty : snapshot.ItemTitle ?? string.Empty)
                .Replace("{sku}", snapshot == null ? string.Empty : snapshot.SkuText ?? string.Empty)
                .Replace("{规格}", snapshot == null ? string.Empty : snapshot.SkuText ?? string.Empty)
                .Replace("{数量}", snapshot == null || snapshot.Quantity <= 0 ? string.Empty : snapshot.Quantity.ToString())
                .Replace("{金额}", snapshot == null || !snapshot.TotalAmount.HasValue ? string.Empty : snapshot.TotalAmount.Value.ToString("0.00"))
                .Replace("{实付}", snapshot == null || !snapshot.PaidAmount.HasValue ? string.Empty : snapshot.PaidAmount.Value.ToString("0.00"))
                .Replace("{订单状态}", snapshot == null ? string.Empty : snapshot.TradeStatus ?? string.Empty);

            // 保留已有字段，同时清理缺失占位符造成的双空格、冒号后空格和行尾空白。
            rendered = Regex.Replace(rendered, @"[ \\t]{2,}", " ");
            rendered = Regex.Replace(rendered, @"([：:])\\s+", "$1");
            rendered = Regex.Replace(rendered, @"\\s+([，。；、])", "$1");
            rendered = Regex.Replace(rendered, @"(?m)[ \\t]+$", string.Empty).Trim();

            var allRequestedFieldsMissing = missing.Count > 0 && present.Count == 0;
            Log.Info("order_template_render"
                + " source=" + source
                + " orderId=" + (plan == null ? string.Empty : plan.OrderId)
                + " partial=" + (missing.Count > 0 && present.Count > 0).ToString().ToLowerInvariant()
                + " all_requested_fields_missing=" + allRequestedFieldsMissing.ToString().ToLowerInvariant()
                + " present=" + string.Join(",", present)
                + " missing=" + string.Join(",", missing)
                + " missing_reason=" + string.Join("|", missingReasons)
                + " snapshot_source=" + Short(snapshot == null ? string.Empty : snapshot.Source, 100)
                + " rendered_length=" + rendered.Length);

            // 若模板引用的所有动态字段都缺失，禁止发送仅剩静态标点的空壳消息。
            return allRequestedFieldsMissing ? string.Empty : rendered;
        }"""
service = replace_once(service, render_marker, render_replacement, "partial RenderTemplate")
service_path.write_text(service, encoding="utf-8-sig")

# 4. Regression tests mirror the real incident and the requested partial-field behavior.
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


def test_v2_accepts_an_already_parsed_plan_and_runs_trade_enrichment():
    source = read(V2)
    method = source.index("internal static bool TryOwnExistingPlan")
    start = source.index("StartOwnedPlan(qn, plan", method)
    enrich = source.index("TryEnrichFromTradeApiAsync", start)
    assert method < start < enrich


def test_partial_fields_are_sent_instead_of_blocking_every_missing_field():
    source = read(V2)
    assert "blocked = missing.Count > 0 && present.Count == 0" in source
    assert "order_template_partial_send=true" in source
    assert "PresentRequiredFields" in source


def test_missing_field_causes_and_self_check_are_structured_in_logs():
    source = read(V2)
    assert "trade_query_attempts=" in source
    assert "buyer_search_attempted=" in source
    assert "missing_reason=" in source
    assert "trade_found_but_sku_empty" in source
    assert "trade_not_found_after_" in source


def test_final_renderer_keeps_present_fields_and_cleans_spacing():
    source = read(SERVICE)
    assert 'RenderTemplate(cfg.OrderPlacedReplyText, plan, "fixed-preset")' in source
    assert "partial=" in source
    assert "all_requested_fields_missing=" in source
    assert 'Regex.Replace(rendered, @"[ \\t]{2,}", " ")' in source
    assert 'Regex.Replace(rendered, @"([：:])\\s+", "$1")' in source


def test_only_all_missing_dynamic_fields_create_an_empty_shell_block():
    source = read(SERVICE)
    assert "allRequestedFieldsMissing = missing.Count > 0 && present.Count == 0" in source
    assert "return allRequestedFieldsMissing ? string.Empty : rendered" in source


def test_http_response_and_fallback_have_the_same_diagnostics():
    source = read(SERVICE)
    assert 'RenderTemplate(reply, plan, "http-response")' in source
    assert 'RenderTemplate(cfg.OrderPlacedReplyText, plan, "http-fallback")' in source
''',
    encoding="utf-8",
)
