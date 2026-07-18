from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def write(path, text):
    (ROOT / path).write_text(text, encoding="utf-8-sig")


def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"patch anchor {label!r} count={count}")
    return text.replace(old, new, 1)


# 1. Make answer source an explicit, durable card property.
path = "src/Bot/AssistWindow/Widget/Robot/CtlConversation.xaml"
text = read(path)
old = '''                    <StackPanel Orientation="Horizontal" Margin="0 5 0 0">
                        <TextBlock x:Name="txtSource" FontSize="11" FontWeight="SemiBold" Visibility="Collapsed" />
                        <TextBlock Text="  ·  " Foreground="{StaticResource MutedText}" FontSize="11" />
                        <TextBlock x:Name="txtStatus" Foreground="{StaticResource SuccessGreen}" FontSize="11" />'''
new = '''                    <StackPanel Orientation="Horizontal" Margin="0 5 0 0">
                        <Border x:Name="bdSource" CornerRadius="4" Padding="5 1" Margin="0 0 5 0" Visibility="Collapsed">
                            <TextBlock x:Name="txtSource" FontSize="11" FontWeight="SemiBold" />
                        </Border>
                        <TextBlock x:Name="txtSourceSeparator" Text="·  " Foreground="{StaticResource MutedText}" FontSize="11" Visibility="Collapsed" />
                        <TextBlock x:Name="txtStatus" Foreground="{StaticResource SuccessGreen}" FontSize="11" />'''
text = replace_once(text, old, new, "source badge xaml")
write(path, text)

path = "src/Bot/AssistWindow/Widget/Robot/CtlConversation.xaml.cs"
text = read(path)
text = replace_once(
    text,
    '        public static CtlConversation Create(string seller, string buyer, string question, string answer, bool isAutoReply = false)\n        {\n            var dlg = new CtlConversation();\n            dlg.Setup(seller, buyer, question, answer, isAutoReply);\n            return dlg;\n        }\n\n        public void Setup(string seller, string buyer, string question, string answer, bool isAutoReply)',
    '        public static CtlConversation Create(string seller, string buyer, string question, string answer, bool isAutoReply = false, string answerSource = "")\n        {\n            var dlg = new CtlConversation();\n            dlg.Setup(seller, buyer, question, answer, isAutoReply, answerSource);\n            return dlg;\n        }\n\n        public void Setup(string seller, string buyer, string question, string answer, bool isAutoReply, string answerSource)',
    "conversation source signature")
text = replace_once(
    text,
    '            SetSource(KnowledgeLearningService.ResolveAnswerSource(_seller, _buyer, _question, _answer));',
    '            var resolvedSource = string.IsNullOrWhiteSpace(answerSource)\n                ? KnowledgeLearningService.ResolveAnswerSource(_seller, _buyer, _question, _answer)\n                : answerSource;\n            SetSource(resolvedSource);',
    "conversation source setup")
text = replace_once(
    text,
    '        public void SetAnswer(string answer)\n        {\n            _answer = answer ?? string.Empty;\n            Ui(() =>\n            {\n                txtAnswer.Text = _answer;\n                var source = KnowledgeLearningService.ResolveAnswerSource(_seller, _buyer, _question, _answer);\n                if (!string.IsNullOrWhiteSpace(source)) SetSource(source);\n                txtTime.Text = DateTime.Now.ToString("HH:mm:ss");\n            });\n        }',
    '        public void SetAnswer(string answer, string answerSource = "")\n        {\n            _answer = answer ?? string.Empty;\n            Ui(() =>\n            {\n                txtAnswer.Text = _answer;\n                var source = string.IsNullOrWhiteSpace(answerSource)\n                    ? KnowledgeLearningService.ResolveAnswerSource(_seller, _buyer, _question, _answer)\n                    : answerSource;\n                if (!string.IsNullOrWhiteSpace(source)) SetSource(source);\n                txtTime.Text = DateTime.Now.ToString("HH:mm:ss");\n            });\n        }',
    "conversation explicit source setter")
old = '''                txtSource.Text = source ?? string.Empty;
                txtSource.Visibility = string.IsNullOrWhiteSpace(source) ? Visibility.Collapsed : Visibility.Visible;
                txtSource.Foreground = new SolidColorBrush(source == "本地" ? Color.FromRgb(39, 174, 96) : source.StartsWith("人工") ? Color.FromRgb(155, 81, 224) : Color.FromRgb(47, 128, 237));'''
new = '''                source = (source ?? string.Empty).Trim();
                var visible = !string.IsNullOrWhiteSpace(source);
                txtSource.Text = source;
                bdSource.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                txtSourceSeparator.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                var local = source == "本地";
                var manual = source.StartsWith("人工", StringComparison.Ordinal);
                txtSource.Foreground = new SolidColorBrush(local
                    ? Color.FromRgb(22, 101, 52)
                    : manual ? Color.FromRgb(107, 33, 168) : Color.FromRgb(29, 78, 216));
                bdSource.Background = new SolidColorBrush(local
                    ? Color.FromRgb(220, 252, 231)
                    : manual ? Color.FromRgb(243, 232, 255) : Color.FromRgb(219, 234, 254));'''
