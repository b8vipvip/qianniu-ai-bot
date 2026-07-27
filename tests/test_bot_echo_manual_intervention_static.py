from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_explicit_bot_echo_is_checked_before_manual_intervention():
    source = text("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")

    marker_check = source.index("IsExplicitBotAuthoredReply(text)")
    cancel_generation = source.index("CancelActiveBuyerGeneration")
    mark_manual = source.index("ResponseProgressTracker.MarkManualIntervention")

    assert marker_check < cancel_generation
    assert marker_check < mark_manual
    assert "ResponseProgressTracker.MarkDeliveryConfirmed" in source
    assert "卖家消息带Bot署名标记，未判定人工介入" in source


def test_bot_marker_fallback_accepts_supported_bracket_styles_only_at_message_end():
    source = text("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")

    assert 'EndsWith("[AI]", StringComparison.OrdinalIgnoreCase)' in source
    assert 'EndsWith("【AI】", StringComparison.OrdinalIgnoreCase)' in source
    assert 'EndsWith("［AI］", StringComparison.OrdinalIgnoreCase)' in source
    assert "char.IsWhiteSpace" in source
    assert "compact.EndsWith" in source
