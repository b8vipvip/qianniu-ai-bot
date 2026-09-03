from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


# Production invariant: a Qianniu policy confirmation is never an automatic retry surface.
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
    safe_uia = native.index("TryInvokeCachedSendButtonNow", after_hwnd)
    after_safe_uia = native.index('StopIfPlatformSendBlockedAsync(buyer, "安全UIA调用后")', safe_uia)
    uia = native.index("TrySendTextViaUiaAsync", after_safe_uia)
    after_uia = native.index('StopIfPlatformSendBlockedAsync(buyer, "UIA发送后")', uia)
    assert first < cdp < after_cdp < hwnd < after_hwnd < safe_uia < after_safe_uia < uia < after_uia


def test_hwnd_sender_never_clicks_a_modal_or_other_sibling_root_window():
    native = read("src/Bot/ChromeNs/QNRpa.NativeSend.cs")
    block = native[native.index("if (root != expectedRoot)"):][:900]
    assert "安全点不属于当前已验证卖家根窗口" in block
    assert "HWND安全发送已阻止跨根窗口点击" in block
    assert "return false;" in block
    # Helper processes are acceptable only under the exact seller root. A sibling/modal root must
    # still fail before targetPid helper handling and before any PostMessage call.
    helper = native.index("if (targetPid != expectedPid)", native.index("if (root != expectedRoot)"))
    post = native.index("PostMessage(target, WmLButtonDown", helper)
    assert native.index("if (root != expectedRoot)") < helper < post
    assert "允许同一千牛进程的独立根窗口" not in block
    qn = read("src/Bot/ChromeNs/QN.cs")
    assert "if (!ok && rpa.LastSendWasCancelled)" in qn
    assert "禁止重试" in qn