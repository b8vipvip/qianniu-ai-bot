from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]


def read_text(path: Path):
    data = path.read_bytes()
    bom = data.startswith(b"\xef\xbb\xbf")
    text = data.decode("utf-8-sig")
    newline = "\r\n" if "\r\n" in text else "\n"
    return text.replace("\r\n", "\n"), bom, newline


def write_text(path: Path, text: str, bom: bool, newline: str):
    if newline == "\r\n":
        text = text.replace("\n", "\r\n")
    payload = text.encode("utf-8")
    if bom:
        payload = b"\xef\xbb\xbf" + payload
    path.write_bytes(payload)


def replace_once(path: Path, old: str, new: str, label: str):
    text, bom, newline = read_text(path)
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected exactly one match in {path}, got {count}")
    text = text.replace(old, new, 1)
    write_text(path, text, bom, newline)


def patch_deterministic_gate():
    path = ROOT / "src/Bot/ChromeNs/DeterministicAutoReplyService.cs"
    text, bom, newline = read_text(path)
    old = """            var key = Key(item.SellerNick, item.BuyerNick);\n            var gate = BuyerGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));\n            await gate.WaitAsync();\n            try\n            {\n"""
    new = """            var key = Key(item.SellerNick, item.BuyerNick);\n            var gate = BuyerGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));\n            var gateAcquired = await gate.WaitAsync(1800).ConfigureAwait(false);\n            if (!gateAcquired)\n            {\n                // Never let a deterministic rule lock strand the buyer generation in Coalescing.\n                // The outer coordinator already serializes the same buyer; this inner guard is only\n                // a compatibility barrier and must fail open when an earlier send is unhealthy.\n                Log.ErrorWithMaxCount(\n                    \"固定规则内部串行门等待超时，已放行普通消息合并/AI链路: seller=\"\n                    + item.SellerNick + \", buyer=\" + item.BuyerNick + \", waitMs=1800\",\n                    50);\n                return true;\n            }\n            try\n            {\n"""
    if text.count(old) != 1:
        raise RuntimeError("deterministic gate acquisition shape changed")
    text = text.replace(old, new, 1)
    start = text.index("var gateAcquired = await gate.WaitAsync")
    finally_old = """            finally\n            {\n                gate.Release();\n            }\n"""
    finally_pos = text.find(finally_old, start)
    if finally_pos < 0:
        raise RuntimeError("deterministic gate release shape changed")
    finally_new = """            finally\n            {\n                if (gateAcquired) gate.Release();\n            }\n"""
    text = text[:finally_pos] + finally_new + text[finally_pos + len(finally_old):]
    write_text(path, text, bom, newline)


def patch_cdp_runtime_session():
    path = ROOT / "src/Bot/ChromeNs/CDPClient.cs"
    text, bom, newline = read_text(path)
    pattern = re.compile(
        r"\s*var physicalSourceSession = \(ForwardedInboundSourceSession\.Value \?\? string\.Empty\)\.Trim\(\);\n"
        r"\s*if \(physicalSourceSession\.Length == 0\) physicalSourceSession = SessionId;\n"
        r"\s*PreferRuntimeSession\(sellerNick, physicalSourceSession, buyerNick, \"onConversationChange\"\);"
    )
    matches = list(pattern.finditer(text))
    if len(matches) != 1:
        raise RuntimeError(f"CDP forwarded-session promotion shape changed: {len(matches)}")
    replacement = """\n                    // Forwarded duplicate pages are ingress-only. A duplicate page may report a\n                    // valid conversation event, but it must never become the runtime command session.\n                    // Promote only the authoritative handler that actually owns this QN instance.\n                    PreferRuntimeSession(sellerNick, SessionId, buyerNick, \"onConversationChange\");"""
    text = pattern.sub(replacement, text, count=1)
    write_text(path, text, bom, newline)


