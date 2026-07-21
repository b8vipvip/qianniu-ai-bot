from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_send_watchdog_requires_real_seller_echo_and_queues_ai_report():
    source = read("src/Bot/ChromeNs/SendDeliveryWatchdog.cs")
    assert "HasRecentSellerEcho" in source
    assert "SendFailureAnomalyService.Queue" in source
    assert "答案已经生成并进入自动发送流程" in source
    assert "VerifyDelayMilliseconds = 9000" in source


def test_unknown_qianniu_version_cannot_fall_into_smart_tip_false_success_path():
    monitor = read("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")
    assert 'qn.QnVersion = "999.999.999N"' in monitor
    assert "禁止误走SendSmartTipMsg" in monitor
    assert "Version.TryParse" in monitor


def test_inflight_burst_is_detached_before_ai_handler_and_new_message_can_start_worker():
    source = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    clear_index = source.index("state.Items.Clear();")
    handler_index = source.index("await _handler(lease);")
    assert clear_index < handler_index
    assert "state.WorkerRunning = false;" in source[clear_index:handler_index]
    assert "var dispatchedItems = state.Items.ToList();" in source
    assert "return state.Version == capturedVersion;" in source


def test_manual_seller_reply_invalidates_old_ai_generation():
    monitor = read("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")
    partial = read("src/Bot/ChromeNs/QN.RuntimeSafety.cs")
    coordinator = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    assert "CancelActiveBuyerGeneration" in monitor
    assert "_buyerMessageBurstCoordinator.CancelBuyer" in partial
    assert "state.Version++;" in coordinator
    assert "检测到客服回复" in monitor


def test_progress_card_rotates_when_new_buyer_turn_arrives_during_ai_generation():
    source = read("src/Bot/ChromeNs/ResponseProgressTracker.cs")
    assert "newerTurnDuringGeneration" in source
    assert "entry.AnswerStartedAt != DateTime.MinValue" in source
    assert "已被买家新消息替代，旧答案不会发送" in source
    assert "Entries.TryUpdate(key, replacement, entry)" in source


def test_answer_context_menu_has_copy_action():
    source = read("src/Bot/AssistWindow/Widget/Robot/CtlConversation.xaml.cs")
    assert 'new MenuItem { Header = "复制" }' in source
    assert "Clipboard.SetText(_answer ?? string.Empty);" in source


def test_control_plane_runtime_guard_is_installed_and_packaged():
    bootstrap = read("services/api-control-plane/bootstrap.py")
    dockerfile = read("services/api-control-plane/Dockerfile")
    guard = read("services/api-control-plane/runtime_routing_guard.py")
    assert "runtime_routing_guard.install(control_plane)" in bootstrap
    assert "runtime_routing_guard.py" in dockerfile
    assert "RUNTIME_TOTAL_BUDGET_SECONDS" in guard
    assert "RUNTIME_ATTEMPT_TIMEOUT_SECONDS" in guard
    assert "Round-robin protocols across models" in guard