text = replace_once(text, old, new, "source badge rendering")
write(path, text)

path = "src/Bot/AssistWindow/Widget/Robot/CtlRobot.xaml.cs"
text = read(path)
text = replace_once(
    text,
    '        public CtlConversation AddConversation(string seller, string buyer, string question, string answer, bool isAutoReply = false)',
    '        public CtlConversation AddConversation(string seller, string buyer, string question, string answer, bool isAutoReply = false, string answerSource = "")',
    "robot add conversation signature")
text = replace_once(
    text,
    '            var ctlConversation = CtlConversation.Create(seller, buyer, question, answer, isAutoReply);',
    '            var ctlConversation = CtlConversation.Create(seller, buyer, question, answer, isAutoReply, answerSource);',
    "robot pass source")
write(path, text)

path = "src/Bot/ChromeNs/QN.cs"
text = read(path)
text = replace_once(
    text,
    '            var answer = MyOpenAI.GetAnswer(sellerNick, buyerNick, messageText);\n            var conversationCtl = Desk.Inst == null ? null : Desk.Inst.AddConversation(sellerNick, buyerNick, messageText, answer, autoSend);',
    '            var answer = MyOpenAI.GetAnswer(sellerNick, buyerNick, messageText);\n            var answerSource = KnowledgeLearningService.ResolveAnswerSource(sellerNick, buyerNick, messageText, answer);\n            var conversationCtl = Desk.Inst == null ? null : Desk.Inst.AddConversation(sellerNick, buyerNick, messageText, answer, autoSend, answerSource);',
    "explicit text answer source")
text = replace_once(
    text,
    '            if (ctl != null) ctl.SetAnswer(result.Answer);',
    '            if (ctl != null) ctl.SetAnswer(result.Answer, "AI生成");',
    "explicit vision source")
write(path, text)

path = "src/Bot/ChromeNs/MyOpenAI.cs"
text = read(path)
old = '''                if (KnowledgeLearningService.TryFindLocalAnswer(seller, buyer, question, out localKnowledge, out localScore))
                {
                    KnowledgeLearningService.RegisterAnswerSource(seller, buyer, question, localKnowledge.Answer, "本地");
                    Log.Info("命中本地知识库，未调用AI。buyer=" + buyer + ", knowledgeId=" + localKnowledge.Id + ", score=" + localScore.ToString("0.00"));
                    return BotFeatureStore.ApplyOutputPolicy(localKnowledge.Answer);
                }

                string manualAnswer;
                string manualReason;
                if (BotFeatureStore.TryMatchManualRule(question, out manualAnswer, out manualReason))
                {
                    return "错误：命中人工确认规则，未自动回复。" + manualAnswer + " 原因：" + manualReason;
                }'''
new = '''                if (KnowledgeLearningService.TryFindLocalAnswer(seller, buyer, question, out localKnowledge, out localScore))
                {
                    var localAnswer = BotFeatureStore.ApplyOutputPolicy(localKnowledge.Answer);
                    KnowledgeLearningService.RegisterAnswerSource(seller, buyer, question, localAnswer, "本地");
                    Log.Info("命中本地知识库，未调用AI。buyer=" + buyer + ", knowledgeId=" + localKnowledge.Id + ", score=" + localScore.ToString("0.00"));
                    return localAnswer;
                }

                var manualDecision = BotFeatureStore.EvaluateAutoReplyRule(question);
                if (manualDecision.Matched)
                {
                    HandoffNotificationService.QueueNotify(seller, buyer, question, manualDecision);
                    if (!manualDecision.AllowAutoReply)
                    {
                        return "错误：命中人工确认规则，未自动回复。" + manualDecision.ReplyText + " 原因：" + manualDecision.Reason;
                    }

                    var offHoursAnswer = manualDecision.UseAiReply
                        ? BuildOffHoursHandoffReply(seller, buyer, question, manualDecision)
                        : BotFeatureStore.ApplyOutputPolicy(manualDecision.ReplyText);
                    var offHoursSource = manualDecision.UseAiReply ? "AI生成" : "本地";
                    KnowledgeLearningService.RegisterAnswerSource(seller, buyer, question, offHoursAnswer, offHoursSource);
                    return offHoursAnswer;
                }'''
