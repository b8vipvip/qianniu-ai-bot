from pathlib import Path
import re


def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{label}: expected exactly one match, got {count}")
    return text.replace(old, new, 1)


def sub_once(text, pattern, repl, label, flags=0):
    out, count = re.subn(pattern, repl, text, count=1, flags=flags)
    if count != 1:
        raise SystemExit(f"{label}: expected exactly one match, got {count}")
    return out


# 1) Composer ownership + side-effect safety.
qnrpa_path = Path("src/Bot/ChromeNs/QNRpa.cs")
q = qnrpa_path.read_text(encoding="utf-8-sig")

q = sub_once(
    q,
    r"(        private static readonly ConcurrentDictionary<string, DateTime> AnswerAttemptStartedAt =\n            new ConcurrentDictionary<string, DateTime>\(StringComparer\.Ordinal\);\n)\n(        public string LastSetPlainText \{ get; private set; \})",
    r"\1        private static readonly TimeSpan OwnedDraftRetention = TimeSpan.FromMinutes(30);\n\n        private string _lastOwnedDraftBuyer = string.Empty;\n        private string _lastOwnedDraftText = string.Empty;\n        private DateTime _lastOwnedDraftAt = DateTime.MinValue;\n\n\2",
    "owned draft fields")

marker = "        internal bool IsKnownBotOwnedDraftText(string currentText)\n"
if q.count(marker) != 1:
    raise SystemExit(f"owned helper insertion marker count={q.count(marker)}")
helpers = '''        private void RememberOwnedDraft(string buyer, string text)
        {
            buyer = BuyerIdentityAliasService.ResolveInternalNick(SellerNick, buyer);
            text = (text ?? string.Empty).Trim();
            _lastOwnedDraftBuyer = buyer ?? string.Empty;
            _lastOwnedDraftText = text;
            _lastOwnedDraftAt = text.Length == 0 ? DateTime.MinValue : DateTime.Now;
            LastSetPlainText = text;
            LatestSetTextTime = _lastOwnedDraftAt;
        }

        private void ForgetOwnedDraft()
        {
            _lastOwnedDraftBuyer = string.Empty;
            _lastOwnedDraftText = string.Empty;
            _lastOwnedDraftAt = DateTime.MinValue;
            LastSetPlainText = string.Empty;
            LatestSetTextTime = DateTime.MinValue;
        }

        private bool IsOwnedDraftForBuyer(string buyer, string currentText)
        {
            buyer = BuyerIdentityAliasService.ResolveInternalNick(SellerNick, buyer);
            var ownedBuyer = BuyerIdentityAliasService.ResolveInternalNick(SellerNick, _lastOwnedDraftBuyer);
            var ownedText = (_lastOwnedDraftText ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(buyer)
                || string.IsNullOrWhiteSpace(ownedBuyer)
                || ownedText.Length == 0
                || _lastOwnedDraftAt == DateTime.MinValue
                || DateTime.Now - _lastOwnedDraftAt > OwnedDraftRetention)
            {
                return false;
            }
            if (!BuyerIdentityAliasService.AreEquivalent(SellerNick, ownedBuyer, buyer)) return false;
            return EditorMatchesExpectedText(currentText, ownedText);
        }

'''
q = q.replace(marker, helpers + marker, 1)

marker = "        private async Task<bool> HasExpectedDraftFastAsync(string text, int probeTimeoutMs)\n"
if q.count(marker) != 1:
    raise SystemExit(f"mutation helper insertion marker count={q.count(marker)}")
mutation = '''        private async Task<bool> RunUiMutationAsync(Func<bool> action, string stage)
        {
            if (action == null) return false;
            try
            {
                // Side-effecting UI work must never be abandoned after a timeout. A timed-out
                // Task.Run can still press Ctrl+A/Backspace later and erase a newer draft.
                return await Task.Run(action).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SetSendFailure(stage, ex.Message);
                Log.Info(stage + "失败: " + ex.Message);
                return false;
            }
        }

'''
q = q.replace(marker, mutation + marker, 1)

start_key = "        private async Task<bool> ClearStaleComposerBeforeNewDraftAsync(string buyer, string expected)"
end_key = "        private async Task<bool> TrySetPlainTextByCdpAsync(string buyer, string text)"
if q.count(start_key) != 1 or q.count(end_key) != 1:
    raise SystemExit("stale composer method boundaries changed")
