from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    if old not in text:
        raise RuntimeError(f"patch marker not found: {label}")
    return text.replace(old, new, 1)


v2_path = Path("src/Bot/ChromeNs/OrderTemplateRequiredFieldsV2.cs")
service_path = Path("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
test_path = Path("tests/test_order_template_final_gate_v3_static.py")

v2 = v2_path.read_text(encoding="utf-8-sig")
v2 = replace_once(
    v2,
    """    /// 统一接管需要订单字段的下单模板。字段未补齐时绝不发送空模板，并释放发送占位，
    /// 让后续付款事件有机会再次查询。新模板统一使用 {sku}，旧 {规格} 只作为兼容别名。""",
    """    /// 统一接管需要订单字段的下单模板。先尽力查询交易详情；部分字段缺失时保留并发送
    /// 已取得的其他字段，只有模板所需动态字段全部缺失时才阻止空壳消息并释放发送占位。
    /// 新模板统一使用 {sku}，旧 {规格} 只作为兼容别名。""",
    "V2 summary",
)
v2 = replace_once(
    v2,
    'Log.Info("订单模板字段完整性 V2 已启动：新占位符={sku}，空字段模板禁止发送。");',
    'Log.Info("订单模板字段完整性 V2 已启动：新占位符={sku}，部分字段保留发送，全部缺失时禁止空壳消息。");',
    "V2 startup log",
)

blocked_marker = """                    blocked = missing.Count > 0 && present.Count == 0;
                    LogProbe(plan, probe, blocked, missing, present, missingReasons, source);"""
blocked_replacement = """                    blocked = missing.Count > 0 && present.Count == 0;
                    if (blocked && HasKnownNonOrderTemplateField(plan.Config, plan))
                    {
                        // 订单号、买家、客服或时间等其他模板字段有值时，也属于可发送的部分结果。
                        blocked = false;
                    }
                    LogProbe(plan, probe, blocked, missing, present, missingReasons, source);"""
v2 = replace_once(v2, blocked_marker, blocked_replacement, "non-order template fields")

present_tail = """            if (template.Contains("{订单状态}") && !string.IsNullOrWhiteSpace(snapshot.TradeStatus)) present.Add("status");
            return present;
        }

        private static List<string> BuildMissingReasons"""
present_tail_replacement = """            if (template.Contains("{订单状态}") && !string.IsNullOrWhiteSpace(snapshot.TradeStatus)) present.Add("status");
            return present;
        }

        private static bool HasKnownNonOrderTemplateField(
            AutoReplyRuleConfig cfg,
            OrderPlacedReplyPlan plan)
        {
            if (cfg == null || plan == null) return false;
            var template = cfg.OrderPlacedReplyText ?? string.Empty;
            return (template.Contains("{客服}") && !string.IsNullOrWhiteSpace(plan.Seller))
                || (template.Contains("{买家}") && !string.IsNullOrWhiteSpace(plan.Buyer))
                || (template.Contains("{订单号}") && !string.IsNullOrWhiteSpace(plan.OrderId))
                || (template.Contains("{时间}") && plan.EventTime != DateTime.MinValue);
        }

        private static List<string> BuildMissingReasons"""
v2 = replace_once(v2, present_tail, present_tail_replacement, "non-order present helper")

reason_block = """                else if (!string.IsNullOrWhiteSpace(probe.Error))
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
                }"""
reason_replacement = """                else if (probe.TradeFound)
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
                else if (!string.IsNullOrWhiteSpace(probe.Error))
                {
                    reason = "trade_query_error_after_" + probe.TradeQueryAttempts + "_attempts";
                }
                else
                {
                    reason = probe.BuyerSearchAttempted && !probe.BuyerSecurityIdFound
                        ? "buyer_security_id_not_found_trade_not_found"
                        : "trade_not_found_after_" + probe.TradeQueryAttempts + "_attempts";
                }"""
v2 = replace_once(v2, reason_block, reason_replacement, "accurate missing reasons")
v2_path.write_text(v2, encoding="utf-8-sig")

service = service_path.read_text(encoding="utf-8-sig")
missing_marker = """            if (template.Contains("{订单号}") && (plan == null || string.IsNullOrWhiteSpace(plan.OrderId))) missing.Add("order_id");
            if ((template.Contains("{sku}") || template.Contains("{规格}"))"""
missing_replacement = """            if (template.Contains("{订单号}") && (plan == null || string.IsNullOrWhiteSpace(plan.OrderId))) missing.Add("order_id");
            if (template.Contains("{时间}") && (plan == null || plan.EventTime == DateTime.MinValue)) missing.Add("event_time");
            if ((template.Contains("{sku}") || template.Contains("{规格}"))"""
service = replace_once(service, missing_marker, missing_replacement, "event time missing")

present_marker = """            if (template.Contains("{订单号}") && plan != null && !string.IsNullOrWhiteSpace(plan.OrderId)) present.Add("order_id");
            if ((template.Contains("{sku}") || template.Contains("{规格}"))"""
present_replacement = """            if (template.Contains("{订单号}") && plan != null && !string.IsNullOrWhiteSpace(plan.OrderId)) present.Add("order_id");
            if (template.Contains("{时间}") && plan != null && plan.EventTime != DateTime.MinValue) present.Add("event_time");
            if ((template.Contains("{sku}") || template.Contains("{规格}"))"""
service = replace_once(service, present_marker, present_replacement, "event time present")

reason_marker = """                        case "order_id": reason = "order_id_empty"; break;
                        case "sku": reason = "snapshot_sku_empty"; break;"""
reason_replacement = """                        case "order_id": reason = "order_id_empty"; break;
                        case "event_time": reason = "event_time_min_value"; break;
                        case "sku": reason = "snapshot_sku_empty"; break;"""
service = replace_once(service, reason_marker, reason_replacement, "event time reason")

snapshot_condition = 'else if (snapshot == null && field != "seller" && field != "buyer" && field != "order_id") reason = "snapshot_null";'
snapshot_replacement = 'else if (snapshot == null && field != "seller" && field != "buyer" && field != "order_id" && field != "event_time") reason = "snapshot_null";'
service = replace_once(service, snapshot_condition, snapshot_replacement, "event time snapshot independence")
service_path.write_text(service, encoding="utf-8-sig")

existing_tests = test_path.read_text(encoding="utf-8")
existing_tests += r'''


def test_other_known_placeholders_are_preserved_when_order_details_are_missing():
    v2 = read(V2)
    service = read(SERVICE)
    assert "HasKnownNonOrderTemplateField(plan.Config, plan)" in v2
    assert 'template.Contains("{订单号}") && !string.IsNullOrWhiteSpace(plan.OrderId)' in v2
    assert 'template.Contains("{时间}") && plan.EventTime != DateTime.MinValue' in v2
    assert 'present.Add("event_time")' in service


def test_trade_found_reason_has_priority_over_an_earlier_transient_error():
    source = read(V2)
    trade_found = source.index("else if (probe.TradeFound)")
    query_error = source.index("trade_query_error_after_", trade_found)
    assert trade_found < query_error
'''
test_path.write_text(existing_tests, encoding="utf-8")
