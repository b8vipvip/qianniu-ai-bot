from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
NATIVE = ROOT / "src" / "Bot" / "ChromeNs" / "QNRpa.NativeSend.cs"
QNRPA = ROOT / "src" / "Bot" / "ChromeNs" / "QNRpa.cs"


def _native() -> str:
    return NATIVE.read_text(encoding="utf-8-sig")


def _qnrpa() -> str:
    return QNRPA.read_text(encoding="utf-8-sig")


def test_qianniu_helper_pid_is_only_trusted_under_exact_verified_seller_root():
    text = _native()

    # The seller desk root remains the trust anchor. A helper PID can only be used after the
    # verified root HWND still belongs to the bound seller process and the point target resolves
    # back to that exact root. This prevents the historical regression without permitting an
    # arbitrary cross-process coordinate send.
    assert "GetWindowThreadProcessId(expectedRoot, out rootPid)" in text
    assert "rootPid == 0 || rootPid != expectedPid" in text
    assert "var root = GetAncestor(target, GaRoot);" in text
    assert "if (root != expectedRoot)" in text
    assert "if (targetPid != expectedPid)" in text
    assert "HWND安全发送已验证千牛辅助进程子窗口" in text

    root_owner = text.index("GetWindowThreadProcessId(expectedRoot, out rootPid)")
    root_match = text.index("if (root != expectedRoot)", root_owner)
    helper_accept = text.index("if (targetPid != expectedPid)", root_match)
    post_message = text.index("PostMessage(target, WmLButtonDown", helper_accept)
    assert root_owner < root_match < helper_accept < post_message


def test_safe_uia_main_action_runs_before_legacy_physical_coordinate_fallback():
    native = _native()
    qnrpa = _qnrpa()

    # AccessDenied from FlaUI Mouse.Click must not be required before trying the proven UIA
    # left/main action. The native pipeline invokes the safe child before entering the legacy
    # physical-coordinate path.
    hwnd = native.index("TryPostSafeMainSendMouseMessage")
    safe_uia = native.index("TryInvokeCachedSendButtonNow", hwnd)
    legacy_uia = native.index("TrySendTextViaUiaAsync(buyer, text, sendStart)", safe_uia)
    assert hwnd < safe_uia < legacy_uia
    assert "发送按钮左侧UIA安全调用（原生前置）" in native

    # Preserve the split-button safety invariant: never restore direct Invoke on the cached whole
    # send button. Only the previously verified left/main child is allowed to invoke.
    assert "_sendMessageButton.AsButton().Invoke()" not in qnrpa
    assert "TryInvokeSafeMainSendCandidate" in qnrpa
    assert "almostWholeSplit" in qnrpa
    assert "protectedArrowStart" in qnrpa


def test_every_new_prephysical_send_transition_revalidates_owned_draft_and_echo():
    text = _native()

    assert "安全UIA回退前确认" in text
    assert "安全UIA调用延迟确认" in text
    assert "物理/UIA兼容回退前确认" in text
    assert text.count("HasExpectedDraftFastAsync(text") >= 6
    assert "WaitForTextSendConfirmedAsync" in text