start = q.index(start_key)
end = q.index(end_key, start)
old_method = q[start:end]
if "按Bot独占工作区策略先清空" not in old_method:
    raise SystemExit("stale composer legacy semantics not found")
new_method = '''        private async Task<bool> ClearStaleComposerBeforeNewDraftAsync(string buyer, string expected)
        {
            try
            {
                var currentBuyer = await ReadCurrentBuyerNickAsync().ConfigureAwait(false);
                if (!IsExpectedBuyer(buyer, currentBuyer))
                {
                    SetSendFailure("残留草稿清理", "清理前无法证明当前会话仍为目标买家；target=" + buyer
                        + ", current=" + currentBuyer);
                    return false;
                }

                if (_messageInputTextArea == null
                    && !await RefreshChatControlsAsync(false).ConfigureAwait(false))
                {
                    SetSendFailure("残留草稿清理", "无法定位当前目标买家的千牛输入框");
                    return false;
                }

                string observedText;
                if (!TryGetEditorText(out observedText))
                {
                    SetSendFailure("残留草稿清理", "无法读取当前输入框内容，禁止盲目清空");
                    return false;
                }
                if (string.IsNullOrWhiteSpace(NormalizeEditorText(observedText))) return true;

                // A concurrent retry may already have placed this exact current answer. Adopt it;
                // never delete it and never append another copy.
                if (!string.IsNullOrEmpty(expected) && EditorMatchesExpectedText(observedText, expected))
                {
                    RememberOwnedDraft(buyer, expected);
                    Log.Info("输入框已存在本次任务精确草稿，已接管且不会重复写入: buyer=" + buyer);
                    return true;
                }

                // Only a draft previously recorded by this QNRpa instance for the same buyer may be
                // deleted. Unknown/manual text is preserved fail-closed.
                if (!IsOwnedDraftForBuyer(buyer, observedText))
                {
                    SetSendFailure("残留草稿清理",
                        "输入框存在所有权无法证明的内容，已保留并阻止覆盖/追加发送");
                    Log.Info("残留草稿未清理：无法证明属于同一买家的Bot历史草稿。buyer=" + buyer
                        + ", chars=" + NormalizeEditorText(observedText).Length);
                    return false;
                }

                var ownedText = observedText;
                Log.Info("检测到同一买家的Bot历史残留草稿，准备安全清空后执行新发送任务: buyer="
                    + buyer + ", chars=" + NormalizeEditorText(ownedText).Length);

                var cleared = await RunUiMutationAsync(() =>
                {
                    string latestText;
                    if (!TryGetEditorText(out latestText)
                        || !EditorMatchesExpectedText(latestText, ownedText)
                        || !IsOwnedDraftForBuyer(buyer, latestText)
                        || !FocusEditor())
                    {
                        return false;
                    }
                    PressCtrlA();
                    PressBackspace();
                    Thread.Sleep(120);
                    string afterClear;
                    if (!TryGetEditorText(out afterClear)
                        || !string.IsNullOrWhiteSpace(NormalizeEditorText(afterClear)))
                    {
                        return false;
                    }
                    ForgetOwnedDraft();
                    return true;
                }, "Bot历史残留草稿清理").ConfigureAwait(false);

                if (!cleared)
                {
                    SetSendFailure("残留草稿清理", "Bot历史残留草稿清空失败，已阻止追加写入");
                    return false;
                }

                var buyerAfterClear = await ReadCurrentBuyerNickAsync().ConfigureAwait(false);
                if (!IsExpectedBuyer(buyer, buyerAfterClear))
                {
                    SetSendFailure("残留草稿清理", "清理后当前会话发生变化；target=" + buyer
                        + ", current=" + buyerAfterClear);
                    return false;
                }

                var after = await ProbeInputboxEmptyAsync("残留草稿清理后确认", CdpQuickProbeTimeoutMs).ConfigureAwait(false);
                if (!after.Completed || !after.IsEmpty)
                {
                    SetSendFailure("残留草稿清理", "清空后CDP未确认输入框为空，禁止盲目追加写入");
                    return false;
                }

                Log.Info("同一买家的Bot历史残留草稿已清空并二次确认为空，可继续写入新任务: buyer=" + buyer);
                return true;
            }
            catch (Exception ex)
            {
                SetSendFailure("残留草稿清理异常", ex.Message);
                Log.Exception(ex);
                return false;
            }
        }

'''
q = q[:start] + new_method + q[end:]

