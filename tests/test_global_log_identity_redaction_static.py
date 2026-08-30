from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_central_log_boundary_redacts_common_runtime_identity_fields():
    log = read("src/BotLib/Log.cs")

    assert "using System.Text.RegularExpressions;" in log
    assert "RuntimeIdentityFieldRegex" in log
    assert "RedactRuntimeIdentityFields" in log
    assert "StableIdentityRef" in log
    assert "IdentityKindForLabel" in log

    for label in (
        "sellerNick",
        "buyerNick",
        "loginNick",
        "conversationNick",
        "ignoredSession",
        "fromSession",
        "toSession",
        "seller",
        "buyer",
        "session",
        "客服",
        "买家",
    ):
        assert label in log

    assert 'return label + "Ref=" + StableIdentityRef' in log
    assert 'return "session";' in log
    assert 'return "buyer";' in log
    assert 'return "seller";' in log


def test_all_normal_runtime_log_levels_pass_through_identity_redaction():
    log = read("src/BotLib/Log.cs")

    error_object_start = log.index("public static void Error(string msg, object o")
    error_plain_start = log.index(
        "public static void Error(string msg, [System.Runtime.CompilerServices.CallerMemberName]",
        error_object_start + 1,
    )
    error_object = log[error_object_start:error_plain_start]
    assert "RedactRuntimeIdentityFields(GetDesc" in error_object

    error_plain = log[error_plain_start:log.index("public static void ErrorWithMaxCount", error_plain_start)]
    assert "RedactRuntimeIdentityFields(GetDesc" in error_plain

    error_bounded = log[log.index("public static void ErrorWithMaxCount"):log.index("private static bool IsLogCountLessThanMaxCount")]
    assert "RedactRuntimeIdentityFields(GetDesc" in error_bounded

    exception = log[log.index("public static void Exception"):log.index("public static void Info")]
    assert "RedactRuntimeIdentityFields(GetDesc" in exception

    info = log[log.index("public static void Info"):log.index("internal static string NormalizeProductionDiagnostic")]
    assert "NormalizeProductionDiagnostic(text)" in info
    assert "RedactRuntimeIdentityFields(text)" in info
    assert info.index("NormalizeProductionDiagnostic(text)") < info.index("RedactRuntimeIdentityFields(text)")

    debug = log[log.index("public static void Debug"):log.index("public static void Show")]
    assert "RedactRuntimeIdentityFields(text)" in debug

    write_line = log[log.index("public static void WriteLine"):log.index("public static void StackTrace")]
    assert "RedactRuntimeIdentityFields(string.Format(format, args))" in write_line


def test_existing_raw_source_fields_are_protected_without_changing_runtime_ids():
    log = read("src/BotLib/Log.cs")
    burst = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")

    # Existing business code may still build diagnostic strings with raw values. The central Log
    # boundary owns persistence redaction, so business keys and in-memory seller/buyer IDs remain
    # untouched for routing, dedupe and local UI behavior.
    assert "seller=" in burst
    assert "Seller = seller" in server
    assert "Buyer = buyer" in server
    assert "RuntimeIdentityFieldRegex.Replace" in log
