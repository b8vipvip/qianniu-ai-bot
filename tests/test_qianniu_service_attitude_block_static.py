from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_service_attitude_prompt_is_a_terminal_platform_block_not_an_auto_confirm():
    guard = read("src/Bot/ChromeNs/QNRpa.PlatformSendGuard.cs")
    assert "服务态度提醒" in guard
    assert "继续发送" in guard
    assert 'SetSendCancellation("平台发送拦截"' in guard
    assert "Bot禁止自动确认" in guard
    assert ".Click(" not in guard
    assert ".Invoke(" not in guard


def test_native_send_checks_platform_block_between_every_physical_send_fallback():
    native = read("src/Bot/ChromeNs/QNRpa.NativeSend.cs")
    first = native.index('StopIfPlatformSendBlockedAsync(buyer, "发送前")')
    cdp = native.index("TryTriggerSendViaCdpDomAsync", first)
    after_cdp = native.index('StopIfPlatformSendBlockedAsync(buyer, "CDP页面发送按钮后")', cdp)
    hwnd = native.index("TryPostSafeMainSendMouseMessage", after_cdp)
    after_hwnd = native.index('StopIfPlatformSendBlockedAsync(buyer, "HWND安全消息后")', hwnd)
    uia = native.index("TrySendTextViaUiaAsync", after_hwnd)
    after_uia = native.index('StopIfPlatformSendBlockedAsync(buyer, "UIA发送后")', uia)
    assert first < cdp < after_cdp < hwnd < after_hwnd < uia < after_uia


def test_hwnd_sender_never_clicks_a_modal_or_other_sibling_root_window():
    native = read("src/Bot/ChromeNs/QNRpa.NativeSend.cs")
    block = native[native.index("if (root != expectedRoot)"):][:700]
    assert "拒绝向未知根窗口投递点击" in block
    assert "return false;" in block
    assert "允许同一千牛进程的独立根窗口" not in block
    qn = read("src/Bot/ChromeNs/QN.cs")
    assert "if (!ok && rpa.LastSendWasCancelled)" in qn
    assert "禁止重试" in qn