text = replace_once(text, old, new, "manual rule decision")
insert_anchor = '        public static string GetAnswer(string seller, string buyer, string question)\n'
method = '''        private static string BuildOffHoursHandoffReply(
            string seller,
            string buyer,
            string question,
            AutoReplyRuleDecision decision)
        {
            var fallback = BotFeatureStore.ApplyOutputPolicy(decision.ReplyText);
            try
            {
                if (!EnsureConfig()) return fallback;
                var messages = new JArray
                {
                    CreateMessage("system",
                        "你是电商店铺的下班转人工助手。当前人工客服已下班。你只能礼貌告知人工客服不在线、工作时间，以及问题已记录或建议买家在上班时间联系；不得回答退款、投诉、赔偿、隐私、订单核验等具体高风险结论。回复一句到两句，禁止编造。"),
                    CreateMessage("user",
                        "人工客服工作时间：" + decision.WorkHoursText
                        + "\\n触发原因：" + decision.Reason
                        + "\\n买家问题：" + question)
                };
                foreach (var endpoint in AiEndpointStore.GetEnabledEndpoints())
                {
                    var result = CallChatCompletions(endpoint, messages);
                    BotRuntimeStats.RecordAiCall(endpoint, result.InputTokens, result.OutputTokens, result.Success, result.LatencyMs, result.Success ? "下班转人工回复成功" : result.Error);
                    if (result.Success && !string.IsNullOrWhiteSpace(result.Answer))
                    {
                        return BotFeatureStore.ApplyOutputPolicy(result.Answer);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Info("生成下班转人工回复失败，使用固定兜底话术：" + ex.Message);
            }
            return fallback;
        }

'''
text = replace_once(text, insert_anchor, method + insert_anchor, "off hours ai method")
write(path, text)

# 2. Move history scan from QA management to Smart Import.
path = "src/Bot/Knowledge/KnowledgeManagerControl.cs"
text = read(path)
text = replace_once(text, '            AddBtn(top, "扫描历史聊天记录", 140, (s, e) => ScanHistory());\n', '', "remove manager scan button")
old = '''        private void ScanHistory()
        {
            var window = new ChatHistoryScanWindow
            {
                Owner = Window.GetWindow(this)
            };
            window.ShowDialog();
            RefreshData();
        }

'''
text = replace_once(text, old, '', "remove manager scan method")
write(path, text)

path = "src/Bot/Knowledge/KnowledgeImportControl.cs"
text = read(path)
text = replace_once(
    text,
    '            var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,0,0,8) };',
    '            var btns = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,0,0,8) };',
    "import buttons wrap")
old = '            btns.Children.Add(new TextBlock{Text="AI分析超时：",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,2,0)}); _timeout=new TextBox{Width=58,Height=28,Text=global::Bot.ChromeNs.BotFeatureStore.GetSmartImportTimeoutSeconds().ToString()}; btns.Children.Add(_timeout); btns.Children.Add(new TextBlock{Text=" 秒",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(2,0,8,0)}); _start = Btn("开始智能导入",130); _start.Click += async (s,e)=>await StartImport(); btns.Children.Add(_start); _cancel=Btn("取消",70); _cancel.IsEnabled=false; _cancel.Click+=(s,e)=>{_cancelSource=SmartImportCancelSource.UserCancel; if(_cts!=null)_cts.Cancel();}; btns.Children.Add(_cancel);'
new = '            btns.Children.Add(new TextBlock{Text="AI分析超时：",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,2,0)}); _timeout=new TextBox{Width=58,Height=28,Text=global::Bot.ChromeNs.BotFeatureStore.GetSmartImportTimeoutSeconds().ToString()}; btns.Children.Add(_timeout); btns.Children.Add(new TextBlock{Text=" 秒",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(2,0,8,0)}); _start = Btn("开始智能导入",130); _start.Click += async (s,e)=>await StartImport(); btns.Children.Add(_start); var scan=Btn("扫描历史聊天记录",150); scan.Click+=(s,e)=>ScanHistory(); btns.Children.Add(scan); _cancel=Btn("取消",70); _cancel.IsEnabled=false; _cancel.Click+=(s,e)=>{_cancelSource=SmartImportCancelSource.UserCancel; if(_cts!=null)_cts.Cancel();}; btns.Children.Add(_cancel);'
text = replace_once(text, old, new, "add scan to import")
anchor = '        private void DeleteSelected(){ var it=_media.SelectedItem as KnowledgeMediaItem; if(it==null)return; _data.Images.Remove(it); _data.Videos.Remove(it); Refresh(); }\n'
method = '        private void ScanHistory(){ var wnd=new ChatHistoryScanWindow{Owner=Window.GetWindow(this)}; wnd.ShowDialog(); _status.Text="历史聊天扫描窗口已关闭；新增知识可在问答管理中查看。"; }\n'
text = replace_once(text, anchor, anchor + method, "import scan method")
write(path, text)