def patch_raw_inbound_logging():
    path = ROOT / "src/Bot/ChromeNs/QN.cs"
    text, bom, newline = read_text(path)
    old_info = '            Log.Info("收到千牛新消息事件: " + e.Message);'
    if old_info not in text:
        raise RuntimeError("raw inbound info log shape changed")
    text = text.replace(
        old_info,
        '            Log.Info("收到千牛新消息事件: payloadLength=" + ((e == null || e.Message == null) ? 0 : e.Message.Length));',
        1)
    old_error = '                Log.Error("收到新消息但无法解析: " + e.Message);'
    if old_error in text:
        text = text.replace(
            old_error,
            '                Log.Error("收到新消息但无法解析: payloadLength=" + ((e == null || e.Message == null) ? 0 : e.Message.Length));',
            1)
    write_text(path, text, bom, newline)


def patch_order_delivery_state():
    path = ROOT / "src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs"
    text, bom, newline = read_text(path)

    field_old = """            public DateTime Until { get; set; }\n            public bool Delivered { get; set; }\n"""
    field_new = """            public DateTime Until { get; set; }\n            public bool Delivered { get; set; }\n            public bool DeliveryUncertain { get; set; }\n"""
    if text.count(field_old) != 1:
        raise RuntimeError("order action record shape changed")
    text = text.replace(field_old, field_new, 1)

    block_old = """                if (_actionState.Records.Any(x => x.Delivered && SameAction(x, plan)))\n                {\n                    reason = \"action_already_delivered\";\n                    return false;\n                }\n\n                ActiveActions.Add(new OrderReplyActionRecord\n"""
    block_new = """                if (_actionState.Records.Any(x => x.Delivered && SameAction(x, plan)))\n                {\n                    reason = \"action_already_delivered\";\n                    return false;\n                }\n                if (_actionState.Records.Any(x => x.DeliveryUncertain && SameAction(x, plan)))\n                {\n                    // A send action was physically triggered but live echo and remote verification\n                    // were both unavailable. Never blind-resend on another Created/Paid ingress.\n                    reason = \"action_delivery_uncertain\";\n                    return false;\n                }\n\n                ActiveActions.Add(new OrderReplyActionRecord\n"""
    if text.count(block_old) != 1:
        raise RuntimeError("TryBeginExecution delivered guard shape changed")
    text = text.replace(block_old, block_new, 1)

    finish_anchor = """        internal static void FinishExecution(OrderPlacedReplyPlan plan, bool delivered, int sentSegments)\n"""
    if text.count(finish_anchor) != 1:
        raise RuntimeError("FinishExecution anchor changed")
    uncertain_method = """        internal static void MarkDeliveryUncertain(OrderPlacedReplyPlan plan, string reason)\n        {\n            if (plan == null) return;\n            lock (ActionSync)\n            {\n                EnsureActionStateLoadedLocked();\n                var existing = _actionState.Records.FirstOrDefault(x => x != null && SameAction(x, plan));\n                if (existing == null)\n                {\n                    existing = new OrderReplyActionRecord();\n                    _actionState.Records.Add(existing);\n                }\n                existing.Seller = Normalize(plan.Seller);\n                existing.Buyer = Normalize(plan.Buyer);\n                existing.OrderId = (plan.OrderId ?? string.Empty).Trim();\n                existing.FollowUp = plan.IsBuyerFollowUp;\n                existing.Until = DateTime.Now.AddMinutes(10);\n                existing.Delivered = false;\n                existing.DeliveryUncertain = true;\n                SaveActionStateLocked();\n            }\n            Log.ErrorWithMaxCount(\n                \"订单发送状态不确定，10分钟内禁止自动重发以避免重复: seller=\" + plan.Seller\n                + \", buyer=\" + plan.Buyer + \", orderId=\" + plan.OrderId\n                + \", reason=\" + (reason ?? string.Empty),\n                20);\n        }\n\n"""
    text = text.replace(finish_anchor, uncertain_method + finish_anchor, 1)

    delivered_assign = """                    existing.Delivered = delivered || sentSegments > 0;\n"""
    if text.count(delivered_assign) != 1:
        raise RuntimeError("FinishExecution delivered assignment shape changed")
    text = text.replace(
        delivered_assign,
        delivered_assign + "                    existing.DeliveryUncertain = false;\n",
        1)

    method_pattern = re.compile(
        r"        private async Task<bool> SendMandatoryOrderTextAsync\(OrderPlacedReplyPlan plan, string text\)\n"
        r"        \{.*?\n        \}\n\n        private async Task<OrderPresetSendResult>",
        re.S,
    )
    match = method_pattern.search(text)
    if not match:
        raise RuntimeError("SendMandatoryOrderTextAsync method shape changed")
    replacement_method = """        private async Task<bool> SendMandatoryOrderTextAsync(OrderPlacedReplyPlan plan, string text)\n        {\n            if (plan == null || string.IsNullOrWhiteSpace(text)) return false;\n            for (var attempt = 0; attempt < 2; attempt++)\n            {\n                // The generic chat path intentionally yields to a human agent. Configured order\n                // business rules are different: once a Created/Paid event has reserved a plan,\n                // manual replies must never consume or cancel this configured message.\n                var sendStartedAt = DateTime.Now;\n                KnowledgeLearningService.AllowNextManualSend(plan.Seller, plan.Buyer, text);\n                var sent = await SendTextWithRetryAsync(plan.Buyer, text, 0);\n                if (sent) return true;\n\n                // Live seller echo can be lost when the authoritative CDP page reconnects. Before\n                // retrying a mandatory order message, query the verified buyer conversation history.\n                // This prevents a false-negative live echo from becoming a duplicate customer send.\n                var remote = await VerifySellerEchoInRemoteHistoryAsync(\n                    plan.Seller,\n                    plan.Buyer,\n                    text,\n                    sendStartedAt).ConfigureAwait(false);\n                if (remote == RemoteSellerEchoVerification.Delivered)\n                {\n                    Log.Info(\"订单发送已由远端历史二次确认，取消自动重试: seller=\" + plan.Seller\n                        + \", buyer=\" + plan.Buyer + \", orderId=\" + plan.OrderId);\n                    return true;\n                }\n                if (remote == RemoteSellerEchoVerification.Unavailable)\n                {\n                    OrderPlacedAutoReplyService.MarkDeliveryUncertain(\n                        plan,\n                        \"live_echo_missing_and_remote_history_unavailable\");\n                    return false;\n                }\n\n                if (attempt == 0)\n                {\n                    Log.Info(\"强制订单规则发送失败且远端历史确认未送达，准备单次安全重试: seller=\"\n                        + plan.Seller + \", buyer=\" + plan.Buyer + \", orderId=\" + plan.OrderId\n                        + \", attempt=1\");\n                    await Task.Delay(180).ConfigureAwait(false);\n                }\n            }\n            return false;\n        }\n\n        private async Task<OrderPresetSendResult>"""
    text = text[:match.start()] + replacement_method + text[match.end():]
    write_text(path, text, bom, newline)


