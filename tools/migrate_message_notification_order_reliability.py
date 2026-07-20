from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    p = ROOT / path
    raw = p.read_bytes()
    return p, raw.startswith(b"\xef\xbb\xbf"), raw.decode("utf-8-sig")


def write(p, bom, text):
    p.write_bytes(text.encode("utf-8-sig" if bom else "utf-8"))


def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected one match, got {count}")
    return text.replace(old, new, 1)


def replace_between(text, start, end, replacement, label):
    i = text.find(start)
    if i < 0:
        raise RuntimeError(f"{label}: start marker missing")
    j = text.find(end, i)
    if j < 0:
        raise RuntimeError(f"{label}: end marker missing")
    return text[:i] + replacement + text[j:]


def patch_targets():
    p, bom, text = read("src/Directory.Build.targets")
    anchor = '''  <ItemGroup Condition="Exists('$(MSBuildProjectDirectory)\\ChromeNs\\BuyerMessageBurstCoordinator.cs')">
    <Compile Include="$(MSBuildProjectDirectory)\\ChromeNs\\BuyerMessageBurstCoordinator.cs" />
  </ItemGroup>
'''
    extra = anchor + '''  <ItemGroup Condition="Exists('$(MSBuildProjectDirectory)\\ChromeNs\\QNRpa.ReliableSend.cs')">
    <Compile Include="$(MSBuildProjectDirectory)\\ChromeNs\\QNRpa.ReliableSend.cs" />
  </ItemGroup>
  <ItemGroup Condition="Exists('$(MSBuildProjectDirectory)\\ChromeNs\\QN.MessageRecovery.cs')">
    <Compile Include="$(MSBuildProjectDirectory)\\ChromeNs\\QN.MessageRecovery.cs" />
  </ItemGroup>
  <ItemGroup Condition="Exists('$(MSBuildProjectDirectory)\\ChromeNs\\OrderPlacedAutoReplyService.cs')">
    <Compile Include="$(MSBuildProjectDirectory)\\ChromeNs\\OrderPlacedAutoReplyService.cs" />
  </ItemGroup>
'''
    text = replace_once(text, anchor, extra, "Directory.Build.targets")
    write(p, bom, text)