# 3. Add work-hours decision model and notification configuration.
path = "src/Bot/Options/CtlRobotOptions.xaml.cs"
text = read(path)
old = '''    public class AutoReplyRuleConfig
    {
        public bool Enabled { get; set; }
        public string ManualKeywords { get; set; }
        public string NoAutoReplyKeywords { get; set; }
        public string HandoffText { get; set; }

        public static AutoReplyRuleConfig Default()
        {
            return new AutoReplyRuleConfig
            {
                Enabled = true,
                ManualKeywords = "退款,退货,投诉,差评,赔偿,发票,税票,订单隐私,身份证,银行卡,法律,维权,平台介入",
                NoAutoReplyKeywords = "手机号,地址,隐私,密码,账号,验证码,转账,补偿,客服主管",
                HandoffText = "这个问题建议转人工确认后再回复，避免承诺错误。参考话术：亲，这个问题我帮您转人工客服确认一下。"
            };
        }
    }
'''
new = '''    public class AutoReplyRuleConfig
    {
        public bool Enabled { get; set; }
        public string ManualKeywords { get; set; }
        public string NoAutoReplyKeywords { get; set; }
        public string HandoffText { get; set; }
        public bool EnableWorkHours { get; set; }
        public string WorkStartTime { get; set; }
        public string WorkEndTime { get; set; }
        public string OffHoursReplyMode { get; set; }
        public string OffHoursFixedText { get; set; }
        public bool EnableHandoffNotification { get; set; }
        public int NotificationCooldownMinutes { get; set; }
        public bool NotifyWeChat { get; set; }
        public string WeChatWebhook { get; set; }
        public bool NotifyQQ { get; set; }
        public string QQWebhook { get; set; }
        public bool NotifyEmail { get; set; }
        public string SmtpHost { get; set; }
        public int SmtpPort { get; set; }
        public bool SmtpEnableSsl { get; set; }
        public string SmtpUser { get; set; }
        public string SmtpPassword { get; set; }
        public string EmailTo { get; set; }
        public bool NotifyFeishu { get; set; }
        public string FeishuWebhook { get; set; }
        public bool NotifyDingTalk { get; set; }
        public string DingTalkWebhook { get; set; }

        public static AutoReplyRuleConfig Default()
        {
            return new AutoReplyRuleConfig
            {
                Enabled = true,
                ManualKeywords = "退款,退货,投诉,差评,赔偿,发票,税票,订单隐私,身份证,银行卡,法律,维权,平台介入",
                NoAutoReplyKeywords = "手机号,地址,隐私,密码,账号,验证码,转账,补偿,客服主管",
                HandoffText = "亲，这个问题需要人工客服核实，我已为您转人工处理。",
                EnableWorkHours = true,
                WorkStartTime = "09:00",
                WorkEndTime = "18:00",
                OffHoursReplyMode = "AI告知下班时间",
                OffHoursFixedText = "亲，人工客服当前已下班，工作时间为每天 {工作时间}。您的问题已记录，请在上班时间联系或等待人工处理。",
                EnableHandoffNotification = false,
                NotificationCooldownMinutes = 10,
                SmtpPort = 465,
                SmtpEnableSsl = true
            };
        }
    }

    public class AutoReplyRuleDecision
    {
        public bool Matched { get; set; }
        public bool AllowAutoReply { get; set; }
        public bool UseAiReply { get; set; }
        public bool IsOffHours { get; set; }
        public string HitKeyword { get; set; }
        public string Reason { get; set; }
        public string ReplyText { get; set; }
        public string WorkHoursText { get; set; }
    }
'''
text = replace_once(text, old, new, "rule config model")
text = replace_once(
    text,
    '        public static AutoReplyRuleConfig GetAutoReplyRules()\n        {\n            return Read(RuleKey, AutoReplyRuleConfig.Default()) ?? AutoReplyRuleConfig.Default();\n        }',
    '''        public static AutoReplyRuleConfig GetAutoReplyRules()
        {
            var cfg = Read(RuleKey, AutoReplyRuleConfig.Default()) ?? AutoReplyRuleConfig.Default();
            if (string.IsNullOrWhiteSpace(cfg.WorkStartTime) && string.IsNullOrWhiteSpace(cfg.WorkEndTime))
            {
                cfg.EnableWorkHours = true;
            }
            if (string.IsNullOrWhiteSpace(cfg.WorkStartTime)) cfg.WorkStartTime = "09:00";
            if (string.IsNullOrWhiteSpace(cfg.WorkEndTime)) cfg.WorkEndTime = "18:00";
            if (string.IsNullOrWhiteSpace(cfg.OffHoursReplyMode)) cfg.OffHoursReplyMode = "AI告知下班时间";
            if (string.IsNullOrWhiteSpace(cfg.OffHoursFixedText)) cfg.OffHoursFixedText = AutoReplyRuleConfig.Default().OffHoursFixedText;
            if (cfg.NotificationCooldownMinutes <= 0) cfg.NotificationCooldownMinutes = 10;
            if (cfg.SmtpPort <= 0) cfg.SmtpPort = 465;
            return cfg;
        }''',
    "normalize old rule config")
