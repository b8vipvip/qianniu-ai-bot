from pathlib import Path


def read(path):
    return Path(path).read_text(encoding="utf-8-sig")


def write(path, text):
    Path(path).write_text(text, encoding="utf-8")


def replace_once(text, old, new, label):
    if old not in text:
        raise SystemExit("missing patch anchor: " + label)
    return text.replace(old, new, 1)


# 1. Sending: when OS coordinate input is denied, invoke only the exact verified Qianniu sendMsg UIA element.
qnrpa_path = "src/Bot/ChromeNs/QNRpa.cs"
qnrpa = read(qnrpa_path)
anchor = """        private bool TryInvokeCachedSendButtonNow()
        {
            if (_sendMessageButton == null || uia3Automation == null) return false;
            try
            {
"""
replacement = """        private bool TryInvokeExactVerifiedSendButtonNow()
        {
            if (_sendMessageButton == null) return false;
            try
            {
                var automationId = _sendMessageButton.AutomationId ?? string.Empty;
                var name = _sendMessageButton.Name ?? string.Empty;
                if (!string.Equals(automationId, SendButtonAutomationId, StringComparison.Ordinal)
                    || !IsSendButtonName(name))
                {
                    Log.Info("精确发送按钮UIA调用已阻止：控件身份不匹配");
                    return false;
                }
                var identity = (automationId + " " + name).ToLowerInvariant();
                if (identity.Contains("arrow") || identity.Contains("dropdown")
                    || identity.Contains("menu") || identity.Contains("downbutton")
                    || identity.Contains("下拉") || identity.Contains("展开"))
                {
                    Log.Info("精确发送按钮UIA调用已阻止：命中下拉身份");
                    return false;
                }
                _sendMessageButton.AsButton().Invoke();
                Log.Info("已通过精确发送按钮UIA Invoke执行发送: seller=" + SellerNick
                    + ", automationId=" + automationId + ", name=" + name
                    + ", rect=" + FormatRect(SafeBoundingRectangle(_sendMessageButton)));
                return true;
            }
            catch (Exception ex)
            {
                Log.Info("精确发送按钮UIA Invoke失败: " + ex.Message
                    + ", type=" + ex.GetType().FullName
                    + ", hresult=0x" + ex.HResult.ToString("X8"));
                return false;
            }
        }

        private bool TryInvokeCachedSendButtonNow()
        {
            if (_sendMessageButton == null || uia3Automation == null) return false;
            try
            {
                if (TryInvokeExactVerifiedSendButtonNow()) return true;
"""
qnrpa = replace_once(qnrpa, anchor, replacement, "exact UIA send invoke")

# Keep exact failed Bot draft ownership after the 20-second retry window; any human edit breaks equality.
owned_anchor = """        private bool HasOwnedRecentDraft(string text)
        {
            text = (text ?? string.Empty).Trim();
            return text.Length > 0
                && string.Equals((LastSetPlainText ?? string.Empty).Trim(), text, StringComparison.Ordinal)
                && LatestSetTextTime != DateTime.MinValue
                && (DateTime.Now - LatestSetTextTime).TotalSeconds <= 20;
        }
"""
owned_replacement = owned_anchor + """
        internal bool IsKnownBotOwnedDraftText(string currentText)
        {
            var expected = (LastSetPlainText ?? string.Empty).Trim();
            return expected.Length > 0 && EditorMatchesExpectedText(currentText, expected);
        }

        internal async Task<bool> IsKnownBotOwnedDraftAsync()
        {
            if (string.IsNullOrWhiteSpace(LastSetPlainText)) return false;
            if (_messageInputTextArea == null)
            {
                await RefreshChatControlsAsync(false).ConfigureAwait(false);
            }
            string current;
            return TryGetEditorText(out current) && IsKnownBotOwnedDraftText(current);
        }
"""
qnrpa = replace_once(qnrpa, owned_anchor, owned_replacement, "bot draft ownership")
write(qnrpa_path, qnrpa)

# 2. Order attention must not turn an unchanged Bot-owned failed draft into recurring human protection.
queue_path = "src/Bot/ChromeNs/NewOrderAttentionQueue.cs"
queue = read(queue_path)
old = """            if (!input.Empty)
            {
                BotActivityCoordinator.MarkHumanInteraction(snapshot.Seller, "客服输入框中存在未发送内容");
                return Denied("客服正在输入消息");
            }
"""
new = """            if (!input.Empty)
            {
                if (rpa != null && await rpa.IsKnownBotOwnedDraftAsync().ConfigureAwait(false))
                {
                    return Denied("Bot发送失败草稿仍在输入框，等待发送恢复且不计为人工操作");
                }
                BotActivityCoordinator.MarkHumanInteraction(snapshot.Seller, "客服输入框中存在未发送内容");
                return Denied("客服正在输入消息");
            }
"""
queue = replace_once(queue, old, new, "order queue owned draft guard")
old = """                    if (!input.Success || !input.Empty)
                    {
                        if (input.Success && !input.Empty)
                        {
                            BotActivityCoordinator.MarkHumanInteraction(snapshot.Seller, "自动切换前检测到客服输入内容");
                        }
                        return false;
                    }
"""
new = """                    if (!input.Success || !input.Empty)
                    {
                        if (input.Success && !input.Empty)
                        {
                            if (rpa != null && await rpa.IsKnownBotOwnedDraftAsync().ConfigureAwait(false))
                            {
                                Log.Info("自动切换前发现Bot自有失败草稿，未标记人工操作");
                            }
                            else
                            {
                                BotActivityCoordinator.MarkHumanInteraction(snapshot.Seller, "自动切换前检测到客服输入内容");
                            }
                        }
                        return false;
                    }
"""
queue = replace_once(queue, old, new, "order focus owned draft guard")
write(queue_path, queue)

