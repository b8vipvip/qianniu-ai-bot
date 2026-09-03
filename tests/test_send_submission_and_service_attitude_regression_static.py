from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
NATIVE = ROOT / "src" / "Bot" / "ChromeNs" / "QNRpa.NativeSend.cs"
PLATFORM = ROOT / "src" / "Bot" / "ChromeNs" / "QNRpa.PlatformSendGuard.cs"
WATCHDOG = ROOT / "src" / "Bot" / "ChromeNs" / "SendDeliveryWatchdog.cs"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def test_native_send_accepts_verified_submission_instead_of_echo_only_retry():
    native = read(NATIVE)
    platform = read(PLATFORM)

    # Every authoritative native action must use the submission-aware confirmation path.
    assert "WaitForTextSubmissionAcceptedAsync" in native
    assert '"CDP页面发送按钮", 1700' in native
    assert '"发送按钮HWND安全消息", 1800' in native
    assert '"发送按钮左侧UIA安全调用（原生前置）", 1800' in native

    # The old echo-only waits were the production duplicate-send trigger: the composer had
    # already cleared, but missing seller echo returned false and caused the same text to be
    # written/clicked again. Native send must no longer depend on them.
    assert "WaitForTextSendConfirmedAsync" not in native
    assert "禁止重复写入" in native
    assert "禁止因实时回显缺失重新写入同一文本" in platform

    # Submission evidence is accepted only after the composer is empty twice and the target
    # conversation is revalidated without navigation.
    first_empty = platform.index("emptyObserved = true")
    stable_empty = platform.index("稳定清空确认", first_empty)
    buyer_check = platform.index("提交后会话确认", stable_empty)
    watchdog_mark = platform.index("SendDeliveryWatchdog.MarkSubmissionAccepted", buyer_check)
    success = platform.index("发送提交确认成功", watchdog_mark)
    assert first_empty < stable_empty < buyer_check < watchdog_mark < success
    assert "VerifyCurrentBuyerWithoutNavigationAsync" in platform


def test_service_attitude_reminder_auto_continues_only_for_exact_verified_action():
    text = read(PLATFORM)

    assert "服务态度提醒" in text
    assert "继续发送" in text
    assert "continueButtons.Length != 1" in text
    assert "continueButtons[0].AsButton().Invoke()" in text
    assert "千牛服务态度提醒已自动点击“继续发送”" in text

    # Buyer proof must occur before the UIA action, and the late-popup watcher keeps checking
    # after a stable composer submission so a reminder that animates in later is not stranded.
    buyer_check = text.index("服务态度提醒继续发送前会话确认")
    invoke = text.index("continueButtons[0].AsButton().Invoke()")
    assert buyer_check < invoke
    assert "ArmLateServiceAttitudeContinuationWatch" in text
    assert "迟到服务态度提醒监控" in text

    # The legacy policy that deliberately refused this exact continuation must not return.
    assert "Bot不会点击“继续发送”" not in text
    assert "该平台提示必须由人工判断，Bot禁止自动确认" not in text


def test_delivery_watchdog_keeps_echo_as_best_proof_but_never_false_fails_verified_submission():
    watchdog = read(WATCHDOG)

    assert "MarkSubmissionAccepted" in watchdog
    assert "SubmissionAcceptedTicks" in watchdog
    assert "SubmissionEvidence" in watchdog
    assert "Interlocked.Exchange(ref pending.SubmissionAcceptedTicks" in watchdog
    assert "Interlocked.Read(ref pending.SubmissionAcceptedTicks)" in watchdog

    # Real seller echo remains the preferred proof and can remove the pending watchdog first.
    assert "HasRecentSellerEcho" in watchdog
    assert "已通过卖家消息回显确认真实发送" in watchdog

    # If echo is missing after 9 seconds but the send layer already proved Qianniu accepted the
    # exact draft, do not emit a false failure/anomaly and do not make that answer eligible to resend.
    accepted = watchdog.index("if (!delivered && submissionTicks > 0)")
    success = watchdog.index("[本店发送回显缺失但提交已确认]", accepted)
    failure = watchdog.index("if (!delivered)", accepted + 1)
    anomaly = watchdog.index("SendFailureAnomalyService.Queue", failure)
    assert accepted < success < failure < anomaly
    assert "不生成发送失败异常，不触发同文本重发" in watchdog
    assert "KnownBotAnswers[AnswerKey(seller, buyer, answer)]" in watchdog
