from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_off_hours_ui_moved_into_auto_reply_rules_and_forces_fixed_mode():
    ui = read("src/Bot/Options/FeatureSettingsOptionsControl.cs")

    organize = ui.index("private void OrganizeAutoReplyRulesPage()")
    remove_legacy = ui.index("private void RemoveLegacyOffHoursFromNotificationPage()")
    assert 'MakeSectionTitle("首条咨询固定回复")' in ui[organize:remove_legacy]
    assert 'MakeSectionTitle("下班自动回复")' in ui[organize:remove_legacy]
    assert 'Content = "启用下班自动回复"' in ui
    assert 'const string fixedMode = "固定预设答案"' in ui
    assert 'value == "人工客服工作时间与下班回复"' in ui
    assert 'value == "转人工通知"' in ui


def test_first_inquiry_and_off_hours_share_legacy_rule_stack_layout():
    ui = read("src/Bot/Options/FeatureSettingsOptionsControl.cs")

    assert "FindPrimaryRuleStack(legacyContent)" in ui
    assert "legacyStack.Children.Insert(0, inserted[i])" in ui
    assert 'Text = "固定答案"' not in ui  # labels are created through the shared row helper
    assert 'MakeLabeledControl(\n                "固定答案"' in ui


def test_attached_bot_tracking_never_hides_controls_before_geometry_move():
    perf = read("src/Bot/AssistWindow/WndAssist.AttachedPerformance.cs")

    assert "SetRightPanelPositionWithoutVisibilityToggle" in perf
    assert "Canvas.SetLeft" in perf
    assert "Canvas.SetTop" in perf
    assert "HasMeaningfulPositionChange" in perf
    assert "MoveUIElement(" not in perf
    assert "Topmost = true" not in perf
    assert "SwpNoActivate" in perf
    assert "SwpNoSendChanging" in perf


def test_periodic_tracking_does_not_raise_z_order():
    perf = read("src/Bot/AssistWindow/WndAssist.AttachedPerformance.cs")

    assert "wnd.SafeTrackGeometry(false, true)" in perf
    assert "if (!periodic && Desk.IsForeground)" in perf
    assert "if (!Desk.IsForeground) return;" in perf
    assert "TimeSpan.FromMilliseconds(150)" in perf
