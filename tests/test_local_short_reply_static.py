from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_local_short_reply_is_shop_scoped_exact_match_and_never_calls_ai():
    service = read("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")

    assert 'ConfigFileName = "local-short-replies.json"' in service
    assert "Paths.GetConfigPath(shop, ConfigFileName)" in service
    assert "ShopSettingsScope.Current" in service
    assert "ShopContextLocator.ResolveRuntimeBySellerNick(seller)" in service
    assert "NormalizePhrase(phrase), normalized, StringComparison.Ordinal" in service
    assert "TrailingSafePunctuation" in service
    assert "？" not in service[service.index("TrailingSafePunctuation"):service.index("private sealed class CacheState")]
    assert "Contains(normalized)" not in service

    local_start = service.index("internal static class LocalShortReplyService")
    ui_start = service.index("namespace Bot.Knowledge")
    local_block = service[local_start:ui_start]
    assert "AiEndpointStore" not in local_block
    assert "StreamMessagesAsync" not in local_block
    assert "SemanticEmbeddingService" not in local_block


def test_defaults_cover_common_acknowledgement_thanks_wait_solved_and_closing_phrases():
    service = read("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")

    for phrase in [
        "好的", "好", "OK", "嗯", "收到", "知道了", "明白了",
        "谢谢", "辛苦了", "已经好了", "解决了", "稍等一下",
        "我试试", "不用了", "不好意思", "再见", "晚安",
    ]:
        assert phrase in service

    assert '"好的。"' in service
    assert '"不客气。"' in service
    assert '"好的，您先操作，有问题再告诉我。"' in service
    assert '"好的，有需要再联系我们。"' in service


def test_knowledge_center_gets_editable_short_message_management_page():
    service = read("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")

    assert 'Header = "短消息回复"' in service
    assert '"问答管理"' in service
    assert "managerIndex + 1" in service
    assert "LocalShortReplyManagerControl" in service
    assert '"新增"' in service
    assert '"编辑所选"' in service
    assert '"启用/停用"' in service
    assert '"删除所选"' in service
    assert '"恢复默认模板"' in service
    assert '"导入JSON"' in service
    assert '"导出JSON"' in service
    assert "LocalShortReplyEditWindow" in service
    assert "SaveForCurrentUi" in service


def test_deterministic_short_reply_precedes_normal_merge_and_preserves_handoff_rule_priority():
    service = read("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")

    local = service.index("LocalShortReplyService.TryResolve(")
    normal_merge = service.index("return true;", local)
    handoff_check = service.rfind("BotFeatureStore.EvaluateAutoReplyRule(question)", 0, local)
    assert handoff_check >= 0
    assert handoff_check < local < normal_merge
    assert '"本地短消息回复"' in service
    assert "return false;" in service[local:normal_merge]
    assert "aiCalled=false" in service


def test_new_buyer_message_can_use_short_reply_without_cancelling_dispatched_ai():
    coordinator = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")

    enqueue_start = coordinator.index("public void Enqueue(BuyerMessageBurstItem item)")
    pending = coordinator.index("allowLocalShortReply = !HasPendingBuyerMessages", enqueue_start)
    deterministic = coordinator.index("DeterministicAutoReplyService.HandleBeforeMergeAsync(", pending)
    merge = coordinator.index("if (continueToMerge) EnqueueForMerge(item);", deterministic)
    assert enqueue_start < pending < deterministic < merge
    assert "InvalidateDispatchedAnswerOnArrival(item.SellerNick, item.BuyerNick);" not in coordinator[enqueue_start:merge]

    assert "state.Items.Count == 0 && !state.WorkerRunning" in coordinator
    assert "state.Version++;" in coordinator
    assert "_states.TryRemove(key, out ignored);" in coordinator
    assert "return state.Items.Count > 0;" in coordinator
    assert "allowLocalShortReply" in coordinator


def test_management_page_registration_is_explicit_and_idempotent():
    coordinator = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    service = read("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")

    assert "Bot.Knowledge.LocalShortReplyUi.Initialize();" in coordinator
    assert "Interlocked.Exchange(ref _initialized, 1)" in service
    assert "EventManager.RegisterClassHandler(" in service
    assert "ConditionalWeakTable<KnowledgeCenterWindow, object>" in service