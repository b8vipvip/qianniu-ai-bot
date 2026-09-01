from pathlib import Path


def read(path):
    return Path(path).read_text(encoding="utf-8-sig")


def write(path, text):
    Path(path).write_text(text, encoding="utf-8")


def replace_once(text, old, new, label):
    if old not in text:
        raise SystemExit("missing patch anchor: " + label)
    return text.replace(old, new, 1)


# Preserve the established safety invariant: never invoke the whole cached split-button element.
qnrpa_path = "src/Bot/ChromeNs/QNRpa.cs"
qnrpa = read(qnrpa_path)
start = qnrpa.index("        private bool TryInvokeExactVerifiedSendButtonNow()")
end = qnrpa.index("        private bool TryInvokeCachedSendButtonNow()", start)
qnrpa = qnrpa[:start] + qnrpa[end:]
qnrpa = replace_once(
    qnrpa,
    "                if (TryInvokeExactVerifiedSendButtonNow()) return true;\n",
    "",
    "remove exact whole-button invoke",
)
qnrpa = replace_once(
    qnrpa,
    """        internal bool IsKnownBotOwnedDraftText(string currentText)
        {
            var expected = (LastSetPlainText ?? string.Empty).Trim();
            return expected.Length > 0 && EditorMatchesExpectedText(currentText, expected);
        }
""",
    """        internal bool IsKnownBotOwnedDraftText(string currentText)
        {
            var expected = (LastSetPlainText ?? string.Empty).Trim();
            return expected.Length > 0
                && !string.IsNullOrWhiteSpace(LastSendFailureReason)
                && !LastSendWasCancelled
                && EditorMatchesExpectedText(currentText, expected);
        }
""",
    "failed draft ownership must require a send failure",
)
write(qnrpa_path, qnrpa)


# The field log shows WindowFromPoint can land on a different root HWND even though the safe point
# belongs to Qianniu. Trust process ownership, not top-level HWND identity: a verified safe point
# may be hosted by another top-level/owned Qt window in the same AliWorkbench process.
native_path = "src/Bot/ChromeNs/QNRpa.NativeSend.cs"
native = read(native_path)
old = """            var target = WindowFromPoint(screenPoint);
            if (target == IntPtr.Zero) return false;
            var root = GetAncestor(target, GaRoot);
            var expectedRoot = new IntPtr(desk.Hwnd.Handle);
            if (root == IntPtr.Zero) root = target;
            if (root != expectedRoot)
            {
                Log.Info("HWND安全发送已阻止：安全点不属于当前卖家千牛根窗口: seller=" + SellerNick
                    + ", expectedRoot=" + expectedRoot + ", actualRoot=" + root);
                return false;
            }
"""
new = """            var target = WindowFromPoint(screenPoint);
            if (target == IntPtr.Zero) return false;

            uint targetPid;
            GetWindowThreadProcessId(target, out targetPid);
            var expectedPid = unchecked((uint)desk.ProcessId);
            if (targetPid == 0 || targetPid != expectedPid)
            {
                var rejectedRoot = GetAncestor(target, GaRoot);
                if (rejectedRoot == IntPtr.Zero) rejectedRoot = target;
                Log.Info("HWND安全发送已阻止：安全点窗口不属于当前卖家千牛进程: seller=" + SellerNick
                    + ", expectedPid=" + expectedPid + ", actualPid=" + targetPid
                    + ", actualRoot=" + rejectedRoot);
                return false;
            }

            var root = GetAncestor(target, GaRoot);
            var expectedRoot = new IntPtr(desk.Hwnd.Handle);
            if (root == IntPtr.Zero) root = target;
            if (root != expectedRoot)
            {
                Log.Info("HWND安全发送允许同一千牛进程的独立根窗口: seller=" + SellerNick
                    + ", pid=" + targetPid + ", expectedRoot=" + expectedRoot + ", actualRoot=" + root);
            }
"""
native = replace_once(native, old, new, "process-owned HWND safe point")
write(native_path, native)


# Update regression coverage to match the production safety model.
test_path = "tests/test_runtime_send_failure_1088_static.py"
test = read(test_path)
old = """def test_exact_verified_send_button_invoke_is_safe_and_non_physical():
    source = read("src/Bot/ChromeNs/QNRpa.cs")
    assert "private bool TryInvokeExactVerifiedSendButtonNow()" in source
    block = source.split("private bool TryInvokeExactVerifiedSendButtonNow()", 1)[1].split(
        "private bool TryInvokeCachedSendButtonNow()", 1
    )[0]
    assert "SendButtonAutomationId" in block
    assert "IsSendButtonName(name)" in block
    assert "_sendMessageButton.AsButton().Invoke()" in block
    assert "arrow" in block
    assert "dropdown" in block
    assert "下拉" in block
    fallback = source.split("private bool TryInvokeCachedSendButtonNow()", 1)[1].split(
        "private bool TryInvokeSafeMainSendCandidate", 1
    )[0]
    assert "if (TryInvokeExactVerifiedSendButtonNow()) return true;" in fallback
"""
new = """def test_hwnd_safe_send_accepts_only_process_owned_verified_point():
    source = read("src/Bot/ChromeNs/QNRpa.cs")
    native = read("src/Bot/ChromeNs/QNRpa.NativeSend.cs")
    assert "_sendMessageButton.AsButton().Invoke()" not in source
    assert "GetWindowThreadProcessId(target, out targetPid)" in native
    assert "targetPid != expectedPid" in native
    assert "安全点窗口不属于当前卖家千牛进程" in native
    assert "允许同一千牛进程的独立根窗口" in native
    process_guard = native.split("GetWindowThreadProcessId(target, out targetPid)", 1)[1].split(
        "ScreenToClient", 1
    )[0]
    assert process_guard.index("targetPid != expectedPid") < process_guard.index("root != expectedRoot")
    assert "return false;" in process_guard.split("targetPid != expectedPid", 1)[1].split("var root", 1)[0]
"""
test = replace_once(test, old, new, "runtime send regression test")
test = replace_once(
    test,
    '    assert "EditorMatchesExpectedText(currentText, expected)" in rpa\n',
    '    assert "EditorMatchesExpectedText(currentText, expected)" in rpa\n    assert "!string.IsNullOrWhiteSpace(LastSendFailureReason)" in rpa\n    assert "!LastSendWasCancelled" in rpa\n',
    "failed draft regression conditions",
)
write(test_path, test)