q, owned_assignments = re.subn(
    r"LastSetPlainText = text;\n\s*LatestSetTextTime = DateTime\.Now;",
    "RememberOwnedDraft(buyer, text);",
    q)
if owned_assignments < 3:
    raise SystemExit(f"expected >=3 owned draft assignment pairs, got {owned_assignments}")
q, owned_clears = re.subn(
    r"LastSetPlainText = string\.Empty;\n\s*LatestSetTextTime = DateTime\.MinValue;",
    "ForgetOwnedDraft();",
    q)
if owned_clears < 1:
    raise SystemExit(f"expected >=1 ownership clear pair, got {owned_clears}")
qnrpa_path.write_text(q, encoding="utf-8")


# 2) Reuse the mature structured SKU parser from the raw-order recovery bridge.
legacy_path = Path("src/Bot/Options/LegacyAboutUpdateRedirect.cs")
legacy = legacy_path.read_text(encoding="utf-8-sig")
legacy_marker = '''        private static string ResolveSkuText(
            IList<FlatValue> flat,
            string combined,
            out string strategy)
'''
if legacy.count(legacy_marker) != 1:
    raise SystemExit(f"structured sku helper marker count={legacy.count(legacy_marker)}")
legacy_helper = '''        internal static string ResolveSkuTextFromPayload(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            try
            {
                var root = ParseExpanded(raw);
                var flat = Flatten(root);
                var combined = BuildCombinedText(raw, flat);
                string strategy;
                return Clean(ResolveSkuText(flat, combined, out strategy), 240);
            }
            catch
            {
                return string.Empty;
            }
        }

'''
legacy = legacy.replace(legacy_marker, legacy_helper + legacy_marker, 1)
legacy_path.write_text(legacy, encoding="utf-8")

order_path = Path("src/Bot/ChromeNs/OrderTemplateRequiredFieldsV2.cs")
o = order_path.read_text(encoding="utf-8-sig")

o = sub_once(
    o,
    r"            var securityBuyerUid = GetCachedBuyerSecurityId\(plan\.Seller, plan\.Buyer\);\n            probe\.BuyerSecurityIdFound = !string\.IsNullOrWhiteSpace\(securityBuyerUid\);\n            var delays = new\[\] \{ 0, 500, 1000, 2000, 3000, 5000, 7000 \};",
    '''            var securityBuyerUid = GetCachedBuyerSecurityId(plan.Seller, plan.Buyer);
            probe.BuyerSecurityIdFound = !string.IsNullOrWhiteSpace(securityBuyerUid);
            var missingAtStart = MissingRequiredFields(plan.Config, snapshot);
            var needsStructuredFields = missingAtStart.Contains("sku") || missingAtStart.Contains("buyer_remark");
            // Never hold the mandatory order reply for the old 18.5-second cumulative ladder.
            // SKU/buyer remark get a short bounded eventual-consistency window; other fields query once.
            var delays = needsStructuredFields
                ? new[] { 0, 250, 500, 1000, 1500 }
                : new[] { 0 };''',
    "order enrichment bounded schedule")

o = sub_once(
    o,
    r"                    MergeTrade\(snapshot, trade\);\n                    UpdateProbe\(probe, snapshot\);\n                    if \(MissingRequiredFields\(plan\.Config, snapshot\)\.Count == 0\) break;",
    '''                    MergeTrade(snapshot, trade);
                    UpdateProbe(probe, snapshot);
                    var remaining = MissingRequiredFields(plan.Config, snapshot);
                    if (remaining.Count == 0
                        || (!remaining.Contains("sku") && !remaining.Contains("buyer_remark")))
                    {
                        break;
                    }''',
    "order enrichment stop condition")

