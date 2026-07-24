from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_unified_api_visible_portability_and_real_bot_test():
    xaml = read("src/Bot/Options/CtlRobotOptions.xaml")
    partial = read("src/Bot/Options/CtlRobotOptions.ControlPlane.Portability.cs")
    assert 'Content="测试 Bot 真实流程"' in xaml
    assert 'Content="导入配置"' in xaml
    assert 'Content="导出配置"' in xaml
    assert "btnTestBotFlow_Click" in partial
    assert "BotFlowTestService.PickRandomCandidateAsync" in partial
    assert "BotFlowTestService.RunAsync" in partial
    assert "qianniu-control-plane-config" in partial
    assert "clientToken" in partial


def test_question_and_answer_timestamps_are_visible_before_answer():
    xaml = read("src/Bot/AssistWindow/Widget/Robot/CtlConversation.xaml")
    code = read("src/Bot/AssistWindow/Widget/Robot/CtlConversation.xaml.cs")
    qn = read("src/Bot/ChromeNs/QN.cs")
    tracker = read("src/Bot/ChromeNs/ResponseProgressTracker.cs")
    assert 'x:Name="txtQuestionTime"' in xaml
    assert 'x:Name="txtAnswerTime"' in xaml
    assert 'x:Name="txtLatency"' in xaml
    assert "识别 " in code and "答案 " in code and "响应 " in code
    assert "ResponseProgressTracker.ObserveQuestion" in qn
    assert "ResponseProgressTracker.BeginAnswer" in qn
    assert "ResponseProgressTracker.SetAnswerReady" in qn
    assert "正在获取答案" in tracker


def test_speed_optimizations_are_guarded():
    burst = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    timing = read("src/Bot/ChromeNs/AdaptiveReplyTimingService.cs")
    context = read("src/Bot/ChromeNs/ConversationContextStore.cs")
    qn = read("src/Bot/ChromeNs/QN.cs")
    ai = read("src/Bot/ChromeNs/MyOpenAI.cs")
    assert "baseline = 350;" in burst
    assert "baseline = 800;" in burst
    assert "Clamp(adjusted, 300, 950)" in timing
    assert "Clamp(adjusted, 650, 1550)" in timing
    assert "TimeSpan.FromSeconds(4)" in burst
    assert "Task.Run(() => RefreshRemoteHistory" in context
    assert "ConfirmStableAsync(220)" in qn
    assert "SharedHttp" in ai
    assert "Timeout.InfiniteTimeSpan" in ai
    assert "HttpRequestMessage" in ai


def test_log_page_discovers_real_chinese_log_and_flushes():
    log = read("src/BotLib/Log.cs")
    writer = read("src/BotLib/LogWriter.cs")
    settings = read("src/Bot/Options/CtlRobotOptions.xaml.cs")
    app_life = read("src/Bot/StartUp/AppLife.cs")
    assert "运行日志.txt" in app_life
    assert "CurrentFileName" in log
    assert "public static void Flush()" in log
    assert "public void Flush()" in writer
    assert 'IndexOf("日志"' in settings
    assert "Encoding.GetEncoding(936)" in settings
    assert "写入测试日志" in settings
    assert "清空日志" in settings
    assert "Log.Flush();" in settings


def test_optional_partials_are_compiled_in_wpf_temp_projects():
    targets = read("src/Directory.Build.targets")
    for name in (
        "ResponseProgressTracker.cs",
        "BotFlowTestService.cs",
        "CtlRobotOptions.ControlPlane.Portability.cs",
    ):
        assert name in targets