def create_delivery_verifier():
    path = ROOT / "src/Bot/ChromeNs/QN.DeliveryVerification.cs"
    if path.exists():
        raise RuntimeError("delivery verification file already exists")
    content = r'''using Bot.ChatRecord;
using BotLib;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal enum RemoteSellerEchoVerification
    {
        Delivered = 1,
        Absent = 2,
        Unavailable = 3
    }

    public partial class QN
    {
        internal async Task<RemoteSellerEchoVerification> VerifySellerEchoInRemoteHistoryAsync(
            string seller,
            string buyer,
            string expectedText,
            DateTime notBefore)
        {
            seller = (seller ?? string.Empty).Trim();
            buyer = BuyerIdentityAliasService.ResolveInternalNick(seller, buyer);
            expectedText = NormalizeDeliveryText(expectedText);
            if (seller.Length == 0 || buyer.Length == 0 || expectedText.Length == 0 || cdp == null)
                return RemoteSellerEchoVerification.Unavailable;

            try
            {
                var response = await GetCurrentConversationID().ConfigureAwait(false);
                var current = response == null ? null : response.Result;
                if (current == null
                    || !BuyerIdentityAliasService.AreEquivalent(seller, current.Nick, buyer)
                    || string.IsNullOrWhiteSpace(current.Ccode))
                {
                    Log.Info("订单送达远端核验不可用：当前会话不是目标买家。seller=" + seller
                        + ", buyer=" + buyer);
                    return RemoteSellerEchoVerification.Unavailable;
                }

                var ccode = current.Ccode.Trim();
                var history = await cdp.Invoke<JObject>("im.singlemsg.GetRemoteHisMsg", new
                {
                    cid = new { ccode = ccode, type = 1 },
                    count = 30,
                    gohistory = 1,
                    msgid = "-1",
                    msgtime = "-1"
                }).ConfigureAwait(false);
                if (history == null)
                    return RemoteSellerEchoVerification.Unavailable;

                var messages = history["result"]?["msgs"]?.ToObject<List<QNChatMessage>>()
                    ?? new List<QNChatMessage>();
                var threshold = notBefore.AddSeconds(-4).Ticks;
                foreach (var message in messages.Where(x => x != null))
                {
                    if (message.fromid == null || !EquivalentSellerNick(message.fromid.nick, seller)) continue;
                    var sort = IncomingMessageSafety.GetSortValue(message);
                    if (sort > 0 && sort < threshold) continue;
                    var actual = NormalizeDeliveryText(ExtractDeliveryText(message));
                    if (actual.Length > 0 && string.Equals(actual, expectedText, StringComparison.Ordinal))
                        return RemoteSellerEchoVerification.Delivered;
                }
                return RemoteSellerEchoVerification.Absent;
            }
            catch (Exception ex)
            {
                Log.Info("订单送达远端核验失败，禁止盲目重发: seller=" + seller
                    + ", buyer=" + buyer + ", error=" + SafeDeliveryError(ex.Message));
                return RemoteSellerEchoVerification.Unavailable;
            }
        }

        private static string ExtractDeliveryText(QNChatMessage message)
        {
            if (message == null) return string.Empty;
            if (message.originalData != null && !string.IsNullOrWhiteSpace(message.originalData.text))
                return message.originalData.text;
            return message.summary ?? string.Empty;
        }

        private static bool EquivalentSellerNick(string candidate, string seller)
        {
            return string.Equals(NormalizeTaobaoIdentity(candidate), NormalizeTaobaoIdentity(seller), StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeTaobaoIdentity(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.StartsWith("cntaobao", StringComparison.OrdinalIgnoreCase))
                value = value.Substring("cntaobao".Length);
            return value;
        }

        private static string NormalizeDeliveryText(string value)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            value = Regex.Replace(value, @"\s+", " ");
            value = Regex.Replace(value, @"\s*\[A\]\s*$", string.Empty, RegexOptions.IgnoreCase);
            return value.Trim();
        }

        private static string SafeDeliveryError(string value)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= 180 ? value : value.Substring(0, 180) + "...";
        }
    }
}
'''
    path.write_text(content, encoding="utf-8")


