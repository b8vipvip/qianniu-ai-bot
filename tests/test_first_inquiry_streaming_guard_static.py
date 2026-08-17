from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_deterministic_reply_service_is_compiled_before_ai_dispatch():
    props = read("src/Bot/Directory.Build.props")
    coordinator = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    service = read("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")

    assert "ChromeNs\\DeterministicAutoReplyService.cs" in props
    deterministic = coordinator.index("DeterministicAutoReplyService.TryHandleAsync")
    legacy_gate = coordinator.index("LegacyAiConfigurationGate.WaitAsync", deterministic)
    assert deterministic < legacy_gate
    assert "不检查AI接口" in service
    assert "MyOpenAI" not in service
    assert "AiEndpointStore" not in service


def test_shop_scoped_replies_are_not_serialized_behind_one_slow_ai_request():
    coordinator = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")

    shop_branch = coordinator.index("using (ShopSettingsScope.Enter(shop))")
    shop_body = coordinator[shop_branch:]
    assert "DeterministicAutoReplyService.TryHandleAsync" in shop_body
    assert "await _handler(lease);" in shop_body
    assert "LegacyAiConfigurationGate.WaitAsync" not in shop_body


def test_first_inquiry_is_sent_locally_and_committed_only_after_real_send():
    service = read("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")

    resolve = service.index("FirstInquiryFixedReplyService.TryResolve(")
    send = service.index("qn.SendTextWithRetryAsync(", resolve)
    mark = service.index("FirstInquiryFixedReplyService.MarkDelivered(", send)
    release = service.index("FirstInquiryFixedReplyService.ReleaseReservation(")

    assert resolve < send < mark
    assert release < send or release > mark
    assert "lease.ConfirmStableAsync(160)" in service
    assert "首条咨询固定回复" in service
    assert "未调用AI" in service


def test_off_hours_reply_is_a_fixed_local_rule_not_manual_keyword_or_ai_dependent():
    service = read("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")

    assert "cfg.EnableWorkHours" in service
    assert "cfg.OffHoursFixedText" in service
    assert "IsInsideWorkHours" in service
    assert 'source = "下班自动回复"' in service
    assert "EvaluateAutoReplyRule" not in service
    assert "OffHoursReplyMode" not in service
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
