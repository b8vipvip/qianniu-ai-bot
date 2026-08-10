from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_attached_bot_is_default_when_qianniu_desks_exist():
    startup = read("src/Bot/AssistWindow/BotDesktopStartup.cs")
    scanner = read("src/Bot/ControllerNs/DeskScanner.cs")
    desk = read("src/Bot/Automation/ChatDeskNs/Desk.cs")

    assert "Desk.Snapshot().Count > 0" in startup
    assert "默认使用每店铺独立贴窗 Bot" in startup
    assert "GetOpenChatWnds()" in scanner
    assert "Desk.FindExistingByHwnd" in scanner
    assert "WndAssist.CreateAndAttachToDesk(desk);" in desk


def test_each_attached_settings_window_uses_its_desk_seller_shopkey():
    entry = read("src/Bot/AssistWindow/Widget/RightPanel.SettingsEntry.cs")
    window = read("src/Bot/Options/WndOption.xaml.cs")
    assert "WndOption.MyShow(Wnd.Desk.WndTitle, Wnd);" in entry
    assert "ShopContextLocator.ResolveBySellerNick(seller)" in window
    assert "ShopKey：" in window
    assert "using (ShopSettingsScope.Enter(_shop))" in window


def test_update_page_has_no_fixed_minheight_and_primary_actions_are_before_preferences():
    ui = read("src/Bot/Options/BotUpdateOptionsControl.cs")
    xaml = read("src/Bot/Options/WndOption.xaml")

    assert "MinHeight = 580" not in ui
    assert "MinWidth = 650" not in ui
    assert "CanContentScroll = false" in ui
    assert "PanningMode = PanningMode.VerticalOnly" in ui
    assert ui.index('CreateTitle("检查与安装")') < ui.index('CreateTitle("自动检查设置")')
    assert 'HorizontalContentAlignment="Stretch"' in xaml
    assert 'VerticalContentAlignment="Stretch"' in xaml
    assert 'ClipToBounds="True"' in xaml


def test_shop_binding_page_replaces_ai_service_navigation_and_can_pull_cloud_knowledge():
    window = read("src/Bot/Options/WndOption.xaml.cs")
    binding = read("src/Bot/Options/ShopBindingOptionsControl.cs")
    sync = read("src/Bot/Knowledge/KnowledgeCloudSyncService.cs")

    assert 'AddPage("店铺与连接", "AI 服务"' not in window
    assert "if (showPage == OptionEnum.Robot) showPage = OptionEnum.ShopBinding;" in window
    assert "本店 Bot 服务端地址" in binding
    assert "本店 Bot 客户端令牌" in binding
    assert "保存连接并立即同步知识库" in binding
    assert "await KnowledgeCloudSyncService.SyncNowAsync(_shop)" in binding
    assert "internal static async Task SyncNowAsync(ShopContext shop)" in sync
