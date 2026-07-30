from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "Bot" / "Options" / "BotUpdateOptionsControl.cs"


def text() -> str:
    return SOURCE.read_text(encoding="utf-8-sig")


def test_all_about_update_actions_use_shared_button_factory():
    source = text()

    for label in (
        "手动检查更新",
        "下载并安装",
        "查看发布页面",
        "打开安装目录",
        "取消跳过版本",
    ):
        assert f'CreateButton("{label}"' in source


def test_secondary_buttons_have_explicit_theme_independent_colors():
    source = text()
    method = source.split("private static Button CreateButton", 1)[1]

    assert "Color.FromRgb(248, 250, 252)" in method
    assert "Color.FromRgb(15, 23, 42)" in method
    assert "Color.FromRgb(148, 163, 184)" in method
    assert "Background = background" in method
    assert "Foreground = foreground" in method
    assert "BorderBrush = border" in method
    assert "BorderThickness = new Thickness(1)" in method

    # The previous implementation inherited null brushes from the global theme,
    # which made secondary actions disappear on a white card.
    assert "Background = primary ?" not in method
    assert "Foreground = primary ? Brushes.White : null" not in method
    assert "BorderBrush = primary ?" not in method


def test_disabled_install_button_remains_visibly_distinguishable():
    source = text()
    method = source.split("private static Button CreateButton", 1)[1]

    assert "button.IsEnabledChanged" in method
    assert "button.IsEnabled ? 1.0 : 0.62" in method
    assert "Opacity = 1.0" in method
