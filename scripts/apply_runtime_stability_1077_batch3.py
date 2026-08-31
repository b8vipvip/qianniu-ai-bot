from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
QN = ROOT / "src/Bot/ChromeNs/QN.cs"
TEST = ROOT / "tests/test_runtime_stability_1077_static.py"


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected exactly one match, got {count}")
    return text.replace(old, new, 1)


s = QN.read_text(encoding="utf-8-sig")

s = replace_once(
    s,
    '''        private readonly IncomingMessageDeduplicator _incomingMessageDeduplicator = new IncomingMessageDeduplicator(2000);\n        // The transport deduplicator can be consumed by a duplicate CDP page before the authoritative\n        // QN instance reaches the reply queue. Background recovery therefore has a deliberate bypass.\n        // Keep a second ledger for messages that the authoritative business path actually handled so\n        // that bypass never replays a question which already started (or was stopped by) a human reply.\n        private readonly IncomingMessageDeduplicator _handledBuyerMessageDeduplicator =\n            new IncomingMessageDeduplicator(4000);''',
    '''        // Seller echoes may be repeated by several injected pages. Keep their transport-level\n        // dedupe separate from buyer business ownership so a malformed/non-business duplicate page\n        // can never consume a buyer key before the authoritative processing path claims it.\n        private readonly IncomingMessageDeduplicator _incomingMessageDeduplicator = new IncomingMessageDeduplicator(2000);\n        // This is the single side-effect claim for buyer messages. Duplicate CDP pages are forwarded\n        // into the authoritative QN instance, and only the path that reaches this ledger first may\n        // enqueue/order-route the buyer event. Background recovery consults the same ledger.\n        private readonly IncomingMessageDeduplicator _handledBuyerMessageDeduplicator =\n            new IncomingMessageDeduplicator(4000);''',
    "dedupe field comments")

s = replace_once(
    s,
    '''            var messageText = GetMessageText(message);\n            var messageKey = IncomingMessageSafety.BuildMessageKey(message, messageText);\n            if (!_incomingMessageDeduplicator.TryAccept(messageKey))\n            {\n                Log.Info("重复消息已跳过: key=" + messageKey);\n                return Task.CompletedTask;\n            }\n            if (IsSellerMessage(message))\n            {\n                ConversationContextStore.RefreshAndRecord(message, messageText);\n                RecordSellerEcho(message.toid.nick, messageText);\n                return Task.CompletedTask;\n            }\n            if (!IsBuyerMessage(message)) return Task.CompletedTask;\n\n            var sellerNick = message.toid.nick;\n            var buyerNick = message.fromid.nick;\n            var detectedAt = DateTime.Now;\n\n            if (!_handledBuyerMessageDeduplicator.TryAccept(messageKey))\n            {\n                Log.Info("已实际处理的买家消息不再重复入队: key=" + messageKey);\n                return Task.CompletedTask;\n            }''',
    '''            var messageText = GetMessageText(message);\n            var messageKey = IncomingMessageSafety.BuildMessageKey(message, messageText);\n\n            // Classify before consuming any transport dedupe key. A duplicate/partially hydrated CDP\n            // page can emit a frame that is not yet a valid buyer business event; allowing that frame\n            // to reserve the key would make the later authoritative copy disappear. Seller echoes use\n            // the transport ledger, while valid buyer messages use the handled-business ledger below.\n            if (IsSellerMessage(message))\n            {\n                if (!_incomingMessageDeduplicator.TryAccept(messageKey))\n                {\n                    Log.Info("重复卖家回显已跳过: key=" + messageKey);\n                    return Task.CompletedTask;\n                }\n                ConversationContextStore.RefreshAndRecord(message, messageText);\n                RecordSellerEcho(message.toid.nick, messageText);\n                return Task.CompletedTask;\n            }\n            if (!IsBuyerMessage(message)) return Task.CompletedTask;\n\n            var sellerNick = message.toid.nick;\n            var buyerNick = message.fromid.nick;\n            var detectedAt = DateTime.Now;\n\n            // This is the authoritative business claim. It is intentionally the first dedupe write\n            // on the buyer path and is shared with background recovery.\n            if (!_handledBuyerMessageDeduplicator.TryAccept(messageKey))\n            {\n                Log.Info("已实际处理的买家消息不再重复入队: key=" + messageKey);\n                return Task.CompletedTask;\n            }''',
    "buyer dedupe ordering")

