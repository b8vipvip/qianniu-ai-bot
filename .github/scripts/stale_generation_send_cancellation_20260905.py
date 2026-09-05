from pathlib import Path


def read(path):
    return Path(path).read_text(encoding="utf-8-sig")


def write(path, text):
    Path(path).write_text(text, encoding="utf-8-sig")


def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{label}: expected exactly 1 match, got {count}")
    return text.replace(old, new, 1)


def replace_count(text, old, new, expected, label):
    count = text.count(old)
    if count != expected:
        raise SystemExit(f"{label}: expected {expected} matches, got {count}")
    return text.replace(old, new)


# 1) Make the shared QN reliable-send helper cancellation-aware while preserving
# the existing overload for all non-generation callers.
qn_path = "src/Bot/ChromeNs/QN.cs"
qn = read(qn_path)
qn = replace_once(
    qn,
    '''        public async Task<bool> SendTextWithRetryAsync(string buyer, string text, int retryCount = 1)\n        {\n            const string segmentToken = "{分段符}";''',
    '''        public Task<bool> SendTextWithRetryAsync(string buyer, string text, int retryCount = 1)\n        {\n            return SendTextWithRetryAsync(buyer, text, retryCount, CancellationToken.None);\n        }\n\n        public async Task<bool> SendTextWithRetryAsync(\n            string buyer,\n            string text,\n            int retryCount,\n            CancellationToken cancellationToken)\n        {\n            cancellationToken.ThrowIfCancellationRequested();\n            const string segmentToken = "{分段符}";''',
    "add cancellation-aware reliable-send overload")
qn = replace_once(
    qn,
    'if (!await SendTextWithRetryAsync(buyer, segment, retryCount)) return false;',
    'if (!await SendTextWithRetryAsync(buyer, segment, retryCount, cancellationToken)) return false;',
    "thread token through segmented send")
qn = replace_once(
    qn,
    'if (segmentIndex + 1 < segments.Count) await Task.Delay(220);',
    'if (segmentIndex + 1 < segments.Count) await Task.Delay(220, cancellationToken);',
    "cancel segmented-send delay")
qn = replace_once(
    qn,
    '''            await _sendGate.WaitAsync();\n            try\n            {\n                rpa.ResetSendFailure();''',
    '''            await _sendGate.WaitAsync(cancellationToken);\n            try\n            {\n                cancellationToken.ThrowIfCancellationRequested();\n                rpa.ResetSendFailure();''',
    "cancel send-gate wait")
qn = replace_count(
    qn,
    'if (!await EnsureActiveBuyerForSendAsync(buyer))',
    'if (!await EnsureActiveBuyerForSendAsync(buyer, cancellationToken))',
    2,
    "thread token through buyer confirmation")
qn = replace_count(
    qn,
    'ok = await SendTextAsync(buyer, text);',
    'cancellationToken.ThrowIfCancellationRequested();\n                    ok = await SendTextAsync(buyer, text);',
    2,
    "guard real send side effect")
# First assignment has `var ok`, so protect it separately.
qn = replace_once(
    qn,
    '''                var sendStartedAt = DateTime.Now;\n                var ok = await SendTextAsync(buyer, text);''',
    '''                cancellationToken.ThrowIfCancellationRequested();\n                var sendStartedAt = DateTime.Now;\n                var ok = await SendTextAsync(buyer, text);''',
    "guard initial real send")
qn = replace_once(
    qn,
    'if (!ok && await WaitForSellerEchoGraceAsync(buyer, text, sendStartedAt, 900))',
    'if (!ok && await WaitForSellerEchoGraceAsync(buyer, text, sendStartedAt, 900, cancellationToken))',
    "cancel initial seller-echo wait")
qn = replace_once(
    qn,
    'if (!ok && await WaitForSellerEchoGraceAsync(buyer, text, sendStartedAt, 900))',
    'if (!ok && await WaitForSellerEchoGraceAsync(buyer, text, sendStartedAt, 900, cancellationToken))',
    "cancel retry seller-echo wait")
qn = replace_once(
    qn,
    'if (!ok && await WaitForSellerEchoGraceAsync(buyer, text, sendStartedAt, 1400))',
    'if (!ok && await WaitForSellerEchoGraceAsync(buyer, text, sendStartedAt, 1400, cancellationToken))',
    "cancel final seller-echo wait")
qn = replace_once(
    qn,
    '''                    rpa.InvalidateChatControls();\n                    await Task.Delay(1800);''',
    '''                    cancellationToken.ThrowIfCancellationRequested();\n                    rpa.InvalidateChatControls();\n                    await Task.Delay(1800, cancellationToken);''',
    "cancel retry delay")