def patch_qnrpa():
    p, bom, text = read("src/Bot/ChromeNs/QNRpa.cs")
    text = replace_once(text, "public class QNRpa", "public partial class QNRpa", "QNRpa partial")

    wrapper = '''        public async void UpdateChatBrowserRect(bool force = false)
        {
            await RefreshChatControlsAsync(force);
        }

'''
    text = replace_between(
        text,
        "        public async void UpdateChatBrowserRect(bool force = false)",
        "        public async Task SendImageAsync",
        wrapper,
        "QNRpa UIA wrapper")

    old_get = '''        private string GetEditorTextSafe()
        {
            try
            {
                if (_messageInputTextArea == null) return string.Empty;
                return _messageInputTextArea.Text ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private bool IsEditorEmptySafe()
        {
            if (_messageInputTextArea == null) return false;
            return string.IsNullOrWhiteSpace(GetEditorTextSafe());
        }
'''
    new_get = '''        private string GetEditorTextSafe()
        {
            string text;
            return TryGetEditorText(out text) ? text : string.Empty;
        }

        private bool IsEditorEmptySafe()
        {
            string text;
            return TryGetEditorText(out text) && string.IsNullOrWhiteSpace(text);
        }
'''
    text = replace_once(text, old_get, new_get, "QNRpa editor read")

    old_cdp = '''                var editorText = GetEditorTextSafe();
                var ok = (hasCdpEmpty && !cdpEmpty) || !string.IsNullOrWhiteSpace(editorText);

                Log.Info("CDP写入输入框结果=" + ok + ", hasCdpEmpty=" + hasCdpEmpty + ", cdpEmpty=" + cdpEmpty + ", editorText=" + editorText + ", text=" + text);
'''
    new_cdp = '''                string editorText;
                var editorReadable = TryGetEditorText(out editorText);
                var ok = (editorReadable && EditorMatchesExpectedText(editorText, text))
                    || (hasCdpEmpty && !cdpEmpty);

                Log.Info("CDP写入输入框结果=" + ok + ", editorReadable=" + editorReadable
                    + ", hasCdpEmpty=" + hasCdpEmpty + ", cdpEmpty=" + cdpEmpty
                    + ", editorText=" + editorText + ", text=" + text);
'''
    text = replace_once(text, old_cdp, new_cdp, "QNRpa CDP exact draft")

    old_focus = '''                    if (_messageInputTextArea == null)
                    {
                        UpdateChatBrowserRect(true);
                        Thread.Sleep(500);
                    }
                    if (_messageInputTextArea == null)
                    {
                        Log.Info("FocusEditor失败：未找到输入框TextRichEdit。");
                        return;
                    }
'''
    new_focus = '''                    if (_messageInputTextArea == null)
                    {
                        RefreshChatControlsAsync(true).GetAwaiter().GetResult();
                    }
                    if (_messageInputTextArea == null)
                    {
                        SetSendFailure("聚焦输入框", "未找到聊天输入框");
                        return;
                    }
'''
    text = replace_once(text, old_focus, new_focus, "QNRpa focus refresh")

    text = replace_once(
        text,
        '''                catch (Exception e)
                {
                    Log.Exception(e);
                }
            });
            return isok;
        }

        public async Task<bool> SendTextAsync''',
        '''                catch (Exception e)
                {
                    SetSendFailure("聚焦输入框", e.Message);
                    Log.Exception(e);
                }
            });
            return isok;
        }

        public async Task<bool> SendTextAsync''',
        "QNRpa focus exception")

    open_method = '''        private async Task<bool> OpenAndSendText(string buyer, string text)
        {
            bool sendResult = false;
            ResetSendFailure();
            try
            {
                Log.Info("自动发送开始: buyer=" + buyer + ", text=" + text + ", current=" + (_qn.Buyer == null ? "" : _qn.Buyer.Nick));

                if (_qn.Buyer == null || _qn.Buyer.Nick != buyer)
                {
                    _qn.OpenChat(buyer);
                    await Task.Delay(500);
                    var conv = await _qn.GetCurrentConversationID();
                    if (conv != null && conv.Result != null && !string.IsNullOrWhiteSpace(conv.Result.Nick))
                    {
                        _qn.SetActiveConversationByNick(_qn.Seller == null ? string.Empty : _qn.Seller.Nick, conv.Result.Nick, "beforeSend");
                    }
                }

                if (_qn.Buyer == null || _qn.Buyer.Nick != buyer)
                {
                    SetSendFailure("会话确认", "当前会话不是目标买家；target=" + buyer
                        + ", current=" + (_qn.Buyer == null ? "" : _qn.Buyer.Nick));
                    return false;
                }

                if (!Desk.Inst.IsVisible)
                {
                    Desk.Inst.Show();
                    Util.WaitFor(new Func<bool>(() => Desk.Inst.IsVisible), 3000, 10, false);
                }

                await RefreshChatControlsAsync(true);
                var setOk = SetPlainText(text);
                if (!setOk)
                {
                    Log.Info("RPA写入输入框失败，改用CDP insertText2Inputbox。buyer=" + buyer + ", text=" + text);
                    setOk = await TrySetPlainTextByCdpAsync(buyer, text);
                }

                if (!setOk)
                {
                    SetSendFailure("写入输入框", "UIA与CDP均未确认目标文本");
                    return false;
                }

                Thread.Sleep(250);
                var sendStart = DateTime.Now;
                sendResult = TryClickSendButton(buyer, text, sendStart);
                if (!sendResult && string.IsNullOrWhiteSpace(LastSendFailureReason))
                {
                    SetSendFailure("发送确认", "Enter与发送按钮均未确认消息送达");
                }
                Log.Info("自动发送完成: result=" + sendResult + ", buyer=" + buyer
                    + ", failure=" + GetSendFailureReason() + ", text=" + text);
            }
            catch (Exception ex)
            {
                SetSendFailure("自动发送异常", ex.Message);
                Log.Exception(ex);
                sendResult = false;
            }
            return sendResult;
        }

'''
    text = replace_between(
        text,
        "        private async Task<bool> OpenAndSendText(string buyer, string text)",
        "        private bool SetPlainText(string text)",
        open_method,
        "QNRpa OpenAndSendText")

    old_loop = '''                        var editorText = GetEditorTextSafe();
                        if (!string.IsNullOrWhiteSpace(editorText))
                        {
                            isok = true;
                            break;
                        }
'''
    new_loop = '''                        string editorText;
                        if (TryGetEditorText(out editorText)
                            && EditorMatchesExpectedText(editorText, text))
                        {
                            isok = true;
                            break;
                        }
'''
    text = replace_once(text, old_loop, new_loop, "QNRpa exact clipboard write")

    old_click_refresh = '''                UpdateChatBrowserRect(true);
                Thread.Sleep(350);
                if (_sendMessageButton != null && TryClickSendButtonLeftPart(buyer, text, sendStart)) return true;
'''
    new_click_refresh = '''                RefreshChatControlsAsync(true).GetAwaiter().GetResult();
                if (!HasExpectedDraft(text))
                {
                    bool cdpEmpty;
                    if (!TryIsInputboxEmptyByCdp(out cdpEmpty) || cdpEmpty)
                    {
                        SetSendFailure("发送按钮回退", "发送前未确认输入框仍包含目标文本");
                        return false;
                    }
                }
                if (_sendMessageButton != null && TryClickSendButtonLeftPart(buyer, text, sendStart)) return true;
                SetSendFailure("发送按钮回退", _sendMessageButton == null ? "未找到发送按钮" : "点击后未确认发送");
'''
    text = replace_once(text, old_click_refresh, new_click_refresh, "QNRpa click fallback")

    old_timeout = '''            BotConnectionDiagnostics.RecordSendAttempt(false, method + "后未确认发送");
            Log.Info(method + "发送未确认，editorText=" + editorText + ", hasCdpEmpty=" + hasCdpEmpty + ", cdpEmpty=" + cdpEmpty + ", text=" + text);
            return false;
'''
    new_timeout = '''            SetSendFailure("发送确认", method + "后未确认发送；editorText=" + editorText
                + ", hasCdpEmpty=" + hasCdpEmpty + ", cdpEmpty=" + cdpEmpty);
            Log.Info(method + "发送未确认，editorText=" + editorText + ", hasCdpEmpty=" + hasCdpEmpty + ", cdpEmpty=" + cdpEmpty + ", text=" + text);
            return false;
'''
    text = replace_once(text, old_timeout, new_timeout, "QNRpa send timeout")
    write(p, bom, text)


