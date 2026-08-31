from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DIRECT = ROOT / "src/Bot/ChromeNs/DirectOrderEventBridge.cs"
ORDER = ROOT / "src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs"
TEST = ROOT / "tests/test_runtime_stability_1077_static.py"


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected exactly one match, got {count}")
    return text.replace(old, new, 1)


d = DIRECT.read_text(encoding="utf-8-sig")

d = replace_once(
    d,
    '''        private static readonly Regex OrderIdTextRegex = new Regex(\n            @"(?:订单号|订单编号|主订单号|子订单号|交易号|订单)\\s*[:：#]?\\s*(\\d{8,})",\n            RegexOptions.Compiled | RegexOptions.IgnoreCase);''',
    '''        private static readonly Regex OrderIdTextRegex = new Regex(\n            @"(?:订单号|订单编号|主订单号|子订单号|交易号|订单)\\s*[:：#]?\\s*(\\d{8,})",\n            RegexOptions.Compiled | RegexOptions.IgnoreCase);\n        // Never rely on a JToken/double round-trip for order identifiers. Taobao order IDs are\n        // commonly above JavaScript's 2^53 exact-integer boundary, so preserve the literal digits\n        // from the raw WebSocket payload before any JSON object conversion can round them.\n        private static readonly Regex RawOrderIdKeyRegex = new Regex(\n            @"[""']?(?:orderid|bizorderid|mainorderid|suborderid|tradeid|biztradeid|tid)[""']?\\s*[:=]\\s*[""']?(?<id>\\d{8,40})[""']?",\n            RegexOptions.Compiled | RegexOptions.IgnoreCase);''',
    "raw order id regex")

d = replace_once(
    d,
    '''                var messages = chat == null || chat.result == null\n                    ? new List<QNChatMessage>()\n                    : chat.result.Where(x => x != null).ToList();\n                foreach (var message in messages)\n                {\n                    if (!MessageLooksPotential(message)) continue;\n                    await qn.ProcessDirectOrderMessageAsync(\n                        message,\n                        DirectOrderIdentityResolver.ResolveSeller(qn, message, null),\n                        DirectOrderIdentityResolver.ResolveBuyer(qn, message, null),\n                        "receiveNewMsg系统订单卡片");\n                }''',
    '''                var messages = chat == null || chat.result == null\n                    ? new List<QNChatMessage>()\n                    : chat.result.Where(x => x != null).ToList();\n                var potential = messages.Where(MessageLooksPotential).ToList();\n                // A receiveNewMsg batch may contain several cards. Only pass a raw exact hint when\n                // both the payload and the business batch identify one unambiguous order.\n                var exactOrderIdHint = potential.Count == 1 ? ExtractExactOrderIdFromRaw(raw) : string.Empty;\n                foreach (var message in potential)\n                {\n                    await qn.ProcessDirectOrderMessageAsync(\n                        message,\n                        DirectOrderIdentityResolver.ResolveSeller(qn, message, null),\n                        DirectOrderIdentityResolver.ResolveBuyer(qn, message, null),\n                        "receiveNewMsg系统订单卡片",\n                        exactOrderIdHint);\n                }''',
    "receive raw exact hint")

d = replace_once(
    d,
    '''            var orderId = DigitsOnly(FindValue(flat, OrderIdKeys), 8, 40);\n            if (string.IsNullOrWhiteSpace(orderId))\n            {\n                var match = OrderIdTextRegex.Match(text);\n                if (match.Success) orderId = match.Groups[1].Value;\n            }''',
    '''            // Prefer the literal raw payload over parsed numeric tokens. This is the only\n            // representation guaranteed to preserve IDs above 2^53 exactly.\n            var orderId = ExtractExactOrderIdFromRaw(raw);\n            if (string.IsNullOrWhiteSpace(orderId))\n                orderId = DigitsOnly(FindValue(flat, OrderIdKeys), 8, 40);\n            if (string.IsNullOrWhiteSpace(orderId))\n            {\n                var match = OrderIdTextRegex.Match(text);\n                if (match.Success) orderId = match.Groups[1].Value;\n            }''',
    "envelope raw exact order id")