# 3. BuyerSessionAgent Failed is terminal: deterministic failure and AI timeout may not become Completed.
burst_path = "src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs"
burst = read(burst_path)
old = """                else
                {
                    _sessionAgent.TryTransition(
                        item.SellerNick,
                        item.BuyerNick,
                        item.SessionGeneration,
                        BuyerSessionAgentState.Completed,
                        "deterministic_rule_consumed");
                }
"""
new = """                else
                {
                    var deterministicSnapshot = _sessionAgent.GetSnapshot(
                        item.SellerNick,
                        item.BuyerNick);
                    if (deterministicSnapshot != null
                        && deterministicSnapshot.Generation == item.SessionGeneration
                        && deterministicSnapshot.State == BuyerSessionAgentState.Failed)
                    {
                        Log.Info("固定规则发送失败后保留Failed终态，禁止升级Completed: seller="
                            + item.SellerNick + ", buyer=" + item.BuyerNick
                            + ", generation=" + item.SessionGeneration);
                    }
                    else
                    {
                        _sessionAgent.TryTransition(
                            item.SellerNick,
                            item.BuyerNick,
                            item.SessionGeneration,
                            BuyerSessionAgentState.Completed,
                            "deterministic_rule_consumed");
                    }
                }
"""
burst = replace_once(burst, old, new, "deterministic failed terminal")
old = """                        var returnedWithoutReady = snapshot != null
                            && snapshot.Generation == burst.SessionGeneration
                            && snapshot.State == BuyerSessionAgentState.Generating;
                        if (returnedWithoutReady && burst.HasReplyableItem)
                        {
                            lease.MarkFailed("reply_pipeline_returned_without_ready");
                            Log.Info("回复管线在答案就绪前返回，保持失败态而非误记Completed: seller="
                                + burst.SellerNick + ", buyer=" + burst.BuyerNick
                                + ", generation=" + burst.SessionGeneration);
                        }
                        else
                        {
                            lease.MarkCompleted(returnedWithoutReady
                                ? "non_replyable_media_skipped"
                                : "reply_pipeline_completed");
                        }
"""
new = """                        var failed = snapshot != null
                            && snapshot.Generation == burst.SessionGeneration
                            && snapshot.State == BuyerSessionAgentState.Failed;
                        var returnedWithoutReady = snapshot != null
                            && snapshot.Generation == burst.SessionGeneration
                            && snapshot.State == BuyerSessionAgentState.Generating;
                        if (failed)
                        {
                            Log.Info("回复管线返回时会话已是Failed，保留失败终态且禁止升级Completed: seller="
                                + burst.SellerNick + ", buyer=" + burst.BuyerNick
                                + ", generation=" + burst.SessionGeneration);
                        }
                        else if (returnedWithoutReady && burst.HasReplyableItem)
                        {
                            lease.MarkFailed("reply_pipeline_returned_without_ready");
                            Log.Info("回复管线在答案就绪前返回，保持失败态而非误记Completed: seller="
                                + burst.SellerNick + ", buyer=" + burst.BuyerNick
                                + ", generation=" + burst.SessionGeneration);
                        }
                        else
                        {
                            lease.MarkCompleted(returnedWithoutReady
                                ? "non_replyable_media_skipped"
                                : "reply_pipeline_completed");
                        }
"""
burst = replace_once(burst, old, new, "post-dispatch failed terminal")
write(burst_path, burst)

# 4. Duplicate/raw WebViews remain inbound recovery only; they cannot become outbound CDP command owner.
server_path = "src/Bot/ChromeNs/MyWebSocketServer.cs"
server = read(server_path)
marker = "        internal static bool TryGetAuthoritativeSession(string sellerNick, out string sessionId)\n"
if marker not in server:
    raise SystemExit("missing authoritative-session helper anchor")
auth_method = """        internal static bool IsAuthoritativeSellerSession(string sellerNick, string sessionId)
        {
            sellerNick = NormalizeSeller(sellerNick);
            sessionId = (sessionId ?? string.Empty).Trim();
            if (sellerNick.Length == 0 || sessionId.Length == 0) return false;
            string authoritative;
            return _sellerSessions.TryGetValue(sellerNick, out authoritative)
                && string.Equals(authoritative, sessionId, StringComparison.Ordinal);
        }

"""
server = server.replace(marker, auth_method + marker, 1)
write(server_path, server)

cdp_path = "src/Bot/ChromeNs/CDPClient.cs"
cdp = read(cdp_path)
old = "            PreferRuntimeSession(sellerNick, SessionId, buyerNick, \"onConversationChange\");\n"
new = """            if (MyWebSocketServer.IsAuthoritativeSellerSession(sellerNick, SessionId))
            {
                PreferRuntimeSession(sellerNick, SessionId, buyerNick, "onConversationChange");
            }
            else
            {
                Log.Info("重复千牛页面会话切换仅作为入站证据，不接管CDP命令路由");
            }
"""
cdp = replace_once(cdp, old, new, "duplicate session route guard")
write(cdp_path, cdp)