qn = replace_once(
    qn,
    '''        private async Task<bool> WaitForSellerEchoGraceAsync(\n            string buyer,\n            string text,\n            DateTime since,\n            int milliseconds)''',
    '''        private async Task<bool> WaitForSellerEchoGraceAsync(\n            string buyer,\n            string text,\n            DateTime since,\n            int milliseconds,\n            CancellationToken cancellationToken)''',
    "seller-echo cancellation parameter")
qn = replace_once(
    qn,
    '''            while (DateTime.Now <= deadline)\n            {\n                if (HasRecentSellerEcho(buyer, text, since)) return true;\n                await Task.Delay(120);''',
    '''            while (DateTime.Now <= deadline)\n            {\n                cancellationToken.ThrowIfCancellationRequested();\n                if (HasRecentSellerEcho(buyer, text, since)) return true;\n                await Task.Delay(120, cancellationToken);''',
    "cancel seller-echo polling")
qn = replace_once(
    qn,
    '        private async Task<bool> EnsureActiveBuyerForSendAsync(string buyer)\n',
    '        private async Task<bool> EnsureActiveBuyerForSendAsync(string buyer, CancellationToken cancellationToken)\n',
    "buyer-confirm cancellation parameter")
qn = replace_once(
    qn,
    '''            for (var attempt = 0; attempt < 22 && DateTime.UtcNow < deadlineUtc; attempt++)\n            {\n                try''',
    '''            for (var attempt = 0; attempt < 22 && DateTime.UtcNow < deadlineUtc; attempt++)\n            {\n                cancellationToken.ThrowIfCancellationRequested();\n                try''',
    "cancel buyer-confirm loop")
qn = replace_once(
    qn,
    '''                    if (attempt == 0)\n                    {\n                        Log.Info("发送前切换目标买家: target=" + buyer + ", current=" + currentNick);\n                        OpenChat(buyer);\n                    }''',
    '''                    if (attempt == 0)\n                    {\n                        cancellationToken.ThrowIfCancellationRequested();\n                        Log.Info("发送前切换目标买家: target=" + buyer + ", current=" + currentNick);\n                        OpenChat(buyer);\n                    }''',
    "guard buyer switch side effect")
qn = replace_once(
    qn,
    '''                    Log.Info("发送前确认买家会话失败: " + ex.Message);\n                    if (attempt == 0) OpenChat(buyer);''',
    '''                    Log.Info("发送前确认买家会话失败: " + ex.Message);\n                    if (attempt == 0)\n                    {\n                        cancellationToken.ThrowIfCancellationRequested();\n                        OpenChat(buyer);\n                    }''',
    "guard buyer switch in exception path")
qn = replace_once(
    qn,
    'await Task.Delay(Math.Min(ActiveBuyerConfirmPollMs, remainingMs));',
    'await Task.Delay(Math.Min(ActiveBuyerConfirmPollMs, remainingMs), cancellationToken);',
    "cancel buyer-confirm polling")
write(qn_path, qn)


# 2) Bind pre-merge deterministic replies to the exact generation token.  These
# replies intentionally remain state-neutral because first-inquiry greeting may
# continue into the AI path; cancellation must therefore happen inside send.
det_path = "src/Bot/ChromeNs/DeterministicAutoReplyService.cs"
det = read(det_path)
det = replace_once(
    det,
    '''            var sessionAgent = new BuyerSessionAgent();\n            if (item.SessionGeneration > 0''',
    '''            var sessionAgent = new BuyerSessionAgent();\n            var generationToken = item.SessionGeneration > 0\n                ? sessionAgent.GetCancellationToken(item.SellerNick, item.BuyerNick, item.SessionGeneration)\n                : CancellationToken.None;\n            if (item.SessionGeneration > 0''',
    "capture deterministic generation token")
det = replace_once(
    det,
    'var ok = await qn.SendTextWithRetryAsync(item.BuyerNick, answer, 3).ConfigureAwait(false);',
    'var ok = await qn.SendTextWithRetryAsync(item.BuyerNick, answer, 3, generationToken).ConfigureAwait(false);',
    "pass deterministic token to reliable send")
