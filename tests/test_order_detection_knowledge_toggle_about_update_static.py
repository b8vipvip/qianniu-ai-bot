from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_object_message_center_payload_is_preserved_as_json_text():
    code = read("src/Bot/ChromeNs/OrderPaymentNotificationFallback.cs")
    app = read("src/Bot/App.xaml.cs")
    targets = read("src/Directory.Build.targets")

    assert "FlexibleJsonStringConverter" in code
    assert "objectType == typeof(string)" in code
    assert "token.ToString(Formatting.None)" in code
    assert "JsonConvert.DefaultSettings" in code
    assert "QianniuWebSocketJsonCompatibility.Initialize();" in app
    assert "ChromeNs\\OrderPaymentNotificationFallback.cs" in targets


def test_paid_order_fallback_expands_nested_json_and_never_guesses_buyer():
    code = read("src/Bot/ChromeNs/OrderPaymentNotificationFallback.cs")
    assert "messageCenterNotify嵌套JSON兼容兜底" in code
    assert "JToken.Parse(trimmed)" in code
    assert "customernick" in code
    assert "oppositenick" in code
    assert "sendernick" in code
    assert "conversationnick" in code
    assert "EventCueRegex" in code
    assert "LabeledOrderRegex" in code
    assert "未猜测当前会话" in code
    assert "ProcessDirectOrderMessageAsync" in code


def test_knowledge_enabled_checkbox_is_single_click_two_way_and_persisted():
    code = read("src/Bot/Knowledge/KnowledgeManagerControl.cs")
    assert "IsReadOnly = false" in code
    assert "EnabledTemplate()" in code
    assert "Mode = BindingMode.TwoWay" in code
    assert "UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged" in code
    assert "OnEnabledClicked" in code
    assert "BotFeatureStore.SaveKnowledgeBase(_all)" in code
    assert "点击后立即保存启用状态" in code
    assert "HasVisualAncestor<CheckBox>" in code


def test_legacy_about_menu_opens_full_about_and_update_center():
    menu = read("src/Bot/AssistWindow/NotifyIcon/MenuCreator/HelpMenuCreator.cs")
    ui = read("src/Bot/Options/BotUpdateOptionsControl.cs")
    assert 'CreateItem("关于与版本更新", OnAboutClicked)' in menu
    assert "BotAboutUpdateLauncher.Show();" in menu
    assert "internal static class BotAboutUpdateLauncher" in ui
    assert "OptionEnum.AboutUpdate" in ui
    assert "WndOption.MyShow" in ui
    assert 'Title = "关于与版本更新"' in ui


def test_about_page_shows_build_metadata_and_manual_update_actions():
    ui = read("src/Bot/Options/BotUpdateOptionsControl.cs")
    assert "release-info.json" in ui
    assert 'AddLabel(versionGrid, 1, "构建提交")' in ui
    assert 'AddLabel(versionGrid, 2, "发布时间/构建时间")' in ui
    assert 'AddLabel(versionGrid, 3, "更新通道")' in ui
    assert 'AddLabel(versionGrid, 4, "构建任务")' in ui
    assert 'AddLabel(versionGrid, 5, "安装目录")' in ui
    assert 'CreateButton("手动检查更新", true)' in ui
    assert 'CreateButton("下载并安装", false)' in ui
    assert "BotUpdateService.CheckNowAsync(true)" in ui
    assert "BotUpdateService.ShowUpdatePrompt" in ui