# messageCenterNotify may already contain the same structured pName/vName fields shown by the
# right-side order panel. Parse those before any trade API retry.
snapshot_marker = '''                OrderId = orderId,
                BuyerRemark = Safe(FindValue(flat, BuyerRemarkKeys), 500),
                TradeStatus = string.IsNullOrWhiteSpace(status) ? (paid == true ? "已付款" : "新下单") : status,
'''
snapshot_replacement = '''                OrderId = orderId,
                SkuText = OrderSkuPayloadRecoveryBridge.ResolveSkuTextFromPayload(raw),
                BuyerRemark = Safe(FindValue(flat, BuyerRemarkKeys), 500),
                TradeStatus = string.IsNullOrWhiteSpace(status) ? (paid == true ? "已付款" : "新下单") : status,
'''
o = replace_once(o, snapshot_marker, snapshot_replacement, "sparse snapshot structured sku")

# The exact trade response can also carry structured SKU outside item.sku. Reuse the same parser
# over the full typed trade object before declaring SKU missing.
sku_block = '''            var sku = string.Join("；", items
                .Select(x => NormalizeSku(x.sku))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(snapshot.SkuText) && sku.Length > 0)
            {
                snapshot.SkuText = Safe(sku, 240);
            }
'''
sku_replacement = '''            var sku = string.Join("；", items
                .Select(x => NormalizeSku(x.sku))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(sku))
            {
                try
                {
                    sku = OrderSkuPayloadRecoveryBridge.ResolveSkuTextFromPayload(
                        JObject.FromObject(trade).ToString(Formatting.None));
                }
                catch { }
            }
            if (string.IsNullOrWhiteSpace(snapshot.SkuText) && sku.Length > 0)
            {
                snapshot.SkuText = Safe(sku, 240);
            }
'''
o = replace_once(o, sku_block, sku_replacement, "trade structured sku fallback")
order_path.write_text(o, encoding="utf-8")


# 3) Update static regression contracts for the intended safety semantics.
stale_test_path = Path("tests/test_stale_composer_cleanup_and_log_retention_static.py")
st = stale_test_path.read_text(encoding="utf-8-sig")
first_start = st.index("def test_stale_desktop_composer_is_cleared_only_after_target_buyer_is_proven():")
second_start = st.index("def test_exact_current_task_draft_is_never_deleted_or_duplicated():", first_start)
third_start = st.index("def test_text_send_clears_stale_buffer_before_new_cdp_insert_and_image_path_does_too():", second_start)
new_first_two = '''def test_stale_desktop_composer_is_cleared_only_when_bot_ownership_and_target_buyer_are_proven():
    text = _read(QNRPA)
    method = text.index("ClearStaleComposerBeforeNewDraftAsync")
    target_read = text.index("var currentBuyer = await ReadCurrentBuyerNickAsync()", method)
    target_guard = text.index("if (!IsExpectedBuyer(buyer, currentBuyer))", target_read)
    ownership = text.index("if (!IsOwnedDraftForBuyer(buyer, observedText))", target_guard)
    clear_log = text.index("检测到同一买家的Bot历史残留草稿", ownership)
    mutation = text.index("RunUiMutationAsync", clear_log)
    exact_recheck = text.index("EditorMatchesExpectedText(latestText, ownedText)", mutation)
    ownership_recheck = text.index("IsOwnedDraftForBuyer(buyer, latestText)", exact_recheck)
    ctrl_a = text.index("PressCtrlA();", ownership_recheck)
    backspace = text.index("PressBackspace();", ctrl_a)
    second_target = text.index("var buyerAfterClear = await ReadCurrentBuyerNickAsync()", backspace)
    cdp_probe = text.index("残留草稿清理后确认", second_target)

    assert method < target_read < target_guard < ownership < clear_log < mutation < exact_recheck < ownership_recheck < ctrl_a < backspace < second_target < cdp_probe
    assert "输入框存在所有权无法证明的内容，已保留并阻止覆盖/追加发送" in text
    method_end = text.index("private async Task<bool> TrySetPlainTextByCdpAsync", method)
    assert "RunUiActionAsync" not in text[method:method_end]
    assert "RunUiMutationAsync" in text[method:method_end]


def test_exact_current_task_draft_is_adopted_and_side_effect_mutations_are_never_timed_out():
    text = _read(QNRPA)
    method = text.index("ClearStaleComposerBeforeNewDraftAsync")
    exact = text.index("EditorMatchesExpectedText(observedText, expected)", method)
    remember = text.index("RememberOwnedDraft(buyer, expected)", exact)
    ownership = text.index("IsOwnedDraftForBuyer(buyer, observedText)", remember)
    clear_log = text.index("检测到同一买家的Bot历史残留草稿", ownership)
    assert method < exact < remember < ownership < clear_log

    mutation = text.index("private async Task<bool> RunUiMutationAsync")
    mutation_end = text.index("private async Task<bool> HasExpectedDraftFastAsync", mutation)
    assert "Task.WhenAny" not in text[mutation:mutation_end]
    assert "Task.Delay" not in text[mutation:mutation_end]
    assert "return await Task.Run(action).ConfigureAwait(false);" in text[mutation:mutation_end]
    assert "OwnedDraftRetention = TimeSpan.FromMinutes(30)" in text


'''
st = st[:first_start] + new_first_two + st[third_start:]
stale_test_path.write_text(st, encoding="utf-8")

