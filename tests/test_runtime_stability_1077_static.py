from pathlib import Path

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

