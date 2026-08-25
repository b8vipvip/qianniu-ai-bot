from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_order_preset_is_forced_to_zero_delay_and_old_values_are_ignored():
    settings = read("src/Bot/Options/OrderPlacedReplyDelaySettings.cs")
    app = read("src/Bot/App.xaml.cs")
    targets = read("src/Directory.Build.targets")

    assert 'ForcedDelaySeconds = 0' in settings
    assert 'public static int GetSeconds()' in settings
    get_seconds = settings.split('public static int GetSeconds()', 1)[1].split('public static void SaveSeconds', 1)[0]
    assert 'return ForcedDelaySeconds;' in get_seconds
    assert 'GetParam2Key' not in get_seconds
    assert 'IsReadOnly = true' in settings
    assert 'IsEnabled = false' in settings
    assert '强制立即发送（0 秒），优先于后续普通 AI 回复' in settings
    assert 'forced-immediate' in settings
    assert 'OrderPlacedReplyDelaySettings.Initialize();' in app
    assert 'Options\\OrderPlacedReplyDelaySettings.cs' in targets


def test_order_reply_keeps_delay_gate_but_forced_zero_reaches_segment_sender_immediately():
    order = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    settings = read("src/Bot/Options/OrderPlacedReplyDelaySettings.cs")

    delay_lookup = 'var delaySeconds = OrderPlacedReplyDelaySettings.GetSeconds();'
    delay_guard = 'if (delaySeconds > 0)'
    preset_send = 'presetSendResult = await SendOrderPresetAnswerAsync(plan, answer);'
    legacy_send = 'sendOk = await SendTextWithRetryAsync(plan.Buyer, answer, 1);'

    assert delay_lookup in order
    assert delay_guard in order
    assert preset_send in order
    assert legacy_send in order
    assert 'KnowledgeLearningService.AllowNextManualSend(plan.Seller, plan.Buyer, answer);' not in order
    assert 'ResponseProgressTracker.HasActiveManualIntervention' in order
    assert 'return ForcedDelaySeconds;' in settings
    assert order.index(delay_lookup) < order.index(delay_guard) < order.index(preset_send)
    assert order.index(preset_send) < order.index(legacy_send)


def test_direct_order_event_is_dispatched_before_normal_message_loop_and_uses_shared_send_gate():
    qn = read("src/Bot/ChromeNs/QN.cs")
    bridge = read("src/Bot/ChromeNs/DirectOrderEventBridge.cs")

    event_raise = 'EvRecieveNewMessage(this, e);'
    normal_loop = 'await ProcessIncomingMessageAsync(message);'
    assert qn.index(event_raise) < qn.index(normal_loop)
    assert 'qn.EvRecieveNewMessage += OnReceiveNewMessage;' in bridge
    assert 'await qn.ProcessDirectOrderMessageAsync' in bridge
    assert 'private readonly SemaphoreSlim _sendGate' in qn
    assert 'await _sendGate.WaitAsync();' in qn
