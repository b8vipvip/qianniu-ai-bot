from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ORDER = ROOT / "src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs"
TEST = ROOT / "tests/test_runtime_stability_1077_static.py"


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected exactly one match, got {count}")
    return text.replace(old, new, 1)


s = ORDER.read_text(encoding="utf-8-sig")

s = replace_once(
    s,
    '''                ActiveActions.Add(new OrderReplyActionRecord\n                {\n                    Seller = Normalize(plan.Seller),\n                    Buyer = Normalize(plan.Buyer),\n                    OrderId = plan.OrderId.Trim(),''',
    '''                ActiveActions.Add(new OrderReplyActionRecord\n                {\n                    Seller = Normalize(plan.Seller),\n                    Buyer = NormalizeBuyer(plan.Seller, plan.Buyer),\n                    OrderId = plan.OrderId.Trim(),''',
    "active action buyer identity")

s = replace_once(
    s,
    '''                existing.Seller = Normalize(plan.Seller);\n                existing.Buyer = Normalize(plan.Buyer);\n                existing.OrderId = (plan.OrderId ?? string.Empty).Trim();''',
    '''                existing.Seller = Normalize(plan.Seller);\n                existing.Buyer = NormalizeBuyer(plan.Seller, plan.Buyer);\n                existing.OrderId = (plan.OrderId ?? string.Empty).Trim();''',
    "uncertain action buyer identity")

s = replace_once(
    s,
    '''                    existing.Seller = Normalize(plan.Seller);\n                    existing.Buyer = Normalize(plan.Buyer);\n                    existing.OrderId = plan.OrderId.Trim();''',
    '''                    existing.Seller = Normalize(plan.Seller);\n                    existing.Buyer = NormalizeBuyer(plan.Seller, plan.Buyer);\n                    existing.OrderId = plan.OrderId.Trim();''',
    "completed action buyer identity")

s = replace_once(
    s,
    '''                    && Normalize(x.Seller) == Normalize(seller)\n                    && Normalize(x.Buyer) == Normalize(buyer)\n                    && string.Equals(x.OrderId, orderId, StringComparison.Ordinal));''',
    '''                    && Normalize(x.Seller) == Normalize(seller)\n                    && NormalizeBuyer(x.Seller, x.Buyer) == NormalizeBuyer(seller, buyer)\n                    && string.Equals(x.OrderId, orderId, StringComparison.Ordinal));''',
    "canonical observation buyer alias")

s = replace_once(
    s,
    '''                        Seller = Normalize(seller),\n                        Buyer = Normalize(buyer),\n                        OrderId = orderId,''',
    '''                        Seller = Normalize(seller),\n                        Buyer = NormalizeBuyer(seller, buyer),\n                        OrderId = orderId,''',
    "canonical record buyer identity")

s = replace_once(
    s,
    '''            var normalizedSeller = Normalize(seller);\n            var normalizedBuyer = Normalize(buyer);\n            orderId = (orderId ?? string.Empty).Trim();\n            var candidates = ActiveActions.Concat(_actionState == null ? new List<OrderReplyActionRecord>() : _actionState.Records)\n                .Where(x => x != null\n                    && Normalize(x.Seller) == normalizedSeller\n                    && Normalize(x.Buyer) == normalizedBuyer''',
    '''            var normalizedSeller = Normalize(seller);\n            var normalizedBuyer = NormalizeBuyer(seller, buyer);\n            orderId = (orderId ?? string.Empty).Trim();\n            var candidates = ActiveActions.Concat(_actionState == null ? new List<OrderReplyActionRecord>() : _actionState.Records)\n                .Where(x => x != null\n                    && Normalize(x.Seller) == normalizedSeller\n                    && NormalizeBuyer(x.Seller, x.Buyer) == normalizedBuyer''',
    "canonical lookup buyer alias")

s = replace_once(
    s,
    '''            return record.FollowUp == plan.IsBuyerFollowUp\n                && Normalize(record.Seller) == Normalize(plan.Seller)\n                && Normalize(record.Buyer) == Normalize(plan.Buyer)\n                && (string.Equals((record.OrderId ?? string.Empty).Trim(), (plan.OrderId ?? string.Empty).Trim(), StringComparison.Ordinal)''',
    '''            return record.FollowUp == plan.IsBuyerFollowUp\n                && Normalize(record.Seller) == Normalize(plan.Seller)\n                && NormalizeBuyer(record.Seller, record.Buyer) == NormalizeBuyer(plan.Seller, plan.Buyer)\n                && (string.Equals((record.OrderId ?? string.Empty).Trim(), (plan.OrderId ?? string.Empty).Trim(), StringComparison.Ordinal)''',
    "same action buyer alias")

