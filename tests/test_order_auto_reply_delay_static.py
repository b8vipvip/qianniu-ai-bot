from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_order_delay_setting_defaults_to_one_second_and_is_bounded():
    settings = read("src/Bot/Options/OrderPlacedReplyDelaySettings.cs")
    app = read("src/Bot/App.xaml.cs")
    targets = read("src/Directory.Build.targets")

    assert 'DefaultDelaySeconds = 1' in settings
    assert 'MaxDelaySeconds = 300' in settings
    assert 'Math.Max(0, Math.Min(MaxDelaySeconds, seconds))' in settings
    assert 'Text = "延时发送（秒）"' in settings
    assert '0=立即发送，默认 1 秒' in settings
    assert 'OrderPlacedReplyDelaySettings.Initialize();' in app
    assert 'Options\\OrderPlacedReplyDelaySettings.cs' in targets


def test_order_delay_happens_immediately_before_manual_bypass_and_real_send():
    order = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")

    delay = 'await Task.Delay(TimeSpan.FromSeconds(delaySeconds));'
    bypass = 'KnowledgeLearningService.AllowNextManualSend(plan.Seller, plan.Buyer, answer);'
    send = 'var sendOk = await SendTextWithRetryAsync(plan.Buyer, answer, 1);'

    assert 'var delaySeconds = OrderPlacedReplyDelaySettings.GetSeconds();' in order
    assert delay in order
    assert 'if (!Params.Robot.CanUseRobotReal || !Params.Robot.GetIsAutoReply())' in order
    assert order.index(delay) < order.index(bypass) < order.index(send)
