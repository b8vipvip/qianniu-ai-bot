from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_local_short_reply_defaults_cover_common_acknowledgements():
    service = read("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")
    assert '"在吗"' in service
    assert '"你好"' in service
    assert '"好的"' in service
    assert '"谢谢"' in service
    assert '"ok"' in service.lower()


def test_local_short_reply_store_is_shop_scoped_and_has_runtime_management():
    service = read("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")
    assert "ShopScopedSettingsStore" in service
    assert "ShopSettingsScope.Current" in service
    assert "LocalShortReplyUi" in service
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