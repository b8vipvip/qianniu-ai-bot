from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_deterministic_reply_service_is_compiled_before_merge_and_ai_dispatch():
    props = read("src/Bot/Directory.Build.props")
    coordinator = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    service = read("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")

    assert "ChromeNs\\DeterministicAutoReplyService.cs" in props
    deterministic = coordinator.index("DeterministicAutoReplyService.HandleBeforeMergeAsync(")
    merge = coordinator.index("EnqueueForMerge(item)", deterministic)
    legacy_gate = coordinator.index("LegacyAiConfigurationGate.WaitAsync", merge)
    assert deterministic < merge < legacy_gate
    assert "不检查AI接口" in service
    assert "MyOpenAI" not in service
    assert "AiEndpointStore" not in service


def test_shop_scoped_replies_are_not_serialized_behind_one_slow_ai_request():
    coordinator = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")

    # Deterministic rules execute before EnqueueForMerge, therefore before either the scoped
    # AI path or the legacy global gate. The scoped post-merge path itself remains lock-free.
    deterministic = coordinator.index("DeterministicAutoReplyService.HandleBeforeMergeAsync(")
    merge = coordinator.index("EnqueueForMerge(item)", deterministic)
    shop_branch = coordinator.index("using (ShopSettingsScope.Enter(shop))", merge)
    shop_body = coordinator[shop_branch:]
    assert deterministic < merge < shop_branch
    assert "await _handler(lease);" in shop_body
    assert "LegacyAiConfigurationGate.WaitAsync" not in shop_body


def test_first_inquiry_is_sent_locally_and_committed_only_after_real_send():
    service = read("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")

    resolve = service.index("FirstInquiryFixedReplyService.TryResolve(")
    invoke_send = service.index("var firstOk = await SendFixedAsync(", resolve)
    success = service.index("if (firstOk)", invoke_send)
    mark = service.index("FirstInquiryFixedReplyService.MarkDelivered(", success)
    failure = service.index("else", mark)
    release = service.index("FirstInquiryFixedReplyService.ReleaseReservation(", failure)
    local_short = service.index("if (allowLocalShortReply)", release)
    failure_block = service[failure:local_short]
    sender = service.index("qn.SendTextWithRetryAsync(item.BuyerNick, answer, 3)")

    assert resolve < invoke_send < success < mark < failure < release < local_short
    assert sender > release  # generic helper implementation appears later in the source file
    assert "首条咨询固定回复" in service
    assert "未调用AI" in service
    # A failed mandatory greeting must consume this buyer message instead of falling through into
    # local-short/context/AI generation. Assert the control flow, not a historical comment string.
    assert "ReleaseReservation(" in failure_block
    assert "return false;" in failure_block
    assert "return true;" not in failure_block


def test_off_hours_reply_is_a_fixed_local_rule_not_manual_keyword_or_ai_dependent():
    service = read("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")

    assert "cfg.EnableWorkHours" in service
    assert "cfg.OffHoursFixedText" in service
    assert "IsInsideWorkHours" in service
    assert '"下班自动回复"' in service
    off_hours_start = service.index("private static bool TryResolveOffHours")
    off_hours_end = service.index("private static bool TryParseClock", off_hours_start)
    off_hours_block = service[off_hours_start:off_hours_end]
    assert "EvaluateAutoReplyRule" not in off_hours_block
    assert "OffHoursReplyMode" not in off_hours_block
    assert "MyOpenAI" not in service


def test_old_reflection_guard_no_longer_rewraps_runtime_handler():
    guard = read("src/Bot/ChromeNs/FirstInquiryStreamingGuard.cs")

    assert "_firstInquiryStreamingGuardBootstrap" in guard
    assert "BuyerMessageBurstCoordinator" in guard
    assert "new Timer" not in guard
    assert "BindingFlags" not in guard
    assert '"_handler"' not in guard
    assert "handlerField.SetValue" not in guard
    assert "不再动态重包消息handler" in guard