def patch_qn():
    p, bom, text = read("src/Bot/ChromeNs/QN.cs")
    text = replace_once(text, "public class QN", "public partial class QN", "QN partial")

    send_retry = '''        public async Task<bool> SendTextWithRetryAsync(string buyer, string text, int retryCount = 1)
        {
            await _sendGate.WaitAsync();
            try
            {
                rpa.ResetSendFailure();
                if (!await EnsureActiveBuyerForSendAsync(buyer))
                {
                    rpa.SetSendFailure("会话确认", "无法确认目标买家会话");
                    return false;
                }

                var ok = await SendTextAsync(buyer, text);
                var retry = Math.Max(0, retryCount);
                for (var i = 0; !ok && i < retry; i++)
                {
                    Log.Info("自动发送失败，准备重试第" + (i + 1) + "次。buyer=" + buyer
                        + ", reason=" + rpa.GetSendFailureReason() + ", text=" + text);
                    rpa.InvalidateChatControls();
                    await Task.Delay(1800);
                    if (!await EnsureActiveBuyerForSendAsync(buyer))
                    {
                        rpa.SetSendFailure("重试会话确认", "无法确认目标买家会话");
                        return false;
                    }
                    ok = await SendTextAsync(buyer, text);
                }
                if (!ok)
                {
                    Log.Error("自动发送最终失败: buyer=" + buyer + ", reason=" + rpa.GetSendFailureReason());
                }
                return ok;
            }
            finally
            {
                _sendGate.Release();
            }
        }

'''
    text = replace_between(
        text,
        "        public async Task<bool> SendTextWithRetryAsync",
        "        private async Task<bool> EnsureActiveBuyerForSendAsync",
        send_retry,
        "QN send retry")

    old_buyer = '''            var sellerNick = message.toid.nick;
            var buyerNick = message.fromid.nick;
            var decision = IncomingMessageSafety.Evaluate(message, messageText, _messageSafetyStartedAt);
'''
    new_buyer = '''            var sellerNick = message.toid.nick;
            var buyerNick = message.fromid.nick;
            MarkBuyerMessageObserved(sellerNick, buyerNick);

            OrderPlacedReplyPlan orderPlan;
            if (OrderPlacedAutoReplyService.TryCreatePlan(
                message,
                messageText,
                sellerNick,
                buyerNick,
                _messageSafetyStartedAt,
                out orderPlan))
            {
                return orderPlan == null
                    ? Task.CompletedTask
                    : ProcessOrderPlacedReplyAsync(orderPlan);
            }

            var decision = IncomingMessageSafety.Evaluate(message, messageText, _messageSafetyStartedAt);
'''
    text = replace_once(text, old_buyer, new_buyer, "QN order hook")

    old_background = '''            if (e != null && e.Seller != null && e.Buyer != null)
            {
                Log.Info("收到后台买家消息通知: seller=" + e.Seller.Nick + ", buyer=" + e.Buyer.Nick);
            }
            if (EvShopRobotReceriveNewMessage != null)
'''
    new_background = '''            if (e != null && e.Seller != null && e.Buyer != null)
            {
                Log.Info("收到后台买家消息通知: seller=" + e.Seller.Nick + ", buyer=" + e.Buyer.Nick);
                ScheduleBackgroundMessageRecovery(e);
            }
            if (EvShopRobotReceriveNewMessage != null)
'''
    text = replace_once(text, old_background, new_background, "QN recovery hook")

    text = replace_once(
        text,
        '''conversationCtl.SetSendResult(sendOk, sendOk ? "已发送（合并本轮买家消息）" : "发送失败：目标买家会话未确认或发送未完成");''',
        '''conversationCtl.SetSendResult(sendOk, sendOk ? "已发送（合并本轮买家消息）" : "发送失败：" + rpa.GetSendFailureReason());''',
        "QN text result detail")
    text = replace_once(
        text,
        '''if (ctl != null) ctl.SetSendResult(sendOk, sendOk ? "已发送（合并图片与本轮消息）" : "识别完成，但目标买家会话未确认，未发送。");''',
        '''if (ctl != null) ctl.SetSendResult(sendOk, sendOk ? "已发送（合并图片与本轮消息）" : "发送失败：" + rpa.GetSendFailureReason());''',
        "QN vision result detail")
    write(p, bom, text)


