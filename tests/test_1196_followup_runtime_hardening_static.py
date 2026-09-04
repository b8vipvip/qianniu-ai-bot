from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_unknown_composer_text_is_never_deleted_and_mutation_has_no_abandoned_timeout():
    q = read("src/Bot/ChromeNs/QNRpa.cs")
    method = q[q.index("ClearStaleComposerBeforeNewDraftAsync"):q.index("TrySetPlainTextByCdpAsync")]
    assert "IsOwnedDraftForBuyer(buyer, observedText)" in method
    assert "输入框存在所有权无法证明的内容，已保留" in method
    assert "RunUiMutationAsync" in method
    helper = q[q.index("private async Task<bool> RunUiMutationAsync"):q.index("private async Task<bool> HasExpectedDraftFastAsync")]
    assert "Task.WhenAny" not in helper
    assert "Task.Delay" not in helper


def test_order_sku_uses_raw_structured_parser_and_bounded_retry_window():
    v2 = read("src/Bot/ChromeNs/OrderTemplateRequiredFieldsV2.cs")
    legacy = read("src/Bot/Options/LegacyAboutUpdateRedirect.cs")
    assert "internal static string ResolveSkuTextFromPayload(string raw)" in legacy
    assert "SkuText = OrderSkuPayloadRecoveryBridge.ResolveSkuTextFromPayload(raw)" in v2
    assert "new[] { 0, 250, 500, 1000, 1500 }" in v2
    assert "new[] { 0, 500, 1000, 2000, 3000, 5000, 7000 }" not in v2


def test_exact_duplicate_cdp_payloads_stay_suppressed_across_recovery_cadences():
    bridge = read("src/Bot/ChromeNs/DuplicateCdpInboundRecoveryBridge.cs")
    assert "InboundFingerprintWindow = TimeSpan.FromMinutes(2)" in bridge
    assert "InboundFingerprintRetention = TimeSpan.FromMinutes(5)" in bridge
    assert "BuildInboundFingerprint(seller, type, response)" in bridge
    assert "+ (response ?? string.Empty)" in bridge
