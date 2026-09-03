from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


# Production invariant: the exact Qianniu service-attitude continuation is allowed only for the
# still-verified target buyer and a unique exact "继续发送" UIA action. Ambiguity remains terminal.
def test_service_attitude_prompt_auto_continues_only_after_buyer_and_unique_button_proof():
    guard = read("src/Bot/ChromeNs/QNRpa.PlatformSendGuard.cs")
    assert "服务态度提醒" in guard
    assert "继续发送" in guard
    assert "服务态度提醒继续发送前会话确认" in guard
    assert "VerifyCurrentBuyerWithoutNavigationAsync" in guard
    assert "continueButtons.Count != 1" in guard
    assert "result.ContinueButton.AsButton().Invoke()" in guard
    assert "千牛服务态度提醒已自动点击“继续发送”" in guard

    # Ambiguous/missing button and buyer mismatch must still fail closed.
    assert 'SetSendCancellation("平台发送拦截"' in guard
    assert "无法安全自动确认，已停止本次发送且禁止盲目重试" in guard
    assert "Bot不会点击“继续发送”" not in guard

    # Side-effectful continuation must be single-flight and fully awaited. Never race an Invoke
    # worker against a timeout because the abandoned worker could click after the caller failed.
    assert "_serviceAttitudeProbeGate.WaitAsync(0)" in guard
    assert "InvokeServiceAttitudeContinue(detected)" in guard
    assert "Task.WhenAny(action" not in guard
    assert "PlatformSendBlockProbeTimeoutMs" not in guard


def test_native_send_uses_submission_guard_between_authoritative_actions():
    native = read("src/Bot/ChromeNs/QNRpa.NativeSend.cs")
    guard = read("src/Bot/ChromeNs/QNRpa.PlatformSendGuard.cs")

    first = native.index('StopIfPlatformSendBlockedAsync(buyer, "发送前")')
    cdp = native.index("TryTriggerSendViaCdpDomAsync", first)
    cdp_confirm = native.index('buyer, text, sendStart, "CDP页面发送按钮", 1700', cdp)
    hwnd = native.index("TryPostSafeMainSendMouseMessage", cdp_confirm)
    hwnd_confirm = native.index('buyer, text, sendStart, "发送按钮HWND安全消息", 1800', hwnd)
    safe_uia = native.index("TryInvokeCachedSendButtonNow", hwnd_confirm)
    uia_confirm = native.index('buyer, text, sendStart, "发送按钮左侧UIA安全调用（原生前置）", 1800', safe_uia)
    legacy_uia = native.index("TrySendTextViaUiaAsync", uia_confirm)
    after_uia = native.index('StopIfPlatformSendBlockedAsync(buyer, "UIA发送后")', legacy_uia)
    assert first < cdp < cdp_confirm < hwnd < hwnd_confirm < safe_uia < uia_confirm < legacy_uia < after_uia

    # Stable-empty acceptance still cannot bypass a visible reminder: the stable boundary performs
    # one serialized/final platform check and then revalidates the buyer before recording submission.
    stable_probe = guard.index('buyer, method + "稳定清空后平台确认"')
    buyer_check = guard.index('buyer, method + "提交后会话确认"', stable_probe)
    watchdog = guard.index("SendDeliveryWatchdog.MarkSubmissionAccepted", buyer_check)
    success = guard.index("发送提交确认成功", watchdog)
    assert stable_probe < buyer_check < watchdog < success

    # No longer restore the 1.1.1189 eight-pass late scanner that piled up UIA traversals and delayed
    # the next order segment. Exactly one delayed single-flight safety check is retained.
    assert "Task.Delay(650)" in guard
    assert "迟到服务态度提醒单次监控" in guard
    assert "for (var attempt = 0; attempt < 8; attempt++)" not in guard


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
