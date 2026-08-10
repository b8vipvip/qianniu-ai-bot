from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_standalone_workbench_has_been_removed_and_attached_ui_is_authoritative():
    removed = (
        "src/Bot/AssistWindow/BotDesktopStartup.cs",
        "src/Bot/AssistWindow/BotDesktopWindow.cs",
        "src/Bot/AssistWindow/DesktopBotUiBridge.cs",
        "src/Bot/AssistWindow/Widget/Robot/CtlConversation.DesktopMirror.cs",
        "src/Bot/AssistWindow/Widget/Robot/CtlRobot.DesktopMirror.cs",
        "src/Bot/Options/WndOption.DesktopOwner.cs",
    )
    for path in removed:
        assert not (ROOT / path).exists(), path

    tray = read("src/Bot/AssistWindow/NotifyIcon/MenuCreator/HelpMenuCreator.cs")
    props = read("src/Bot/Directory.Build.props")
    coordinator = read("src/Bot/ChromeNs/MultiShopRuntimeSessionCoordinator.cs")
    desk = read("src/Bot/Automation/ChatDeskNs/Desk.cs")
    scanner = read("src/Bot/ControllerNs/DeskScanner.cs")

    assert "打开Bot工作台" not in tray
    assert "BotDesktop" not in tray
    assert "BotDesktop" not in props
    assert "DesktopMirror" not in props
    assert "BotDesktopStartup" not in coordinator
    assert "WndAssist.CreateAndAttachToDesk(desk);" in desk
    assert "GetOpenChatWnds()" in scanner
    assert "EnsureVisibleForMultiShopAttachedMode" in scanner


def test_attached_multi_shop_helpers_remain_in_main_and_wpf_temporary_builds():
    props = read("src/Bot/Directory.Build.props")
    for name in (
        "AssistWindow\\WndAssist.MultiShopAttached.cs",
        "AssistWindow\\Widget\\Robot\\CtlRobot.MultiShopSession.cs",
        "Automation\\ChatDeskNs\\DeskSellerBindingRegistry.cs",
        "ChromeNs\\MultiShopRuntimeSessionCoordinator.cs",
        "ChromeNs\\QNRpa.MultiShopDeskBinding.cs",
    ):
        assert name in props