order_test_path = Path("tests/test_order_template_required_fields_v2_static.py")
ot = order_test_path.read_text(encoding="utf-8-sig")
ot = replace_once(
    ot,
    '''def test_query_retries_until_required_fields_are_complete_and_payment_can_arrive_later():
    source = read(SOURCE)

    assert "new[] { 0, 500, 1000, 2000, 3000, 5000, 7000 }" in source
    assert "MissingRequiredFields(plan.Config, snapshot).Count == 0" in source
    assert "trade.payTime ?? itemPayTime" in source
    assert "snapshot.PaidAmount = total" in source
    assert "snapshot.EventType = OrderEventType.Paid" in source
    assert "Inflight.TryRemove(inflightKey" in source
    assert "OrderPlacedAutoReplyService.Complete(plan, false)" in source
''',
    '''def test_query_retries_are_bounded_for_structured_fields_and_payment_can_arrive_later():
    source = read(SOURCE)

    assert 'missingAtStart.Contains("sku") || missingAtStart.Contains("buyer_remark")' in source
    assert "new[] { 0, 250, 500, 1000, 1500 }" in source
    assert "new[] { 0, 500, 1000, 2000, 3000, 5000, 7000 }" not in source
    assert 'remaining.Contains("sku")' in source
    assert 'remaining.Contains("buyer_remark")' in source
    assert "trade.payTime ?? itemPayTime" in source
    assert "snapshot.PaidAmount = total" in source
    assert "snapshot.EventType = OrderEventType.Paid" in source
    assert "Inflight.TryRemove(inflightKey" in source
    assert "OrderPlacedAutoReplyService.Complete(plan, false)" in source


def test_required_fields_v2_reuses_structured_sku_parser_before_render_and_after_trade_query():
    source = read(SOURCE)
    assert "SkuText = OrderSkuPayloadRecoveryBridge.ResolveSkuTextFromPayload(raw)" in source
    assert "JObject.FromObject(trade).ToString(Formatting.None)" in source
    assert "OrderSkuPayloadRecoveryBridge.ResolveSkuTextFromPayload(" in source
''',
    "order retry regression test")
order_test_path.write_text(ot, encoding="utf-8")

inbound_test_path = Path("tests/test_qianniu_duplicate_cdp_inbound_recovery_static.py")
it = inbound_test_path.read_text(encoding="utf-8-sig")
it = it.replace(
    'assert "InboundFingerprintWindow = TimeSpan.FromSeconds(3)" in bridge',
    'assert "InboundFingerprintWindow = TimeSpan.FromMinutes(2)" in bridge')
it = it.replace(
    'assert "InboundFingerprintRetention = TimeSpan.FromSeconds(30)" in bridge',
    'assert "InboundFingerprintRetention = TimeSpan.FromMinutes(5)" in bridge')
if "InboundFingerprintWindow = TimeSpan.FromMinutes(2)" not in it:
    raise SystemExit("inbound long-window assertion replacement failed")
inbound_test_path.write_text(it, encoding="utf-8")

# Add a focused cross-cutting regression file.
regression = Path("tests/test_1196_followup_runtime_hardening_static.py")
regression.write_text('''from pathlib import Path

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
''', encoding="utf-8")

print("runtime hardening source migration applied")
