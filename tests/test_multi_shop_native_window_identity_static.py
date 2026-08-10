from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_qianniu_generic_window_title_is_never_treated_as_shop_identity():
    finder = read("src/Bot/Automation/ChatDeskNs/Automators/QnAccountFinder.cs")
    settings = read("src/Bot/AssistWindow/Widget/RightPanel.SettingsEntry.cs")

    assert 'value.Equals("千牛接待台"' in finder
    assert "ResolveSellerNameForWindow" in finder
    assert "HasSellerWindowEvidence" in finder
    assert "Process.GetProcessById(pid)" in finder
    assert 'FindAllDesktopWindowByClassNameAndTitlePattern(' in finder
    assert '"Qt5152QWindowIcon"' in finder
    assert "matches.Count == 1" in finder
    assert "qns.Count == 1" in finder
    assert "never guesses between two online shops" in finder

    assert "ResolveSellerNameForWindow" in settings
    assert "IsGenericReceptionTitle(seller)" in settings
    assert "系统不会在多个店铺之间猜测绑定" in settings
    assert "WndOption.MyShow(seller, Wnd)" in settings
    assert "WndOption.MyShow(Wnd.Desk.WndTitle" not in settings


def test_scanner_upgrades_bootstrap_generic_desk_and_keeps_all_attached_shells_visible():
    scanner = read("src/Bot/ControllerNs/DeskScanner.cs")
    assist = read("src/Bot/AssistWindow/WndAssist.MultiShopAttached.cs")
    props = read("src/Bot/Directory.Build.props")

    assert "IsGenericReceptionTitle(existing.WndTitle)" in scanner
    assert "!QnAccountFinder.IsGenericReceptionTitle(chatWnd.Name)" in scanner
    assert "existing.Dispose();" in scanner
    assert "Desk.Create(chatWnd)" in scanner
    assert "EnsureVisibleForMultiShopAttachedMode" in scanner

    assert "Desk.IsVisibleAndNotMinimized" in assist
    assert "ShowAssist()" in assist
    assert "it does not" in assist and "enable AI or sending" in assist
    assert "WndAssist\\MultiShopAttached.cs" in props


def test_existing_seller_bound_runtime_stays_fail_closed_for_multi_shop():
    rpa = read("src/Bot/ChromeNs/QNRpa.MultiShopDeskBinding.cs")
    coordinator = read("src/Bot/ChromeNs/MultiShopRuntimeSessionCoordinator.cs")
    robot = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MultiShopSession.cs")

    assert "Desk.HasMultipleDesks" in rpa
    assert "禁止猜测其他店铺" in rpa
    assert "Desk.FindExistingBySellerNick" in rpa
    assert "Desk.FindExistingBySellerNick" in coordinator
    assert "_desk.WndTitle" in robot
