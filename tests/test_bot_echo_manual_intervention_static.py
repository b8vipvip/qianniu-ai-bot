from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_explicit_bot_echo_is_checked_before_manual_intervention():
    source = text("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")

    marker_check = source.index("texts.FirstOrDefault(IsExplicitBotAuthoredReply)")
    cancel_generation = source.index("CancelActiveBuyerGeneration")
    mark_manual = source.index("ResponseProgressTracker.MarkManualIntervention")

    assert marker_check < cancel_generation
    assert marker_check < mark_manual
    assert "ResponseProgressTracker.MarkDeliveryConfirmed" in source
    assert "卖家消息多字段命中Bot署名，未判定人工介入" in source


def test_bot_marker_fallback_accepts_supported_bracket_styles_only_at_message_end():
    source = text("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")

    assert 'EndsWith("[AI]", StringComparison.OrdinalIgnoreCase)' in source
    assert 'EndsWith("【AI】", StringComparison.OrdinalIgnoreCase)' in source
    assert 'EndsWith("［AI］", StringComparison.OrdinalIgnoreCase)' in source
    assert "char.IsWhiteSpace" in source
    assert "compact.EndsWith" in source


def test_seller_echo_checks_original_text_and_summary_before_manual_intervention():
    source = text("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")

    extract = source.index("ExtractMessageTextCandidates(message)")
    delivery = source.index("TryConfirmBotDelivery(seller, buyer, texts)")
    marker = source.index("texts.FirstOrDefault(IsExplicitBotAuthoredReply)")
    cancel = source.index("CancelActiveBuyerGeneration")

    assert extract < delivery < marker < cancel
    assert "message.originalData.text" in source
    assert "message.summary" in source
    assert "Distinct(StringComparer.Ordinal)" in source
    assert "foreach (var candidate in texts)" in source
    assert "原始正文有时会去掉末尾 [AI]" in source