d = replace_once(
    d,
    '''        private static string FindIdentity(IList<FlatValue> flat, string seller)''',
    '''        internal static string ExtractExactOrderIdFromRaw(string raw)\n        {\n            raw = raw ?? string.Empty;\n            if (raw.Length == 0) return string.Empty;\n\n            var candidates = new List<string>();\n            foreach (Match match in RawOrderIdKeyRegex.Matches(raw))\n            {\n                var id = match.Groups["id"].Value;\n                if (!string.IsNullOrWhiteSpace(id)) candidates.Add(id);\n            }\n            foreach (Match match in OrderIdTextRegex.Matches(raw))\n            {\n                var id = match.Groups[1].Value;\n                if (!string.IsNullOrWhiteSpace(id)) candidates.Add(id);\n            }\n\n            var distinct = candidates\n                .Where(x => x.Length >= 8 && x.Length <= 40)\n                .Distinct(StringComparer.Ordinal)\n                .ToList();\n            return distinct.Count == 1 ? distinct[0] : string.Empty;\n        }\n\n        private static string FindIdentity(IList<FlatValue> flat, string seller)''',
    "extract exact helper")

d = replace_once(
    d,
    '''        internal async Task ProcessDirectOrderMessageAsync(\n            QNChatMessage message,\n            string sellerHint,\n            string buyerHint,\n            string source)''',
    '''        internal async Task ProcessDirectOrderMessageAsync(\n            QNChatMessage message,\n            string sellerHint,\n            string buyerHint,\n            string source,\n            string exactOrderIdHint = null)''',
    "direct processing optional exact hint")

d = replace_once(
    d,
    '''                buyer,\n                _messageSafetyStartedAt,\n                out plan)) return;''',
    '''                buyer,\n                _messageSafetyStartedAt,\n                out plan,\n                exactOrderIdHint)) return;''',
    "direct plan exact hint")

DIRECT.write_text(d, encoding="utf-8")

o = ORDER.read_text(encoding="utf-8-sig")
o = replace_once(
    o,
    '''            string buyer,\n            DateTime botStartedAt,\n            out OrderPlacedReplyPlan plan)''',
    '''            string buyer,\n            DateTime botStartedAt,\n            out OrderPlacedReplyPlan plan,\n            string exactOrderIdHint = null)''',
    "plan optional exact hint")

o = replace_once(
    o,
    '''            ObserveCanonicalOrderId(seller, buyer, snapshot.OrderId);\n            OrderGuidanceDeliveryGuard.ObserveOrder(snapshot);''',
    '''            var exactOrderId = Regex.Replace(exactOrderIdHint ?? string.Empty, @"\\D", string.Empty);\n            if (exactOrderId.Length >= 8 && exactOrderId.Length <= 40\n                && !string.Equals(snapshot.OrderId, exactOrderId, StringComparison.Ordinal))\n            {\n                // Raw WebSocket digits outrank a parsed numeric token. Do this before publishing the\n                // snapshot/reserving the action so a rounded ghost ID can never become a second order.\n                Log.Info("订单号使用原始载荷精确字符串覆盖解析值: parsedOrderId=" + snapshot.OrderId\n                    + ", exactOrderId=" + exactOrderId);\n                snapshot.OrderId = exactOrderId;\n            }\n            ObserveCanonicalOrderId(seller, buyer, snapshot.OrderId);\n            OrderGuidanceDeliveryGuard.ObserveOrder(snapshot);''',
    "apply exact hint before publish")
ORDER.write_text(o, encoding="utf-8")

t = TEST.read_text(encoding="utf-8-sig")
extra = r'''


def test_raw_order_id_literal_wins_before_json_numeric_rounding():
    direct = read("src/Bot/ChromeNs/DirectOrderEventBridge.cs")
    order = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    assert "RawOrderIdKeyRegex" in direct
    assert "internal static string ExtractExactOrderIdFromRaw(string raw)" in direct
    envelope = direct[direct.index("private static NotificationEnvelope BuildEnvelope"):direct.index("private static List<FlatValue> Flatten")]
    assert envelope.index("ExtractExactOrderIdFromRaw(raw)") < envelope.index("FindValue(flat, OrderIdKeys)")
    assert "string exactOrderIdHint = null" in direct
    assert "string exactOrderIdHint = null" in order
    plan = order[order.index("public static bool TryCreatePlan"):order.index("private static bool TryCreateBuyerFollowUpPlan")]
    assert plan.index("snapshot.OrderId = exactOrderId;") < plan.index("OrderEventHub.Publish(snapshot)")


def test_regression_order_id_above_js_safe_integer_is_kept_as_string_literal():
    direct = read("src/Bot/ChromeNs/DirectOrderEventBridge.cs")
    sample = "5127395078262028714"
    assert "2^53" in direct
    assert "\\d{8,40}" in direct
    # Guard the exact production incident shape: no code should hard-code a rounded replacement.
    assert sample.replace("8714", "8000") not in direct
'''
if "test_raw_order_id_literal_wins_before_json_numeric_rounding" in t:
    raise RuntimeError("batch4 tests already present")
TEST.write_text(t.rstrip() + extra + "\n", encoding="utf-8")
print("runtime stability 1077 batch4 patch applied")