old = '''        public static bool TryMatchManualRule(string question, out string answer, out string reason)
        {
            var cfg = GetAutoReplyRules();
            answer = string.Empty;
            reason = string.Empty;
            if (cfg == null || !cfg.Enabled) return false;
            string hit;
            if (ContainsAny(question, cfg.ManualKeywords, out hit))
            {
                reason = "命中强制转人工关键词：" + hit;
                answer = string.IsNullOrWhiteSpace(cfg.HandoffText) ? AutoReplyRuleConfig.Default().HandoffText : cfg.HandoffText;
                return true;
            }
            if (ContainsAny(question, cfg.NoAutoReplyKeywords, out hit))
            {
                reason = "命中仅人工确认关键词：" + hit;
                answer = string.IsNullOrWhiteSpace(cfg.HandoffText) ? AutoReplyRuleConfig.Default().HandoffText : cfg.HandoffText;
                return true;
            }
            return false;
        }
'''
new = '''        public static AutoReplyRuleDecision EvaluateAutoReplyRule(string question)
        {
            var decision = new AutoReplyRuleDecision();
            var cfg = GetAutoReplyRules();
            if (cfg == null || !cfg.Enabled) return decision;

            string hit;
            if (ContainsAny(question, cfg.ManualKeywords, out hit))
            {
                decision.Matched = true;
                decision.HitKeyword = hit;
                decision.Reason = "命中强制转人工关键词：" + hit;
            }
            else if (ContainsAny(question, cfg.NoAutoReplyKeywords, out hit))
            {
                decision.Matched = true;
                decision.HitKeyword = hit;
                decision.Reason = "命中仅人工确认关键词：" + hit;
            }
            if (!decision.Matched) return decision;

            decision.WorkHoursText = GetWorkHoursText(cfg);
            decision.IsOffHours = cfg.EnableWorkHours && !IsHumanServiceOnline(cfg, DateTime.Now);
            if (!decision.IsOffHours)
            {
                decision.AllowAutoReply = false;
                decision.ReplyText = string.IsNullOrWhiteSpace(cfg.HandoffText)
                    ? AutoReplyRuleConfig.Default().HandoffText
                    : cfg.HandoffText.Trim();
                return decision;
            }

            decision.AllowAutoReply = true;
            decision.UseAiReply = string.Equals(
                cfg.OffHoursReplyMode,
                "AI告知下班时间",
                StringComparison.Ordinal);
            var fixedText = string.IsNullOrWhiteSpace(cfg.OffHoursFixedText)
                ? AutoReplyRuleConfig.Default().OffHoursFixedText
                : cfg.OffHoursFixedText.Trim();
            decision.ReplyText = fixedText.Replace("{工作时间}", decision.WorkHoursText);
            decision.Reason += "；当前为人工客服下班时间";
            return decision;
        }

        public static bool TryMatchManualRule(string question, out string answer, out string reason)
        {
            var decision = EvaluateAutoReplyRule(question);
            answer = decision.ReplyText ?? string.Empty;
            reason = decision.Reason ?? string.Empty;
            return decision.Matched;
        }

        public static bool IsHumanServiceOnline(AutoReplyRuleConfig cfg, DateTime now)
        {
            if (cfg == null || !cfg.EnableWorkHours) return true;
            TimeSpan start;
            TimeSpan end;
            if (!TimeSpan.TryParse(cfg.WorkStartTime, out start)) start = new TimeSpan(9, 0, 0);
            if (!TimeSpan.TryParse(cfg.WorkEndTime, out end)) end = new TimeSpan(18, 0, 0);
            var current = now.TimeOfDay;
            if (start == end) return true;
            if (start < end) return current >= start && current < end;
            return current >= start || current < end;
        }

        public static string GetWorkHoursText(AutoReplyRuleConfig cfg)
        {
            cfg = cfg ?? AutoReplyRuleConfig.Default();
            var start = string.IsNullOrWhiteSpace(cfg.WorkStartTime) ? "09:00" : cfg.WorkStartTime.Trim();
            var end = string.IsNullOrWhiteSpace(cfg.WorkEndTime) ? "18:00" : cfg.WorkEndTime.Trim();
            return "每天 " + start + "–" + end;
        }
'''
text = replace_once(text, old, new, "rule evaluation")

# Feature settings fields.
text = replace_once(
    text,
    '        private CheckBox _rulesEnabled;\n        private ComboBox _tone;',
    '''        private CheckBox _rulesEnabled;
        private CheckBox _workHoursEnabled;
        private TextBox _workStartTime;
        private TextBox _workEndTime;
        private ComboBox _offHoursMode;
        private TextBox _offHoursFixedText;
        private CheckBox _notificationEnabled;
        private TextBox _notificationCooldown;
        private CheckBox _notifyWeChat;
        private TextBox _weChatWebhook;
        private CheckBox _notifyQQ;
        private TextBox _qqWebhook;
        private CheckBox _notifyEmail;
        private TextBox _smtpHost;
        private TextBox _smtpPort;
        private CheckBox _smtpSsl;
        private TextBox _smtpUser;
        private PasswordBox _smtpPassword;
        private TextBox _emailTo;
        private CheckBox _notifyFeishu;
        private TextBox _feishuWebhook;
        private CheckBox _notifyDingTalk;
        private TextBox _dingTalkWebhook;
        private ComboBox _tone;''',
    "feature rule fields")