def patch_recovered_media_identity():
    path = ROOT / "src/Bot/ChromeNs/QN.MessageRecovery.cs"
    text, bom, newline = read_text(path)
    pattern = re.compile(
        r"IsBuyerMessage\((?P<var>[A-Za-z_][A-Za-z0-9_]*)\)\s*&&\s*"
        r"(?P=var)\.fromid != null\s*&&\s*"
        r"BuyerIdentityAliasService\.AreEquivalent\(seller,\s*(?P=var)\.fromid\.nick,\s*buyer\)"
    )
    count = len(pattern.findall(text))
    if count < 2:
        raise RuntimeError(f"recovered buyer identity filter shape changed: {count}")
    text = pattern.sub(lambda m: f"IsRecoveredBuyerMessageForTarget({m.group('var')}, seller, buyer)", text)

    helper = r'''
        private static bool IsRecoveredBuyerMessageForTarget(QNChatMessage message, string seller, string buyer)
        {
            if (message == null || message.fromid == null) return false;
            // Remote history is fetched only after the target conversation itself has been verified.
            // Therefore the sender identity is the authoritative buyer discriminator. Do not require
            // toid.nick to equal one exact seller alias: media/history cards can target the main seller
            // account while the active QN runtime is a dispatched sub-account.
            return BuyerIdentityAliasService.AreEquivalent(seller, message.fromid.nick, buyer);
        }
'''
    anchor = "\n    }\n}\n"
    pos = text.rfind(anchor)
    if pos < 0:
        raise RuntimeError("QN.MessageRecovery class closing anchor not found")
    text = text[:pos] + helper + text[pos:]
    write_text(path, text, bom, newline)


