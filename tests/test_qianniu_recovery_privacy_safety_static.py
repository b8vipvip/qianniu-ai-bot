from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_destructive_qianniu_recovery_is_explicit_opt_in_only():
    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")

    assert 'DestructiveRecoveryEnvironmentKey = "QNBOT_ALLOW_DESTRUCTIVE_QIANNIU_RECOVERY"' in server
    assert "IsDestructiveRecoveryExplicitlyEnabled" in server
    request = server[server.index("public static void RequestRecover"):server.index("private static async Task RecoverAsync")]
    assert "if (!IsDestructiveRecoveryExplicitlyEnabled())" in request
    assert "已阻止旧版自动杀进程/重启千牛恢复" in request
    assert "return;" in request
    assert "await RecoverAsync(reason);" in request

    # The destructive primitives can remain as a consciously enabled emergency fallback, but the
    # default RequestRecover path must return before scheduling RecoverAsync/KillQianniuProcesses.
    guard_pos = request.index("if (!IsDestructiveRecoveryExplicitlyEnabled())")
    return_pos = request.index("return;", guard_pos)
    schedule_pos = request.index("await RecoverAsync(reason);")
    assert guard_pos < return_pos < schedule_pos


def test_websocket_and_recovery_logs_use_stable_refs_for_runtime_identities():
    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")

    assert "internal static string DiagnosticRef" in server
    assert 'DiagnosticRef("seller", sellerNick)' in server
    assert 'DiagnosticRef("session", sessionId)' in server
    assert 'DiagnosticRef("buyer", buyerNick)' in server
    assert 'MyWebSocketServer.DiagnosticRef("seller", QN.CurQN.Seller.Nick)' in server
    assert 'MyWebSocketServer.DiagnosticRef("buyer", lastBuyer)' in server

    # Keep seller/buyer values in the in-memory diagnostic snapshot because local UI and recovery
    # need them, while preventing those raw values from being appended to ordinary runtime logs.
    assert "Seller = seller" in server
    assert "Buyer = buyer" in server
    assert 'Log.Info("千牛CDP初始化成功, seller=" + qn.Seller.Nick' not in server
    assert 'Log.Info("千牛自动恢复成功：客服=" + QN.CurQN.Seller.Nick)' not in server
    assert 'Log.Info("已尝试恢复打开最近买家会话：" + lastBuyer)' not in server


def test_qnbot_status_raw_payload_still_flows_only_through_central_log_sanitizer():
    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    log = read("src/BotLib/Log.cs")

    assert 'Log.Info("千牛注入状态: " + wMsg.Response)' in server
    assert 'text.IndexOf("千牛注入状态:"' in log
    assert "return NormalizeInjectionStatus(text);" in log
    assert 'sellerPresent=' in log
    assert 'buyerPresent=' in log
