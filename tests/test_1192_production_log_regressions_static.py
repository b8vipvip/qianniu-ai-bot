from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
NATIVE = ROOT / "src" / "Bot" / "ChromeNs" / "QNRpa.NativeSend.cs"
PLATFORM = ROOT / "src" / "Bot" / "ChromeNs" / "QNRpa.PlatformSendGuard.cs"
RELIABLE = ROOT / "src" / "Bot" / "ChromeNs" / "QNRpa.ReliableSend.cs"
LOG_WRITER = ROOT / "src" / "BotLib" / "LogWriter.cs"
KNOWLEDGE_V2 = ROOT / "src" / "Bot" / "ChromeNs" / "KnowledgeEngineV2RuntimeBridge.cs"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def test_hwnd_send_can_bypass_foreign_overlay_only_inside_verified_seller_root():
    text = read(NATIVE)

    assert "ChildWindowFromPointEx" in text
    assert "ResolveTargetInsideVerifiedSellerRoot" in text
    assert "HWND安全发送已绕过外部覆盖窗口并在已验证卖家根内重新解析安全点" in text

    root_owner = text.index("GetWindowThreadProcessId(expectedRoot, out rootPid)")
    global_target = text.index("WindowFromPoint(screenPoint)", root_owner)
    constrained = text.index("ResolveTargetInsideVerifiedSellerRoot(expectedRoot, screenPoint)", global_target)
    final_root_reject = text.index("if (root != expectedRoot)", constrained)
    post = text.index("PostMessage(target, WmLButtonDown", final_root_reject)
    assert root_owner < global_target < constrained < final_root_reject < post

    # The recovery never accepts an arbitrary overlay target: the constrained target must still
    # resolve back to the exact seller root before any message is posted.
    assert "constrainedRoot == expectedRoot" in text
    assert "安全点不属于当前已验证卖家根窗口" in text


def test_service_attitude_read_probe_is_bounded_cached_and_cannot_block_send_mainline():
    text = read(PLATFORM)

    call = text.index("GetBoundedServiceAttitudeReadProbeAsync(buyer, stage)")
    action_gate = text.index("_serviceAttitudeProbeGate.WaitAsync(0)", call)
    assert call < action_gate

    assert "PlatformReadProbeTimeoutMs = 650" in text
    assert "_serviceAttitudeReadProbeTask" in text
    assert "_serviceAttitudeReadProbeTask == null || _serviceAttitudeReadProbeTask.IsCompleted" in text
    assert "Task.WhenAny(" in text
    assert "Task.Delay(PlatformReadProbeTimeoutMs)" in text
    assert "已放行发送主链且复用同一后台探测避免UIA堆积" in text

    # Read-only workers may be abandoned safely after the bounded wait, but the side-effectful
    # unique Continue Invoke must still never race a timeout (the 1.1.1189 ghost-click regression).
    invoke = text.index("InvokeServiceAttitudeContinue(detected)")
    assert "Task.WhenAny(action" not in text
    assert "自动点击“继续发送”超时" not in text
    assert invoke > action_gate


def test_stale_answer_cancellation_clears_only_exact_bot_owned_draft():
    text = read(RELIABLE)

    classify = text.index("LastSendWasCancelled = IsNonRetryableStaleAnswer")
    cleanup_call = text.index("ClearCancelledExactBotDraftIfPresent()", classify)
    helper = text.index("private void ClearCancelledExactBotDraftIfPresent()")
    exact_before = text.index("EditorMatchesExpectedText(current, expected)", helper)
    focus = text.index("FocusEditor()", exact_before)
    exact_after = text.index("EditorMatchesExpectedText(focusedCurrent, expected)", focus)
    clear = text.index("PressCtrlA()", exact_after)
    assert classify < cleanup_call < helper < exact_before < focus < exact_after < clear
    assert "LastSetPlainText = string.Empty" in text[helper:]
    assert "避免旧答案滞留千牛输入框" in text


def test_new_process_rotates_previous_active_log_and_only_24h_retention_deletes_archives():
    text = read(LOG_WRITER)

    ctor = text.index("public LoopSaveFile(string fn")
    startup_rotate = text.index("RotatePreviousRunFileAtStartup();", ctor)
    maintenance = text.index("MaintainLogFiles(true);", ctor)
    timer = text.index("new NoReEnterTimer", maintenance)
    assert ctor < startup_rotate < maintenance < timer

    helper = text.index("private void RotatePreviousRunFileAtStartup()")
    assert "RotateCurrentFile();" in text[helper:helper + 900]
    assert "LogRetention = TimeSpan.FromHours(24)" in text
    assert "if (info.LastWriteTimeUtc < cutoffUtc) info.Delete();" in text
    assert "if (string.Equals(path, FileName, StringComparison.OrdinalIgnoreCase)) continue;" in text


def test_knowledge_v2_absolute_age_barrier_precedes_answer_ready():
    text = read(KNOWLEDGE_V2)

    assert "MaxDirectReplyAgeSeconds = 55" in text
    age = text.index("(DateTime.Now - detectedAt).TotalSeconds > MaxDirectReplyAgeSeconds")
    begin = text.index("ResponseProgressTracker.BeginAnswer", age)
    ready = text.index("ResponseProgressTracker.SetAnswerReady", begin)
    assert age < begin < ready
    assert "超过generation绝对年龄，已丢弃且禁止进入Ready/Sending" in text