def patch_manual_comparison_cancellation():
    matches = []
    for path in (ROOT / "src").rglob("*.cs"):
        try:
            text, bom, newline = read_text(path)
        except Exception:
            continue
        if "人工答案对比学习失败" in text:
            matches.append((path, text, bom, newline))
    if not matches:
        raise RuntimeError("manual comparison failure log source not found")

    patched = 0
    for path, text, bom, newline in matches:
        marker = "人工答案对比学习失败"
        marker_pos = text.find(marker)
        while marker_pos >= 0:
            catch_pos = text.rfind("catch (Exception ex)", max(0, marker_pos - 1200), marker_pos)
            if catch_pos >= 0:
                previous = text[max(0, catch_pos - 300):catch_pos]
                if "catch (OperationCanceledException)" not in previous:
                    line_start = text.rfind("\n", 0, catch_pos) + 1
                    indent = text[line_start:catch_pos]
                    if indent.strip():
                        indent = re.match(r"[ \t]*", indent).group(0)
                    cancellation = (
                        f"{indent}catch (OperationCanceledException)\n"
                        f"{indent}{{\n"
                        f"{indent}    Log.Info(\"人工答案对比学习任务已取消：后台请求或会话生命周期已结束，不作为Bot运行故障。\");\n"
                        f"{indent}}}\n"
                    )
                    text = text[:line_start] + cancellation + text[line_start:]
                    marker_pos += len(cancellation)
                    patched += 1
            marker_pos = text.find(marker, marker_pos + len(marker))
        write_text(path, text, bom, newline)
    if patched < 1:
        raise RuntimeError("manual comparison cancellation catch was not patched")


def add_static_tests():
    path = ROOT / "tests/test_runtime_stability_1077_static.py"
    content = r'''from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(rel):
    return (ROOT / rel).read_text(encoding="utf-8-sig")


def test_deterministic_rule_gate_is_bounded_and_fail_open():
    s = read("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")
    assert "await gate.WaitAsync();" not in s
    assert "gate.WaitAsync(1800)" in s
    assert "固定规则内部串行门等待超时，已放行普通消息合并/AI链路" in s


def test_forwarded_duplicate_session_cannot_be_promoted_for_commands():
    s = read("src/Bot/ChromeNs/CDPClient.cs")
    assert 'PreferRuntimeSession(sellerNick, physicalSourceSession, buyerNick, "onConversationChange")' not in s
    assert 'PreferRuntimeSession(sellerNick, SessionId, buyerNick, "onConversationChange")' in s


def test_order_retry_requires_remote_delivery_verification():
    order = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    verifier = read("src/Bot/ChromeNs/QN.DeliveryVerification.cs")
    assert "VerifySellerEchoInRemoteHistoryAsync" in order
    assert "RemoteSellerEchoVerification.Unavailable" in order
    assert "MarkDeliveryUncertain" in order
    assert "action_delivery_uncertain" in order
    assert '"im.singlemsg.GetRemoteHisMsg"' in verifier
    assert "RemoteSellerEchoVerification.Delivered" in verifier


def test_recovered_buyer_media_uses_verified_conversation_plus_buyer_alias():
    s = read("src/Bot/ChromeNs/QN.MessageRecovery.cs")
    assert "IsRecoveredBuyerMessageForTarget" in s
    assert "BuyerIdentityAliasService.AreEquivalent(seller, message.fromid.nick, buyer)" in s


def test_raw_receive_payload_is_not_logged():
    s = read("src/Bot/ChromeNs/QN.cs")
    assert 'Log.Info("收到千牛新消息事件: " + e.Message)' not in s
    assert 'Log.Error("收到新消息但无法解析: " + e.Message)' not in s
    assert "收到千牛新消息事件: payloadLength=" in s


def test_manual_comparison_cancellation_is_not_generic_failure():
    files = list((ROOT / "src").rglob("*.cs"))
    sources = [p.read_text(encoding="utf-8-sig") for p in files]
    assert any("人工答案对比学习任务已取消" in s and "catch (OperationCanceledException)" in s for s in sources)
'''
    path.write_text(content, encoding="utf-8")


if __name__ == "__main__":
    patch_deterministic_gate()
    patch_cdp_runtime_session()
    patch_raw_inbound_logging()
    patch_order_delivery_state()
    create_delivery_verifier()
    patch_recovered_media_identity()
    patch_manual_comparison_cancellation()
    add_static_tests()
    print("runtime stability 1077 patch applied")