old_start = text.index('        private UIElement BuildRulesTab()')
old_end = text.index('        private UIElement BuildPolicyTab()', old_start)
new_method = '''        private UIElement BuildRulesTab()
        {
            var cfg = BotFeatureStore.GetAutoReplyRules();
            var sp = new StackPanel { Margin = new Thickness(8) };
            _rulesEnabled = new CheckBox { Content = "启用转人工规则", IsChecked = cfg.Enabled, Margin = new Thickness(0, 0, 0, 8), FontWeight = FontWeights.SemiBold };
            sp.Children.Add(_rulesEnabled);
            _manualKeywords = AddLabeledText(sp, "强制转人工关键词", cfg.ManualKeywords, 78, "例：退款、投诉、差评、赔偿、发票、订单隐私。", true);
            _noAutoKeywords = AddLabeledText(sp, "仅人工确认关键词", cfg.NoAutoReplyKeywords, 68, "例：银行卡、身份证、手机号、地址、法律、维权。", true);
            _handoffText = AddLabeledText(sp, "工作时间转人工话术", cfg.HandoffText, 62, "人工在线时命中规则：Bot 不自动发送，只在面板提示并通知人工。", true);

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
                Text = "微信使用企业微信群机器人；QQ使用兼容Webhook；飞书、钉钉使用群机器人；邮箱使用SMTP。密钥和密码仅保存在本机 params.db。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                FontSize = 11,
                Margin = new Thickness(0, 5, 0, 0)
            });
            return new ScrollViewer { Content = Card(sp), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        private TextBlock SectionTitle(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontWeight = FontWeights.Bold,
                FontSize = 15,
                Margin = new Thickness(0, 14, 0, 9),
                Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55))
            };
        }

        private CheckBox AddChannel(StackPanel sp, string name, bool enabled, string label, string value, out TextBox box)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var check = new CheckBox { Content = name, IsChecked = enabled, Width = 90, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(check);
            box = new TextBox { Text = value ?? string.Empty, Height = 26, ToolTip = label };
            row.Children.Add(box);
            sp.Children.Add(row);
            return check;
        }

        private AutoReplyRuleConfig BuildRuleConfigFromUi()
        {
            int cooldown;
            if (!int.TryParse(_notificationCooldown == null ? "10" : _notificationCooldown.Text, out cooldown)) cooldown = 10;
            int smtpPort;
            if (!int.TryParse(_smtpPort == null ? "465" : _smtpPort.Text, out smtpPort)) smtpPort = 465;
            return new AutoReplyRuleConfig
            {
                Enabled = _rulesEnabled == null || (_rulesEnabled.IsChecked ?? true),
                ManualKeywords = _manualKeywords == null ? string.Empty : _manualKeywords.Text,
                NoAutoReplyKeywords = _noAutoKeywords == null ? string.Empty : _noAutoKeywords.Text,
                HandoffText = _handoffText == null ? string.Empty : _handoffText.Text,
                EnableWorkHours = _workHoursEnabled != null && (_workHoursEnabled.IsChecked ?? false),
                WorkStartTime = _workStartTime == null ? "09:00" : _workStartTime.Text,
                WorkEndTime = _workEndTime == null ? "18:00" : _workEndTime.Text,
                OffHoursReplyMode = _offHoursMode == null || _offHoursMode.SelectedItem == null ? "AI告知下班时间" : _offHoursMode.SelectedItem.ToString(),
                OffHoursFixedText = _offHoursFixedText == null ? string.Empty : _offHoursFixedText.Text,
                EnableHandoffNotification = _notificationEnabled != null && (_notificationEnabled.IsChecked ?? false),
                NotificationCooldownMinutes = Math.Max(1, Math.Min(1440, cooldown)),
                NotifyWeChat = _notifyWeChat != null && (_notifyWeChat.IsChecked ?? false),
                WeChatWebhook = _weChatWebhook == null ? string.Empty : _weChatWebhook.Text,
                NotifyQQ = _notifyQQ != null && (_notifyQQ.IsChecked ?? false),
                QQWebhook = _qqWebhook == null ? string.Empty : _qqWebhook.Text,
                NotifyEmail = _notifyEmail != null && (_notifyEmail.IsChecked ?? false),
                SmtpHost = _smtpHost == null ? string.Empty : _smtpHost.Text,
                SmtpPort = smtpPort,
                SmtpEnableSsl = _smtpSsl != null && (_smtpSsl.IsChecked ?? true),
                SmtpUser = _smtpUser == null ? string.Empty : _smtpUser.Text,
                SmtpPassword = _smtpPassword == null ? string.Empty : _smtpPassword.Password,
                EmailTo = _emailTo == null ? string.Empty : _emailTo.Text,
                NotifyFeishu = _notifyFeishu != null && (_notifyFeishu.IsChecked ?? false),
                FeishuWebhook = _feishuWebhook == null ? string.Empty : _feishuWebhook.Text,
                NotifyDingTalk = _notifyDingTalk != null && (_notifyDingTalk.IsChecked ?? false),
                DingTalkWebhook = _dingTalkWebhook == null ? string.Empty : _dingTalkWebhook.Text
            };
        }

'''
text = text[:old_start] + new_method + text[old_end:]
text = replace_once(
    text,
    '''                    BotFeatureStore.SaveAutoReplyRules(new AutoReplyRuleConfig
                    {
                        Enabled = _rulesEnabled.IsChecked ?? true,
                        ManualKeywords = _manualKeywords == null ? string.Empty : _manualKeywords.Text,
                        NoAutoReplyKeywords = _noAutoKeywords == null ? string.Empty : _noAutoKeywords.Text,
                        HandoffText = _handoffText == null ? string.Empty : _handoffText.Text
                    });''',
    '                    BotFeatureStore.SaveAutoReplyRules(BuildRuleConfigFromUi());',
    "save extended rules")
