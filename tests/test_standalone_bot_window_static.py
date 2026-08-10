from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_desktop_window_is_fallback_when_no_qianniu_desk_exists():
    startup = read("src/Bot/AssistWindow/BotDesktopStartup.cs")
    desktop = read("src/Bot/AssistWindow/BotDesktopWindow.cs")

    assert "typeof(WndNotifyIcon)" in startup
    assert "Desk.Snapshot().Count > 0" in startup
    assert "TotalSeconds < 2.5" in startup
    assert "BotDesktopWindow.ShowMain" in startup
    assert "默认使用每店铺独立贴窗 Bot" in startup
    assert "独立工作台可从托盘手动打开" in startup
    assert "CreateAndAttachToDesk" not in startup
    assert "new CtlRobot(null, null)" in desktop
    assert "WindowStartupLocation.CenterScreen" in desktop
    assert "千牛：等待连接" in desktop


def test_desktop_window_reuses_existing_switch_settings_and_stats_services():
    desktop = read("src/Bot/AssistWindow/BotDesktopWindow.cs")

    assert "Params.Robot.CanUseRobot" in desktop
    assert "Params.Robot.SetIsAutoReply" in desktop
    assert "WndOption.MyShow(seller, this)" in desktop
    assert "_robot.ShowDataDesk(this)" in desktop
    assert "BotConnectionDiagnostics.GetSnapshot()" in desktop
    assert "ShopContextLocator.ResolveBySellerNick" in desktop
    assert "ShopProfileStore" in desktop


def test_desktop_mirror_is_passive_and_cannot_duplicate_runtime_side_effects():
    bridge = read("src/Bot/AssistWindow/DesktopBotUiBridge.cs")
    mirror = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.DesktopMirror.cs")
    conversation = read("src/Bot/AssistWindow/Widget/Robot/CtlConversation.DesktopMirror.cs")

    assert "FrameworkElement.LoadedEvent" in bridge
    assert "conversation.IsDesktopMirror" in bridge
    assert "ObservedConversations" in bridge
    assert "new CtlConversation { IsDesktopMirror = true }" in mirror
    assert "BotRuntimeStats.RecordDisplayedAnswer" not in mirror
    assert "BotRuntimeStats.RecordReception" not in mirror
    assert "RefreshItems();" not in mirror
    assert "SendTextWithRetryAsync" not in bridge + mirror + conversation
    assert "MyOpenAI" not in bridge + mirror + conversation


def test_qianniu_desk_and_attached_window_pipeline_remains_authoritative_when_desks_exist():
    desk = read("src/Bot/Automation/ChatDeskNs/Desk.cs")
    assist = read("src/Bot/AssistWindow/WndAssist.xaml.cs")
    scanner = read("src/Bot/ControllerNs/DeskScanner.cs")

    assert "WndAssist.CreateAndAttachToDesk(desk);" in desk
    assert "GetOpenChatWnds()" in scanner
    assert "Desk.FindExistingByHwnd" in scanner
    assert "Desk.EvClosed += Desk_EvClosed;" in assist
    assert "Desk.EvMinimize += Desk_EvMinimize;" in assist
    assert "Desk.EvMoved += Desk_EvMoved;" in assist
    assert "Desk.EvResized += Desk_EvResized;" in assist
    assert "ctlRightPanel.Init(this);" in assist


def test_desktop_can_be_reopened_from_tray_and_sources_are_in_wpf_compile_graph():
    tray = read("src/Bot/AssistWindow/NotifyIcon/MenuCreator/HelpMenuCreator.cs")
    props = read("src/Bot/Directory.Build.props")

    assert 'CreateItem("打开Bot工作台", OnOpenBotDesktopClicked)' in tray
    assert "BotDesktopWindow.ShowMain();" in tray
    for name in (
        "AssistWindow\\BotDesktopWindow.cs",
        "AssistWindow\\BotDesktopStartup.cs",
        "AssistWindow\\DesktopBotUiBridge.cs",
        "AssistWindow\\Widget\\Robot\\CtlConversation.DesktopMirror.cs",
        "AssistWindow\\Widget\\Robot\\CtlRobot.DesktopMirror.cs",
        "Options\\WndOption.DesktopOwner.cs",
    ):
        assert name in props
