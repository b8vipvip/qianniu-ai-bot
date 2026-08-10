from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_scanner_discovers_every_qianniu_window_and_tracks_by_hwnd():
    finder = read("src/Bot/Automation/ChatDeskNs/Automators/QnAccountFinder.cs")
    scanner = read("src/Bot/ControllerNs/DeskScanner.cs")

    assert "GetOpenChatWnds()" in finder
    assert "HashSet<int>" in finder
    assert "Desk.FindExistingByHwnd(chatWnd.Hwnd)" in scanner
    assert "foreach (var chatWnd in opened)" in scanner
    assert "foreach (var desk in Desk.Snapshot().ToList())" in scanner
    assert "Desk.Inst.ProcessId" not in scanner
    assert "GetOpenedSingleChatWnd" not in scanner


def test_desk_registry_resolves_scope_and_never_routes_instance_ui_through_last_desk():
    desk = read("src/Bot/Automation/ChatDeskNs/Desk.cs")

    assert "ConcurrentDictionary<int, Desk> DesksByHwnd" in desk
    assert "FindExistingBySellerNick" in desk
    assert "FindExistingByHwnd" in desk
    assert "ShopSettingsScope.Current" in desk
    assert "QN.CurQN" in desk
    assert "DesksByHwnd[Hwnd.Handle] = this" in desk
    assert "DesksByHwnd.TryRemove" in desk

    change_area = desk[desk.index("public void ChangeBuyer"):desk.index("public void SetActiveQn")]
    assert "inst.AssistWindow" not in change_area
    assert "var assist = AssistWindow" in change_area


def test_rpa_has_explicit_seller_bound_native_window_binding():
    binding = read("src/Bot/ChromeNs/QNRpa.MultiShopDeskBinding.cs")
    scope = read("src/Bot/ShopScope/ShopSettingsScope.cs")
    coordinator = read("src/Bot/ChromeNs/MultiShopRuntimeSessionCoordinator.cs")

    assert "Desk.FindExistingBySellerNick(SellerNick)" in binding
    assert "FlaUI.Core.Application.Attach(desk.ProcessId)" in binding
    assert "if (desks.Count == 1)" in binding
    assert "if (Desk.HasMultipleDesks)" in binding
    assert "禁止猜测其他店铺" in binding
    assert "MultiShopRuntimeSessionCoordinator.EnsureShopBinding(shop)" in scope
    assert "EvRecieveNewMessage += Qn_EvRecieveNewMessage" in coordinator
    assert "EnsureSellerDeskBinding(force)" in coordinator


def test_response_progress_cards_are_created_on_the_exact_seller_desk():
    tracker = read("src/Bot/ChromeNs/ResponseProgressTracker.cs")

    assert "var sellerDesk = Desk.FindExistingBySellerNick(seller);" in tracker
    assert "sellerDesk.AddConversation(" in tracker
    observe = tracker[tracker.index("public static CtlConversation ObserveQuestion"):tracker.index("public static CtlConversation BeginAnswer")]
    assert "Desk.Inst.AddConversation" not in observe


def test_each_attached_robot_is_resynchronized_from_its_own_qn():
    session = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MultiShopSession.cs")
    coordinator = read("src/Bot/ChromeNs/MultiShopRuntimeSessionCoordinator.cs")

    assert "_desk.WndTitle" in session
    assert "ReferenceEquals(_preQN, qn)" in session
    assert "_preQN = qn" in session
    assert "RefreshConversations();" in session
    assert "robot.SynchronizeSellerSession(qn)" in coordinator
    assert "Desk.FindExistingBySellerNick(seller)" in coordinator


def test_new_multi_shop_sources_are_in_wpf_compile_graph():
    props = read("src/Bot/Directory.Build.props")
    for name in (
        "ChromeNs\\MultiShopRuntimeSessionCoordinator.cs",
        "ChromeNs\\QNRpa.MultiShopDeskBinding.cs",
        "AssistWindow\\Widget\\Robot\\CtlRobot.MultiShopSession.cs",
    ):
        assert name in props