write(path, text)

# 4. Notification service.
notification = r'''using Bot.Options;
using BotLib;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal static class HandoffNotificationService
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private static readonly ConcurrentDictionary<string, DateTime> Recent = new ConcurrentDictionary<string, DateTime>();

        public static void QueueNotify(
            string seller,
            string buyer,
            string question,
            AutoReplyRuleDecision decision)
        {
            var cfg = BotFeatureStore.GetAutoReplyRules();
            if (cfg == null || !cfg.EnableHandoffNotification || decision == null || !decision.Matched) return;
            var key = Normalize(seller) + "#" + Normalize(buyer) + "#" + Normalize(question);
            var now = DateTime.Now;
            DateTime until;
            if (Recent.TryGetValue(key, out until) && until > now) return;
            Recent[key] = now.AddMinutes(Math.Max(1, cfg.NotificationCooldownMinutes));
            Task.Run(async () =>
            {
                try
                {
                    var result = await SendAsync(cfg, BuildMessage(seller, buyer, question, decision));
                    Log.Info("转人工通知结果：" + result);
                }
                catch (Exception ex)
                {
                    Log.Info("转人工通知异常：" + ex.Message);
                }
            });
        }

        public static async Task<string> TestAsync(AutoReplyRuleConfig cfg)
        {
            cfg = cfg ?? AutoReplyRuleConfig.Default();
            return await SendAsync(cfg,
                "【千牛Bot测试通知】\n时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                + "\n这是一条转人工通知通道测试消息。");
        }

        private static string BuildMessage(
            string seller,
            string buyer,
            string question,
            AutoReplyRuleDecision decision)
        {
            return "【千牛Bot转人工提醒】"
                + "\n时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                + "\n客服：" + Safe(seller, 80)
                + "\n买家：" + Safe(buyer, 80)
                + "\n状态：" + (decision.IsOffHours ? "人工客服下班" : "人工客服工作时间")
                + "\n原因：" + Safe(decision.Reason, 200)
                + "\n问题：" + Safe(question, 500);
        }

        private static async Task<string> SendAsync(AutoReplyRuleConfig cfg, string message)
        {
            var results = new List<string>();
            if (cfg.NotifyWeChat)
            {
                results.Add("微信=" + await PostJson(cfg.WeChatWebhook,
                    new JObject { ["msgtype"] = "text", ["text"] = new JObject { ["content"] = message } }));
            }
            if (cfg.NotifyQQ)
            {
                results.Add("QQ=" + await PostJson(cfg.QQWebhook,
                    new JObject { ["message"] = message, ["content"] = message, ["text"] = message }));
            }
            if (cfg.NotifyFeishu)
            {
                results.Add("飞书=" + await PostJson(cfg.FeishuWebhook,
                    new JObject { ["msg_type"] = "text", ["content"] = new JObject { ["text"] = message } }));
            }
            if (cfg.NotifyDingTalk)
            {
                results.Add("钉钉=" + await PostJson(cfg.DingTalkWebhook,
                    new JObject { ["msgtype"] = "text", ["text"] = new JObject { ["content"] = message } }));
            }
            if (cfg.NotifyEmail)
            {
                results.Add("邮箱=" + await SendEmail(cfg, message));
            }
            return results.Count == 0 ? "未选择任何通知渠道" : string.Join("；", results);
        }

        private static async Task<string> PostJson(string url, JObject payload)
        {
            Uri uri;
            if (!Uri.TryCreate((url ?? string.Empty).Trim(), UriKind.Absolute, out uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return "未配置有效Webhook";
            }
            try
            {
                using (var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json"))
                using (var response = await Http.PostAsync(uri, content))
                {
                    var body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        return "HTTP " + (int)response.StatusCode + " " + Short(body, 120);
                    }
                    return "成功";
                }
            }
            catch (Exception ex)
            {
                return "失败：" + Short(ex.Message, 120);
            }
        }

        private static Task<string> SendEmail(AutoReplyRuleConfig cfg, string message)
        {
            return Task.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(cfg.SmtpHost) || string.IsNullOrWhiteSpace(cfg.EmailTo))
                {
                    return "SMTP服务器或收件人未配置";
                }
                try
                {
                    var recipients = (cfg.EmailTo ?? string.Empty)
                        .Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => x.Trim())
                        .Where(x => x.Length > 0)
                        .ToList();
                    if (recipients.Count == 0) return "收件人未配置";
                    using (var mail = new MailMessage())
                    using (var client = new SmtpClient(cfg.SmtpHost.Trim(), cfg.SmtpPort <= 0 ? 465 : cfg.SmtpPort))
                    {
                        mail.Subject = "千牛Bot转人工提醒";
                        mail.Body = message;
                        mail.BodyEncoding = Encoding.UTF8;
                        var sender = string.IsNullOrWhiteSpace(cfg.SmtpUser) ? recipients[0] : cfg.SmtpUser.Trim();
                        mail.From = new MailAddress(sender);
                        foreach (var recipient in recipients) mail.To.Add(recipient);
                        client.EnableSsl = cfg.SmtpEnableSsl;
                        if (!string.IsNullOrWhiteSpace(cfg.SmtpUser))
                        {
                            client.Credentials = new NetworkCredential(cfg.SmtpUser.Trim(), cfg.SmtpPassword ?? string.Empty);
                        }
                        client.Send(mail);
                    }
                    return "成功";
                }
                catch (Exception ex)
                {
                    return "失败：" + Short(ex.Message, 120);
                }
            });
        }

        private static string Normalize(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", string.Empty);
        }

        private static string Safe(string value, int max)
        {
            value = value ?? string.Empty;
            value = Regex.Replace(value, @"(?<!\d)1\d{10}(?!\d)", "[手机号]");
            value = Regex.Replace(value, @"(?i)sk-[a-z0-9_-]{12,}", "[API_KEY]");
            return Short(value, max);
        }

        private static string Short(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }
}
'''
write("src/Bot/ChromeNs/HandoffNotificationService.cs", notification)

