from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_first_inquiry_fast_path_uses_background_notification_ccode_without_chat_switch():
    source = read("src/Bot/ChromeNs/FirstInquiryStreamingGuard.cs")

    assert 'e.Type, "onShopRobotReceriveNewMsgs"' in source
    assert "active.Conversation.Ccode" in source
    assert 'cdp.Invoke<JObject>("im.singlemsg.GetRemoteHisMsg"' in source
    assert "directCcode=true, mergeWait=false" in source
    assert "OpenChat(" not in source
    assert "GetCurrentConversationID(" not in source
    assert "BackgroundRecoveryPostSwitchHydrationDelayMs" not in source


def test_first_inquiry_fast_path_excludes_product_metadata_and_platform_tips():
    source = read("src/Bot/ChromeNs/FirstInquiryStreamingGuard.cs")

    assert "IsRealFirstInquiryMessage" in source
    assert "ConversationContextStore.IsPlatformSystemTip(message, text)" in source
    assert "ConversationContextStore.IsProductLink(message, text)" in source
    assert "ConversationContextStore.IsWithdrawalNotice(message, text)" in source
    assert "earliest real buyer-authored content" in source


def test_first_real_message_is_replayed_before_later_context_messages():
    source = read("src/Bot/ChromeNs/FirstInquiryStreamingGuard.cs")

    first_process = source.index(
        "ProcessRecoveredBuyerMessageAfterMissAsync(first, seller, buyer)"
    )
    staging_delay = source.index("await Task.Delay(160)", first_process)
    later_loop = source.index("foreach (var message in recentBuyerMessages)", staging_delay)

    assert first_process < staging_delay < later_loop
    assert "DeterministicAutoReplyService before merge" in source


def test_fast_path_claims_recovery_only_after_real_first_message_exists():
    source = read("src/Bot/ChromeNs/FirstInquiryStreamingGuard.cs")

    candidate = source.index("var first = recentBuyerMessages.FirstOrDefault(")
    predicate = source.index("IsRealFirstInquiryMessage", candidate)
    empty_guard = source.index("if (first == null)", predicate)
    claim = source.index("MarkBuyerMessageObserved(seller, buyer);", empty_guard)

    assert candidate < predicate < empty_guard < claim
    assert "IsReplyableFirstInquiryCandidate" in source
    assert "NonBuyerConversationGuard.ShouldBlockMessage" in source


def test_later_background_notifications_do_not_restart_active_first_inquiry_recovery():
    source = read("src/Bot/ChromeNs/FirstInquiryStreamingGuard.cs")

    assert "_firstInquiryFastRecoveryActive.TryAdd(key, 0)" in source
    assert "后续同买家通知已合并" in source
    assert "_firstInquiryFastRecoveryWindowStart" in source


def test_completed_fast_recovery_keeps_window_until_age_expiry_to_block_replay():
    source = read("src/Bot/ChromeNs/FirstInquiryStreamingGuard.cs")

    finally_start = source.index("finally\n                {", source.index("Task.Run(async () =>"))
    finally_end = source.index("                }\n            });", finally_start)
    finally_block = source[finally_start:finally_end]

    assert "recovered ||" not in finally_block
    assert "Keep the original recovery window after success as a short replay guard" in finally_block
    assert "DateTime.Now - windowStart > TimeSpan.FromSeconds(30)" in finally_block
    assert "_firstInquiryFastRecoveryWindowStart.TryRemove(key, out ignoredStart)" in finally_block
