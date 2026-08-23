from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_startup_language_repair_is_non_destructive_while_qianniu_is_running():
    bootstrap = read("src/Bot/StartUp/BootStrap.cs")

    assert "LanguageStartupSafetyGate.CheckAndRepairLanguageSafely" in bootstrap
    assert "ActiveResourceHasCurrentMarkers" in bootstrap
    assert "TryGetActiveResourceZip" in bootstrap
    assert "运行中的千牛资源已是当前版本，跳过自动修复，不关闭WebView" in bootstrap
    assert "为保护登录态，本次启动不关闭WebView、不清缓存、不覆盖资源" in bootstrap

    gate = bootstrap[bootstrap.index("internal static class LanguageStartupSafetyGate"):]
    not_running = gate.index("if (!IsQianniuRunning())")
    legacy_repair = gate.index("LanguageRepairService.CheckAndRepairLanguage()")
    running_check = gate.index("TryGetActiveResourceZip")
    assert not_running < legacy_repair < running_check
    assert "KillWorkbenchProcesses" not in gate
    assert "StopQianniu" not in gate


def test_injection_self_heal_stays_alive_in_degraded_mode_without_restarting_qianniu():
    repair = read("src/Bot/Update/BotUpdateStartupConnection.Fast.cs")

    assert "RunDegradedRecoveryAsync" in repair
    assert "DegradedRetryDelay = TimeSpan.FromSeconds(30)" in repair
    degraded = repair[repair.index("private static async Task RunDegradedRecoveryAsync"):]
    assert "while (true)" in degraded
    assert "WebSocketSessionCount > 0" in degraded
    assert "MyWebSocketServer.WSocketSvrInst.Start();" in degraded
    assert "未自动重启千牛" in degraded
    assert "QianniuRecoveryManager.RequestRecover" not in repair
    assert "Process.Kill" not in repair
    assert "KillQianniu" not in repair


def test_desk_scanner_rejects_login_and_workbench_shells_and_selects_one_desk_per_seller():
    finder = read("src/Bot/Automation/ChatDeskNs/Automators/QnAccountFinder.cs")

    assert "IsNonReceptionWorkbenchTitle" in finder
    assert 'value.IndexOf("千牛登录"' in finder
    assert 'value.Equals("千牛工作台"' in finder
    assert 'value.EndsWith("-千牛工作台"' in finder

    generic = finder[finder.index("public static bool IsGenericReceptionTitle"):finder.index("public static bool IsSystemNotificationTitle")]
    assert 'value.Equals("千牛工作台"' not in generic

    candidate = finder[finder.index("private static bool IsReceptionCandidate"):finder.index("private static int GetReceptionCandidateCount")]
    assert "IsNonReceptionWorkbenchTitle(title)" in candidate
    assert 'title.IndexOf("千牛"' not in candidate

    open_windows = finder[finder.index("public virtual IList<QnChatWnd> GetOpenChatWnds"):finder.index("private static int GetReceptionEvidenceScore")]
    assert ".GroupBy(candidate => candidate.Seller" in open_windows
    assert ".OrderByDescending(candidate => candidate.Score)" in open_windows
    assert "GetReceptionEvidenceScore" in open_windows


def test_imsdk_production_logging_is_sanitized_and_verbose_trace_is_opt_in():
    log = read("src/BotLib/Log.cs")

    assert 'ImsdkVerboseTraceEnvironmentKey = "QNBOT_IMSDK_VERBOSE_TRACE"' in log
    assert "NormalizeProductionDiagnostic" in log
    assert "IMSDK API扫描摘要" in log
    assert "IMSDK调用追踪摘要" in log
    assert "elapsed < 2000" in log
    assert "return null;" in log
    normalizer = log[log.index("internal static string NormalizeProductionDiagnostic"):log.index("private static bool IsImsdkVerboseTraceEnabled")]
    assert 'payload["targetId"]' not in normalizer
    assert 'payload["ccode"]' not in normalizer
    assert 'payload["conversation"]' not in normalizer

    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    assert 'wMsg.Type == "imsdkApiScan"' in server
    assert 'wMsg.Type == "imsdkInvokeTrace"' in server