path = "src/Bot/Bot.csproj"
text = read(path)
text = replace_once(
    text,
    '    <Compile Include="ChromeNs\\KnowledgeLearningService.cs" />',
    '    <Compile Include="ChromeNs\\KnowledgeLearningService.cs" />\n    <Compile Include="ChromeNs\\HandoffNotificationService.cs" />',
    "notification csproj include")
write(path, text)

# 5. Static regression tests.
test = r'''from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_answer_source_badges_are_explicit():
    xaml = text("src/Bot/AssistWindow/Widget/Robot/CtlConversation.xaml")
    code = text("src/Bot/AssistWindow/Widget/Robot/CtlConversation.xaml.cs")
    qn = text("src/Bot/ChromeNs/QN.cs")
    assert "bdSource" in xaml
    assert "txtSourceSeparator" in xaml
    assert "answerSource" in code
    assert "ResolveAnswerSource" in qn
    assert 'SetAnswer(result.Answer, "AI生成")' in qn


def test_history_scan_button_is_in_smart_import_only():
    manager = text("src/Bot/Knowledge/KnowledgeManagerControl.cs")
    importer = text("src/Bot/Knowledge/KnowledgeImportControl.cs")
    assert "扫描历史聊天记录" not in manager
    assert "扫描历史聊天记录" in importer
    assert "ChatHistoryScanWindow" in importer


def test_off_hours_rule_supports_ai_and_fixed_reply():
    source = text("src/Bot/Options/CtlRobotOptions.xaml.cs")
    ai = text("src/Bot/ChromeNs/MyOpenAI.cs")
    assert "WorkStartTime" in source
    assert "WorkEndTime" in source
    assert "AI告知下班时间" in source
    assert "固定预设答案" in source
    assert "EvaluateAutoReplyRule" in source
    assert "BuildOffHoursHandoffReply" in ai
    assert "AllowAutoReply" in ai


def test_handoff_notifications_cover_requested_channels():
    source = text("src/Bot/ChromeNs/HandoffNotificationService.cs")
    options = text("src/Bot/Options/CtlRobotOptions.xaml.cs")
    for value in ["微信", "QQ", "邮箱", "飞书", "钉钉"]:
        assert value in options or value in source
    assert "WeChatWebhook" in source
    assert "QQWebhook" in source
    assert "FeishuWebhook" in source
    assert "DingTalkWebhook" in source
    assert "SmtpClient" in source
    assert "NotificationCooldownMinutes" in source
'''
write("tests/test_rules_notifications_static.py", test)

print("PATCH_RESULT=PASS")