def patch_options():
    p, bom, text = read("src/Bot/Options/CtlRobotOptions.xaml.cs")

    field_anchor = '''        private CheckBox _notifyDingTalk;
        private TextBox _dingTalkWebhook;
'''
    fields = field_anchor + '''        private CheckBox _orderPlacedReplyEnabled;
        private ComboBox _orderPlacedReplyMode;
        private TextBox _orderPlacedReplyText;
        private TextBox _orderPlacedApiUrl;
        private PasswordBox _orderPlacedApiToken;
        private TextBox _orderPlacedApiTimeout;
        private TextBox _orderPlacedDedupHours;
'''
    text = replace_once(text, field_anchor, fields, "settings fields")

    tab_anchor = '''            _tabs.Items.Add(new TabItem { Header = "自动回复规则", Content = BuildRulesTab() });
            _tabs.Items.Add(new TabItem { Header = "消息策略", Content = BuildPolicyTab() });
'''
    tabs = '''            _tabs.Items.Add(new TabItem { Header = "自动回复规则", Content = BuildRulesTab() });
            _tabs.Items.Add(new TabItem { Header = "消息通知", Content = BuildNotificationTab() });
            _tabs.Items.Add(new TabItem { Header = "消息策略", Content = BuildPolicyTab() });
'''
    text = replace_once(text, tab_anchor, tabs, "settings notification tab")

    methods = '''        private UIElement BuildRulesTab()
        {
            var cfg = BotFeatureStore.GetAutoReplyRules();
            var sp = new StackPanel { Margin = new Thickness(8) };
            _rulesEnabled = new CheckBox { Content = "启用转人工规则", IsChecked = cfg.Enabled, Margin = new Thickness(0, 0, 0, 8), FontWeight = FontWeights.SemiBold };
            sp.Children.Add(_rulesEnabled);
            _manualKeywords = AddLabeledText(sp, "强制转人工关键词", cfg.ManualKeywords, 78, "例：退款、投诉、差评、赔偿、发票、订单隐私。", true);
            _noAutoKeywords = AddLabeledText(sp, "仅人工确认关键词", cfg.NoAutoReplyKeywords, 68, "例：银行卡、身份证、手机号、地址、法律、维权。", true);
            _handoffText = AddLabeledText(sp, "工作时间转人工话术", cfg.HandoffText, 62, "人工在线时命中规则：Bot 不自动发送，只在面板提示并通知人工。", true);

            sp.Children.Add(SectionTitle("买家下单后自动发送"));
            _orderPlacedReplyEnabled = new CheckBox
            {
                Content = "识别到买家新下单后自动发送消息",
                IsChecked = cfg.EnableOrderPlacedReply,
                Margin = new Thickness(0, 0, 0, 8),
                FontWeight = FontWeights.SemiBold
            };
            sp.Children.Add(_orderPlacedReplyEnabled);

            var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            modeRow.Children.Add(new TextBlock { Text = "回复来源", Width = 90, VerticalAlignment = VerticalAlignment.Center });
            _orderPlacedReplyMode = new ComboBox { Width = 190, Height = 26 };
            _orderPlacedReplyMode.Items.Add("固定预设答案");
            _orderPlacedReplyMode.Items.Add("调用HTTP接口");
            _orderPlacedReplyMode.SelectedItem = string.IsNullOrWhiteSpace(cfg.OrderPlacedReplyMode)
                ? "固定预设答案"
                : cfg.OrderPlacedReplyMode;
            modeRow.Children.Add(_orderPlacedReplyMode);
            sp.Children.Add(modeRow);

            _orderPlacedReplyText = AddLabeledText(
                sp,
                "预设/兜底答案",
                cfg.OrderPlacedReplyText,
                72,
                "支持 {客服}、{买家}、{订单号}、{时间}。接口失败时也会使用这段话兜底。",
                true);
            _orderPlacedApiUrl = AddLabeledText(
                sp,
                "HTTP接口",
                cfg.OrderPlacedApiUrl,
                28,
                "POST JSON：event、seller、buyer、orderId、eventTime、message；返回 reply、answer 或 message。",
                false);

            var tokenRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            tokenRow.Children.Add(new TextBlock { Text = "接口令牌", Width = 90, VerticalAlignment = VerticalAlignment.Center });
            _orderPlacedApiToken = new PasswordBox { Password = cfg.OrderPlacedApiToken ?? string.Empty, Height = 26, ToolTip = "可留空；填写后使用 Bearer Token" };
            tokenRow.Children.Add(_orderPlacedApiToken);
            sp.Children.Add(tokenRow);

            var runtimeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            runtimeRow.Children.Add(new TextBlock { Text = "接口超时秒", Width = 90, VerticalAlignment = VerticalAlignment.Center });
            _orderPlacedApiTimeout = new TextBox { Text = cfg.OrderPlacedApiTimeoutSeconds.ToString(), Width = 70, Height = 26 };
            runtimeRow.Children.Add(_orderPlacedApiTimeout);
            runtimeRow.Children.Add(new TextBlock { Text = "同订单去重小时", Width = 120, Margin = new Thickness(20, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            _orderPlacedDedupHours = new TextBox { Text = cfg.OrderPlacedDedupHours.ToString(), Width = 70, Height = 26 };
            runtimeRow.Children.Add(_orderPlacedDedupHours);
            sp.Children.Add(runtimeRow);

            sp.Children.Add(new TextBlock
            {
                Text = "当前仅在 Bot 运行期间识别实时订单卡片，历史订单不会补发。后续兑换码逻辑可由 HTTP 接口按 orderId 返回一次性回复。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                FontSize = 11,
                Margin = new Thickness(0, 5, 0, 0)
            });
            return new ScrollViewer { Content = Card(sp), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        private UIElement BuildNotificationTab()
        {
            var cfg = BotFeatureStore.GetAutoReplyRules();
            var sp = new StackPanel { Margin = new Thickness(8) };

            sp.Children.Add(SectionTitle("人工客服工作时间与下班回复"));
            _workHoursEnabled = new CheckBox { Content = "启用人工客服上下班时间判断", IsChecked = cfg.EnableWorkHours, Margin = new Thickness(0, 0, 0, 8) };
            sp.Children.Add(_workHoursEnabled);
            var workRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            workRow.Children.Add(new TextBlock { Text = "上班时间", Width = 90, VerticalAlignment = VerticalAlignment.Center });
            _workStartTime = new TextBox { Text = cfg.WorkStartTime, Width = 80, Height = 26 };
            workRow.Children.Add(_workStartTime);
            workRow.Children.Add(new TextBlock { Text = "下班时间", Width = 85, Margin = new Thickness(20, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            _workEndTime = new TextBox { Text = cfg.WorkEndTime, Width = 80, Height = 26 };
            workRow.Children.Add(_workEndTime);
            workRow.Children.Add(new TextBlock { Text = "格式 HH:mm；支持跨夜，例如 18:00–09:00。", Margin = new Thickness(12, 4, 0, 0), Foreground = Brushes.Gray });
            sp.Children.Add(workRow);

            var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            modeRow.Children.Add(new TextBlock { Text = "下班回复方式", Width = 90, VerticalAlignment = VerticalAlignment.Center });
            _offHoursMode = new ComboBox { Width = 190, Height = 26 };
            _offHoursMode.Items.Add("AI告知下班时间");
            _offHoursMode.Items.Add("固定预设答案");
            _offHoursMode.SelectedItem = string.IsNullOrWhiteSpace(cfg.OffHoursReplyMode) ? "AI告知下班时间" : cfg.OffHoursReplyMode;
            modeRow.Children.Add(_offHoursMode);
            sp.Children.Add(modeRow);
            _offHoursFixedText = AddLabeledText(sp, "下班固定话术", cfg.OffHoursFixedText, 68, "可使用 {工作时间} 占位符。AI模式调用失败时也使用这段话兜底。", true);

            sp.Children.Add(SectionTitle("转人工通知"));
            _notificationEnabled = new CheckBox { Content = "命中转人工规则时发送通知", IsChecked = cfg.EnableHandoffNotification, Margin = new Thickness(0, 0, 0, 8), FontWeight = FontWeights.SemiBold };
            sp.Children.Add(_notificationEnabled);
            _notificationCooldown = AddLabeledText(sp, "通知去重分钟", cfg.NotificationCooldownMinutes.ToString(), 28, "同一客服、买家和问题在此时间内只通知一次。", false);
            _notifyWeChat = AddChannel(sp, "微信", cfg.NotifyWeChat, "企业微信群机器人 Webhook", cfg.WeChatWebhook, out _weChatWebhook);
            _notifyQQ = AddChannel(sp, "QQ", cfg.NotifyQQ, "QQ机器人 Webhook（需兼容 JSON message/content 字段）", cfg.QQWebhook, out _qqWebhook);
            _notifyFeishu = AddChannel(sp, "飞书", cfg.NotifyFeishu, "飞书群机器人 Webhook", cfg.FeishuWebhook, out _feishuWebhook);
            _notifyDingTalk = AddChannel(sp, "钉钉", cfg.NotifyDingTalk, "钉钉群机器人 Webhook", cfg.DingTalkWebhook, out _dingTalkWebhook);

            var emailHeader = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 6) };
            _notifyEmail = new CheckBox { Content = "邮箱", IsChecked = cfg.NotifyEmail, Width = 90, VerticalAlignment = VerticalAlignment.Center };
            emailHeader.Children.Add(_notifyEmail);
            emailHeader.Children.Add(new TextBlock { Text = "SMTP 通知", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(emailHeader);
            _smtpHost = AddLabeledText(sp, "SMTP服务器", cfg.SmtpHost, 28, string.Empty, false);
            var smtpRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            smtpRow.Children.Add(new TextBlock { Text = "SMTP端口", Width = 90, VerticalAlignment = VerticalAlignment.Center });
            _smtpPort = new TextBox { Text = cfg.SmtpPort.ToString(), Width = 70, Height = 26 };
            smtpRow.Children.Add(_smtpPort);
            _smtpSsl = new CheckBox { Content = "SSL", IsChecked = cfg.SmtpEnableSsl, Margin = new Thickness(16, 3, 0, 0) };
            smtpRow.Children.Add(_smtpSsl);
            sp.Children.Add(smtpRow);
            _smtpUser = AddLabeledText(sp, "SMTP账号", cfg.SmtpUser, 28, string.Empty, false);
            var passwordRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            passwordRow.Children.Add(new TextBlock { Text = "SMTP密码", Width = 90, VerticalAlignment = VerticalAlignment.Center });
            _smtpPassword = new PasswordBox { Password = cfg.SmtpPassword ?? string.Empty, Height = 26 };
            passwordRow.Children.Add(_smtpPassword);
            sp.Children.Add(passwordRow);
            _emailTo = AddLabeledText(sp, "收件邮箱", cfg.EmailTo, 28, "多个收件人用逗号分隔。", false);

            var test = MakeButton("测试已选通知", 120);
            test.Click += async (s, e) =>
            {
                test.IsEnabled = false;
                _status.Text = "正在发送测试通知...";
                try
                {
                    _status.Text = await HandoffNotificationService.TestAsync(BuildRuleConfigFromUi());
                }
                catch (Exception ex)
                {
                    _status.Text = "测试通知失败：" + ex.Message;
                }
                finally
                {
                    test.IsEnabled = true;
                }
            };
            sp.Children.Add(test);
            sp.Children.Add(new TextBlock
            {
                Text = "企业微信应用消息由 Ubuntu 控制面配置；微信、QQ、飞书、钉钉 Webhook 与 SMTP 密码保存在本机 params.db。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                FontSize = 11,
                Margin = new Thickness(0, 5, 0, 0)
            });
            return new ScrollViewer { Content = Card(sp), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

'''
    text = replace_between(
        text,
        "        private UIElement BuildRulesTab()",
        "        private TextBlock SectionTitle",
        methods,
        "settings rules and notifications")

    old_parse = '''            int smtpPort;
            if (!int.TryParse(_smtpPort == null ? "465" : _smtpPort.Text, out smtpPort)) smtpPort = 465;
            return new AutoReplyRuleConfig
'''
    new_parse = '''            int smtpPort;
            if (!int.TryParse(_smtpPort == null ? "465" : _smtpPort.Text, out smtpPort)) smtpPort = 465;
            int orderTimeout;
            if (!int.TryParse(_orderPlacedApiTimeout == null ? "10" : _orderPlacedApiTimeout.Text, out orderTimeout)) orderTimeout = 10;
            int orderDedupHours;
            if (!int.TryParse(_orderPlacedDedupHours == null ? "72" : _orderPlacedDedupHours.Text, out orderDedupHours)) orderDedupHours = 72;
            return new AutoReplyRuleConfig
'''
    text = replace_once(text, old_parse, new_parse, "settings order parse")

    old_end = '''                NotifyDingTalk = _notifyDingTalk != null && (_notifyDingTalk.IsChecked ?? false),
                DingTalkWebhook = _dingTalkWebhook == null ? string.Empty : _dingTalkWebhook.Text
'''
    new_end = '''                NotifyDingTalk = _notifyDingTalk != null && (_notifyDingTalk.IsChecked ?? false),
                DingTalkWebhook = _dingTalkWebhook == null ? string.Empty : _dingTalkWebhook.Text,
                EnableOrderPlacedReply = _orderPlacedReplyEnabled != null && (_orderPlacedReplyEnabled.IsChecked ?? false),
                OrderPlacedReplyMode = _orderPlacedReplyMode == null || _orderPlacedReplyMode.SelectedItem == null
                    ? "固定预设答案"
                    : _orderPlacedReplyMode.SelectedItem.ToString(),
                OrderPlacedReplyText = _orderPlacedReplyText == null ? string.Empty : _orderPlacedReplyText.Text,
                OrderPlacedApiUrl = _orderPlacedApiUrl == null ? string.Empty : _orderPlacedApiUrl.Text,
                OrderPlacedApiToken = _orderPlacedApiToken == null ? string.Empty : _orderPlacedApiToken.Password,
                OrderPlacedApiTimeoutSeconds = Math.Max(3, Math.Min(60, orderTimeout)),
                OrderPlacedDedupHours = Math.Max(1, Math.Min(720, orderDedupHours))
'''
    text = replace_once(text, old_end, new_end, "settings order save")

    prop_anchor = '''        public bool NotifyDingTalk { get; set; }
        public string DingTalkWebhook { get; set; }
'''
    props = prop_anchor + '''        public bool EnableOrderPlacedReply { get; set; }
        public string OrderPlacedReplyMode { get; set; }
        public string OrderPlacedReplyText { get; set; }
        public string OrderPlacedApiUrl { get; set; }
        public string OrderPlacedApiToken { get; set; }
        public int OrderPlacedApiTimeoutSeconds { get; set; }
        public int OrderPlacedDedupHours { get; set; }
'''
    text = replace_once(text, prop_anchor, props, "config order properties")

    default_anchor = '''                EnableHandoffNotification = false,
                NotificationCooldownMinutes = 10,
                SmtpPort = 465,
                SmtpEnableSsl = true
'''
    defaults = '''                EnableHandoffNotification = false,
                NotificationCooldownMinutes = 10,
                SmtpPort = 465,
                SmtpEnableSsl = true,
                EnableOrderPlacedReply = false,
                OrderPlacedReplyMode = "固定预设答案",
                OrderPlacedReplyText = "亲，您的订单已收到，我们会尽快为您处理。",
                OrderPlacedApiUrl = string.Empty,
                OrderPlacedApiToken = string.Empty,
                OrderPlacedApiTimeoutSeconds = 10,
                OrderPlacedDedupHours = 72
'''
    text = replace_once(text, default_anchor, defaults, "config order defaults")

    normalize_anchor = '''            if (cfg.SmtpPort <= 0) cfg.SmtpPort = 465;
            return cfg;
'''
    normalize = '''            if (cfg.SmtpPort <= 0) cfg.SmtpPort = 465;
            if (string.IsNullOrWhiteSpace(cfg.OrderPlacedReplyMode)) cfg.OrderPlacedReplyMode = "固定预设答案";
            if (string.IsNullOrWhiteSpace(cfg.OrderPlacedReplyText)) cfg.OrderPlacedReplyText = AutoReplyRuleConfig.Default().OrderPlacedReplyText;
            if (cfg.OrderPlacedApiTimeoutSeconds <= 0) cfg.OrderPlacedApiTimeoutSeconds = 10;
            if (cfg.OrderPlacedDedupHours <= 0) cfg.OrderPlacedDedupHours = 72;
            return cfg;
'''
    text = replace_once(text, normalize_anchor, normalize, "config order normalize")
    write(p, bom, text)


def main():
    patch_targets()
    patch_qnrpa()
    patch_qn()
    patch_options()
    print("MIGRATION_OK")


if __name__ == "__main__":
    main()
