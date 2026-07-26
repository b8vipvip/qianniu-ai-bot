from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_order_trace_and_alias_bridges_are_started():
    app = text("src/Bot/App.xaml.cs")
    targets = text("src/Directory.Build.targets")

    assert "OrderNotificationTraceBridge.Start();" in app
    assert "BuyerIdentityAliasRuntimeBridge.Initialize();" in app
    assert "BuyerIdentityAliasUiBridge.Start();" in app
    assert "OrderNotificationTraceBridge.cs" in targets
    assert "BuyerIdentityAliasRuntimeBridge.cs" in targets
    assert "BuyerIdentityAliasUiBridge.cs" in targets


def test_runtime_build_identity_is_logged_and_built():
    app = text("src/Bot/App.xaml.cs")
    identity = text("src/Bot/Update/RuntimeBuildIdentityService.cs")
    targets = text("src/Directory.Build.targets")

    assert "RuntimeBuildIdentityService.Initialize();" in app
    assert "运行构建身份:" in identity
    assert "sha256=" in identity
    assert "exe=" in identity
    assert "release-info.json" in identity
    assert "RuntimeBuildIdentityService.cs" in targets


def test_legacy_about_menu_opens_update_center():
    app = text("src/Bot/App.xaml.cs")
    redirect = text("src/Bot/Options/LegacyAboutUpdateRedirect.cs")
    update_ui = text("src/Bot/Options/BotUpdateOptionsControl.cs")
    targets = text("src/Directory.Build.targets")

    assert "LegacyAboutUpdateRedirect.Initialize();" in app
    assert 'string.Equals(HeaderText(item), "关于"' in redirect
    assert "BotAboutUpdateLauncher.Show" in redirect
    assert "手动检查更新" in update_ui
    assert "当前程序与构建信息" in update_ui
    assert "LegacyAboutUpdateRedirect.cs" in targets


def test_alias_bridge_observes_real_message_payloads():
    bridge = text("src/Bot/ChromeNs/BuyerIdentityAliasRuntimeBridge.cs")
    alias = text("src/Bot/ChromeNs/BuyerIdentityAliasService.cs")

    assert "EvRecieveNewMessage += OnReceiveNewMessage" in bridge
    assert "JsonConvert.DeserializeObject<ChatResponse>" in bridge
    assert "BuyerIdentityAliasService.ObserveMessage" in bridge
    assert "message.fromid.display" in alias
    assert "message.fromid.targetId" in alias