old_ready = '''            var answerReadyAt = DateTime.Now;\n            var answerSource = KnowledgeLearningService.ResolveAnswerSource(\n                burst.SellerNick,\n                burst.BuyerNick,\n                burst.CombinedQuestion,\n                answer);\n            conversationCtl = ResponseProgressTracker.SetAnswerReady(\n                burst.SellerNick,\n                burst.BuyerNick,\n                burst.CombinedQuestion,\n                answer,\n                answerSource,\n                detectedAt,\n                answerReadyAt);'''
new_ready = '''            // Defensive legacy path: never publish an error/empty model result as AnswerReady.\n            // BuyerStreamingReplyPipeline normally replaces this handler, but if that patch is ever\n            // unavailable the fallback must preserve the same terminal failure semantics.\n            if (string.IsNullOrWhiteSpace(answer) || answer.StartsWith("错误：", StringComparison.Ordinal))\n            {\n                var failure = string.IsNullOrWhiteSpace(answer) ? "错误：AI未返回有效答案。" : answer;\n                if (conversationCtl != null)\n                {\n                    conversationCtl.SetProcessing("AI未生成可用答案");\n                    conversationCtl.SetStatus(failure, false);\n                }\n                ResponseProgressTracker.Fail(burst.SellerNick, burst.BuyerNick, failure);\n                Log.Info("旧文本回复路径AI失败，保持失败态且不进入答案就绪/完成: buyer="\n                    + burst.BuyerNick);\n                return;\n            }\n\n            var answerReadyAt = DateTime.Now;\n            var answerSource = KnowledgeLearningService.ResolveAnswerSource(\n                burst.SellerNick,\n                burst.BuyerNick,\n                burst.CombinedQuestion,\n                answer);\n            conversationCtl = ResponseProgressTracker.SetAnswerReady(\n                burst.SellerNick,\n                burst.BuyerNick,\n                burst.CombinedQuestion,\n                answer,\n                answerSource,\n                detectedAt,\n                answerReadyAt);'''
s = replace_once(s, old_ready, new_ready, "legacy error before answer ready")

s = replace_once(
    s,
    '''            if (string.IsNullOrWhiteSpace(answer) || answer.StartsWith("错误："))\n            {\n                if (conversationCtl != null) conversationCtl.SetSendResult(false, "未发送：AI错误");\n                ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick);\n                return;\n            }\n\n            if (!lease.IsCurrent)''',
    '''            if (!lease.IsCurrent)''',
    "remove post-ready error completion")

QN.write_text(s, encoding="utf-8")

t = TEST.read_text(encoding="utf-8-sig")
extra = r'''


def test_buyer_business_dedupe_is_claimed_only_after_role_classification():
    s = read("src/Bot/ChromeNs/QN.cs")
    method = s[s.index("private Task ProcessIncomingMessageAsync"):s.index("private async Task ProcessBuyerBurstAsync")]
    seller_pos = method.index("if (IsSellerMessage(message))")
    buyer_pos = method.index("if (!IsBuyerMessage(message))")
    handled_pos = method.index("_handledBuyerMessageDeduplicator.TryAccept(messageKey)")
    transport_pos = method.index("_incomingMessageDeduplicator.TryAccept(messageKey)")
    assert seller_pos < transport_pos < buyer_pos < handled_pos
    assert method.count("_incomingMessageDeduplicator.TryAccept(messageKey)") == 1
    assert "This is the authoritative business claim" in method


def test_legacy_text_path_cannot_complete_an_ai_error():
    s = read("src/Bot/ChromeNs/QN.cs")
    method = s[s.index("private async Task ProcessTextBurstAsync"):s.index("private async Task ProcessBuyerBurstAsync") if False else s.index("private async Task ProcessVisionBurstAsync")]
    failure_check = 'if (string.IsNullOrWhiteSpace(answer) || answer.StartsWith("错误：", StringComparison.Ordinal))'
    assert failure_check in method
    assert method.index(failure_check) < method.index("ResponseProgressTracker.SetAnswerReady(")
    failure_block = method[method.index(failure_check):method.index("var answerReadyAt = DateTime.Now;")]
    assert "ResponseProgressTracker.Fail" in failure_block
    assert "ResponseProgressTracker.Complete" not in failure_block


def test_streaming_timeout_is_terminal_failure_not_completed():
    s = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    catch_start = s.index("catch (OperationCanceledException)")
    catch_end = s.index("catch (Exception ex)", catch_start)
    block = s[catch_start:catch_end]
    assert "ResponseProgressTracker.Fail" in block
    assert "return;" in block
    assert "ResponseProgressTracker.Complete" not in block


def test_wecom_reason_is_bounded_below_control_plane_limit():
    s = read("src/Bot/ChromeNs/WeComAppBridgeClient.cs")
    assert '["reason"] = SafePayload(rawReason, 480)' in s
    assert "schema limits reason to 500" in s


def test_central_log_redaction_covers_colons_and_json_identity_fields():
    s = read("src/BotLib/Log.cs")
    assert "(?:=|:|：)" in s
    assert "RuntimeIdentityJsonFieldRegex" in s
    for key in ("seller", "buyer", "session", "客服", "买家"):
        assert key in s


def test_websocket_diagnostics_distinguish_page_channels_from_business_cdp():
    s = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    assert '"已连接｜业务CDP=" + authoritativeCdpSessionCount + "｜页面通道=" + wsSessionCount' in s
    assert "RecordAuthoritativeCdpSessionCount" in s
'''
if "test_buyer_business_dedupe_is_claimed_only_after_role_classification" in t:
    raise RuntimeError("batch3 tests already present")
TEST.write_text(t.rstrip() + extra + "\n", encoding="utf-8")
print("runtime stability 1077 batch3 patch applied")