det = replace_once(
    det,
    '''            catch (Exception ex)\n            {\n                if (item.SessionGeneration > 0)''',
    '''            catch (OperationCanceledException)\n            {\n                if (ctl != null) ctl.SetSendResult(false, "generation已失效，固定回复发送已取消");\n                Log.Info(source + "等待发送资源/会话确认期间generation失效，已在UI副作用前取消: seller="\n                    + item.SellerNick + ", buyer=" + item.BuyerNick\n                    + ", generation=" + item.SessionGeneration);\n                return false;\n            }\n            catch (Exception ex)\n            {\n                if (item.SessionGeneration > 0)''',
    "classify deterministic cancellation")
write(det_path, det)


# 3) Streaming sends already own Sending state; bind the shared send helper to
# the lease token so watchdog invalidation also stops lock waits, buyer switching,
# retry delays and composer/send-button side effects.
stream_path = "src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs"
stream = read(stream_path)
stream = replace_once(
    stream,
    '''            var sendOk = await qn.SendTextWithRetryAsync(burst.BuyerNick, answer, 1);\n            if (sendOk)''',
    '''            bool sendOk;\n            try\n            {\n                sendOk = await qn.SendTextWithRetryAsync(\n                    burst.BuyerNick, answer, 1, lease.CancellationToken);\n            }\n            catch (OperationCanceledException)\n            {\n                const string cancelledDuringSend = "未发送：generation在等待发送资源/会话确认期间已失效";\n                if (conversationCtl != null) conversationCtl.SetSendResult(false, cancelledDuringSend);\n                ResponseProgressTracker.Cancel(burst.SellerNick, burst.BuyerNick, cancelledDuringSend);\n                Log.Info("Smart Reply发送期间generation硬失效，已停止后续UI发送副作用: buyer="\n                    + burst.BuyerNick);\n                return;\n            }\n            if (sendOk)''',
    "bind streaming send to generation token")
write(stream_path, stream)


# 4) Static regression coverage for the exact production regression observed in
# 1.1.1280: generation 14 expired at ~55s but entered automatic UI send ~20s later.
test_path = Path("tests/test_1281_generation_bound_send_cancellation_static.py")
test_path.write_text('''from pathlib import Path\n\nROOT = Path(__file__).resolve().parents[1]\n\n\ndef read(path: str) -> str:\n    return (ROOT / path).read_text(encoding="utf-8-sig")\n\n\ndef test_reliable_send_has_backward_compatible_cancellation_aware_overload():\n    qn = read("src/Bot/ChromeNs/QN.cs")\n    assert "return SendTextWithRetryAsync(buyer, text, retryCount, CancellationToken.None);" in qn\n    assert "CancellationToken cancellationToken" in qn\n    assert "await _sendGate.WaitAsync(cancellationToken);" in qn\n    assert "await Task.Delay(1800, cancellationToken);" in qn\n    assert "EnsureActiveBuyerForSendAsync(buyer, cancellationToken)" in qn\n    assert "Task.Delay(Math.Min(ActiveBuyerConfirmPollMs, remainingMs), cancellationToken)" in qn\n\n\ndef test_reliable_send_checks_cancellation_before_real_ui_side_effects():\n    qn = read("src/Bot/ChromeNs/QN.cs")\n    confirm = qn.split("private async Task<bool> EnsureActiveBuyerForSendAsync", 1)[1]\n    assert "cancellationToken.ThrowIfCancellationRequested();\\n                        OpenChat(buyer);" in confirm\n    send = qn.split("public async Task<bool> SendTextWithRetryAsync(", 1)[1]\n    assert send.count("cancellationToken.ThrowIfCancellationRequested();") >= 3\n    assert "Task.Delay(120, cancellationToken)" in qn\n\n\ndef test_premerge_fixed_reply_passes_exact_generation_token_without_marking_sending():\n    fixed = read("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")\n    region = fixed.split("private static async Task<bool> SendFixedAsync", 1)[1].split("public static async Task<bool> TryHandleAsync", 1)[0]\n    assert "GetCancellationToken(item.SellerNick, item.BuyerNick, item.SessionGeneration)" in region\n    assert "SendTextWithRetryAsync(item.BuyerNick, answer, 3, generationToken)" in region\n    assert "catch (OperationCanceledException)" in region\n    assert "BuyerSessionAgentState.Sending" not in region\n\n\ndef test_streaming_send_is_cancelled_by_lease_generation_token():\n    stream = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")\n    assert "burst.BuyerNick, answer, 1, lease.CancellationToken" in stream\n    assert "Smart Reply发送期间generation硬失效" in stream\n    assert "catch (OperationCanceledException)" in stream\n''', encoding="utf-8")

print("stale-generation send cancellation hardening applied")
