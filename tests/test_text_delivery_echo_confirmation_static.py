from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
RPA = ROOT / "src" / "Bot" / "ChromeNs" / "QNRpa.cs"


def _source() -> str:
    return RPA.read_text(encoding="utf-8-sig")


def test_text_send_success_requires_seller_echo():
    source = _source()
    start = source.index("private async Task<bool> WaitForTextSendConfirmedAsync")
    end = source.index("private bool TryClickCachedSendButtonNow", start)
    block = source[start:end]

    assert "HasRecentSellerEcho(buyer, text, sendStart)" in block
    assert "卖家消息已回显" in block
    assert "draftClearedObserved = true" in block
    assert "继续等待同买家同文本卖家回显" in block

    # Composer clearance is only action evidence and must never finalize delivery success.
    cleared = block.index("if (probe.IsEmpty && !draftClearedObserved)")
    tail = block[cleared:]
    assert "return true;" not in tail.split("SetSendFailure", 1)[0]


def test_late_echo_window_is_extended_before_retry():
    source = _source()
    start = source.index("private async Task<bool> WaitForTextSendConfirmedAsync")
    end = source.index("private bool TryClickCachedSendButtonNow", start)
    block = source[start:end]

    assert "DateTime.Now.AddMilliseconds(4500)" in block
    assert "if (extendedEnd > end) end = extendedEnd;" in block
    assert "未检测到卖家消息回显" in block


def test_text_send_no_longer_records_input_clear_as_success():
    source = _source()
    start = source.index("private async Task<bool> WaitForTextSendConfirmedAsync")
    end = source.index("private bool TryClickCachedSendButtonNow", start)
    block = source[start:end]

    assert 'RecordSendAttempt(true, method + "，输入框已清空")' not in block
    assert '发送确认成功：输入框已清空' not in block