s = replace_once(
    s,
    '''        private static string BuildReservationKey(string seller, string buyer, string orderId, bool followUp)\n        {\n            return Normalize(seller) + "#" + Normalize(buyer) + "#" + (orderId ?? string.Empty).Trim()\n                + (followUp ? "#guidance-followup" : string.Empty);\n        }''',
    '''        private static string BuildReservationKey(string seller, string buyer, string orderId, bool followUp)\n        {\n            return Normalize(seller) + "#" + NormalizeBuyer(seller, buyer) + "#" + (orderId ?? string.Empty).Trim()\n                + (followUp ? "#guidance-followup" : string.Empty);\n        }''',
    "reservation buyer alias")

s = replace_once(
    s,
    '''        private static OrderPlacedReplyResolution Fail(string error) { return new OrderPlacedReplyResolution { Success = false, Error = Short(error, 500) }; }\n        private static string Normalize(string value) { return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"\\s+", string.Empty); }\n        private static string Short(string value, int max)''',
    '''        private static OrderPlacedReplyResolution Fail(string error) { return new OrderPlacedReplyResolution { Success = false, Error = Short(error, 500) }; }\n        private static string Normalize(string value) { return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"\\s+", string.Empty); }\n        private static string NormalizeBuyer(string seller, string buyer)\n        {\n            var canonical = BuyerIdentityAliasService.ResolveInternalNick(\n                (seller ?? string.Empty).Trim(),\n                (buyer ?? string.Empty).Trim());\n            return Normalize(string.IsNullOrWhiteSpace(canonical) ? buyer : canonical);\n        }\n        private static string Short(string value, int max)''',
    "normalize buyer helper")

s = replace_once(
    s,
    '''                    OrderPlacedAutoReplyService.Complete(\n                        plan,\n                        !string.Equals(actionReason, "precision_risk_order_id", StringComparison.Ordinal));\n                    Log.Info("下单自动回复动作级幂等已阻止重复执行: seller=" + plan.Seller''',
    '''                    // Only a durably delivered action may extend the normal long reservation.\n                    // In-flight/precision-risk/uncertain outcomes are not delivery success. In\n                    // particular, delivery-uncertain has its own 10-minute durable safety window;\n                    // converting it to Complete(true) here would suppress a legitimate retry for\n                    // the full order dedup period (often 24h).\n                    if (string.Equals(actionReason, "action_already_delivered", StringComparison.Ordinal))\n                    {\n                        OrderPlacedAutoReplyService.Complete(plan, true);\n                    }\n                    else if (!string.Equals(actionReason, "action_inflight", StringComparison.Ordinal))\n                    {\n                        OrderPlacedAutoReplyService.Complete(plan, false);\n                    }\n                    Log.Info("下单自动回复动作级幂等已阻止重复执行: seller=" + plan.Seller''',
    "blocked action reservation semantics")

ORDER.write_text(s, encoding="utf-8")

t = TEST.read_text(encoding="utf-8-sig")
extra = r'''


def test_order_delivery_uncertain_does_not_become_long_delivered_reservation():
    s = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    assert 'string.Equals(actionReason, "action_already_delivered", StringComparison.Ordinal)' in s
    assert 'OrderPlacedAutoReplyService.Complete(plan, true);' in s
    assert 'else if (!string.Equals(actionReason, "action_inflight", StringComparison.Ordinal))' in s
    assert '!string.Equals(actionReason, "precision_risk_order_id", StringComparison.Ordinal)' not in s


def test_order_action_identity_canonicalizes_buyer_aliases():
    s = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    assert "private static string NormalizeBuyer(string seller, string buyer)" in s
    assert "BuyerIdentityAliasService.ResolveInternalNick" in s
    assert 'NormalizeBuyer(record.Seller, record.Buyer) == NormalizeBuyer(plan.Seller, plan.Buyer)' in s
    assert 'Normalize(seller) + "#" + NormalizeBuyer(seller, buyer)' in s
'''
if "test_order_delivery_uncertain_does_not_become_long_delivered_reservation" in t:
    raise RuntimeError("follow-up tests already present")
TEST.write_text(t.rstrip() + extra + "\n", encoding="utf-8")
print("runtime stability 1077 follow-up patch applied")
