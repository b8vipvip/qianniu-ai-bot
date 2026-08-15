from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_auto_reply_rules_expose_custom_first_inquiry_reply():
    source = read("src/Bot/Options/FeatureSettingsOptionsControl.cs")
    assert "OrganizeAutoReplyRulesPage" in source
    assert '"自动回复规则"' in source
    assert 'Text = "自动回复规则"' not in source
    assert '"启用首条咨询固定回复"' in source
    assert '"固定答案"' in source
    assert '"① 首条咨询固定回复"' not in source
    assert '"② 下单后及高级自动回复"' not in source
    assert "DetachLegacyScrollHost" in source
    assert "new ScrollViewer" in source
    assert "VerticalScrollBarVisibility = ScrollBarVisibility.Auto" in source
    assert "FirstInquiryFixedReplyService.Load(Seller)" in source
    assert "FirstInquiryFixedReplyService.Save(" in source
    assert "_firstInquiryFixedReplyAnswer.Text" in source


def test_first_inquiry_reply_is_shop_scoped_and_customizable():
    source = read("src/Bot/ChromeNs/QN.RuntimeSafety.cs")
    assert 'SettingsScope = "feature"' in source
    assert 'EnabledKey = "FirstInquiryFixedReplyEnabled"' in source
    assert 'AnswerKey = "FirstInquiryFixedReplyAnswer"' in source
    assert "ShopSettingsScope.Current" in source
    assert "ShopContextLocator.ResolveRuntimeBySellerNick" in source
    assert "PersistentParams.TrySaveParam2Key" in source
    assert "PersistentParams.GetParam2Key" in source


def test_first_inquiry_defaults_to_enabled_and_expected_answer():
    source = read("src/Bot/ChromeNs/QN.RuntimeSafety.cs")
    compact = "".join(source.split())
    assert 'DefaultAnswer = "在的，亲！"' in source
    assert 'GetParam2Key(EnabledKey,SettingsScope,"true")' in compact
    assert 'GetParam2Key(AnswerKey,SettingsScope,DefaultAnswer)' in compact


def test_first_inquiry_is_once_per_30_minute_consultation_session():
    source = read("src/Bot/ChromeNs/QN.RuntimeSafety.cs")
    assert "SessionResetMinutes = 30" in source
    assert "ConversationContextStore.GetRecentTurns(" in source
    assert "currentQuestion" in source
    assert "latestPrior.Timestamp == DateTime.MinValue" in source
    assert "latestPrior.Timestamp >= now.AddMinutes(-SessionResetMinutes)" in source
    assert "TriggeredAt" in source
    assert "PendingReplies" in source
    assert "SameBurstHistoryGraceSeconds = 8" in source
    assert "IsIgnorableFirstInquiryHistoryTurn" in source
    assert 'text.StartsWith("当前用户来自"' in source
    assert 'text.StartsWith("系统提示"' in source
    assert 'string.Equals(turn.Role, "user", StringComparison.Ordinal)' in source


def test_first_inquiry_session_is_committed_after_real_delivery():
    source = read("src/Bot/ChromeNs/QN.RuntimeSafety.cs")
    assert "public static void MarkDelivered" in source
    assert "public static void ReleaseReservation" in source
    assert "InFlight" in source
    resolve = source[source.index("public static bool TryResolve"):source.index("public static void MarkDelivered")]
    assert "TriggeredAt[key] = now" not in resolve
    delivered = source[source.index("public static void MarkDelivered"):source.index("public static void ReleaseReservation")]
    assert "TriggeredAt[key] = DateTime.Now" in delivered


def test_any_fresh_buyer_or_system_message_can_prepare_fixed_reply():
    service = read("src/Bot/ChromeNs/QN.RuntimeSafety.cs")
    router = read("src/Bot/ChromeNs/VisionMessageDecision.cs")
    assert "public static bool TryPrepare(" in service
    assert 'decision.MessageLabel, "历史消息"' in service
    assert "FirstInquiryFixedReplyService.TryPrepare(" in router
    prepare = router.index("FirstInquiryFixedReplyService.TryPrepare(")
    ordinary_text = router.index("if (safetyDecision.ShouldCallAi)", prepare)
    image_route = router.index('if (!string.Equals(safetyDecision.MessageLabel, "[图片]"', ordinary_text)
    assert prepare < ordinary_text < image_route
    assert "IncomingMessageSafety.GetDisplayText(message, text)" in router
    assert "Kind = VisionDecisionKind.Text" in router[prepare:ordinary_text]
    assert "首条咨询固定回复已预留" in service


def test_platform_system_tips_are_eligible_before_normal_skip_routing():
    safety = read("src/Bot/ChromeNs/IncomingMessageSafety.cs")
    router = read("src/Bot/ChromeNs/VisionMessageDecision.cs")
    assert 'Skip("[淘宝系统提示]"' in safety
    prepare = router.index("FirstInquiryFixedReplyService.TryPrepare(")
    normal_skip = router.index("return Skip(safetyDecision.MessageLabel, safetyDecision.Note);", prepare)
    assert prepare < normal_skip


def test_fixed_first_reply_skips_ai_and_uses_normal_send_pipeline():
    source = read("src/Bot/ChromeNs/QN.cs")
    fixed = source.index("FirstInquiryFixedReplyService.TryResolve(")
    ai = source.index("MyOpenAI.GetAnswer(", fixed)
    send = source.index("SendTextWithRetryAsync(burst.BuyerNick, answer, 1)", ai)
    assert fixed < ai < send
    assert "if (usedFirstInquiryFixedReply)" in source[fixed:ai]
    assert '"首条咨询固定回复"' in source
    assert "BotOutboundMessageFormatter.EnsureAiMarker(answer)" in source
    assert "if (!usedFirstInquiryFixedReply)" in source
    assert "ReplyDeduplicationService.RememberDelivered" in source[send:]


def test_fixed_reply_does_not_enter_ai_learning_path():
    source = read("src/Bot/ChromeNs/QN.cs")
    assert 'if (string.Equals(answerSource, "AI生成", StringComparison.Ordinal))' in source
    assert 'KnowledgeLearningService.RegisterAnswerSource(' in source
    assert '"首条咨询固定回复"' in source


def test_qnrpa_no_longer_requires_global_desk_and_send_path_is_nonblocking_main_area_click():
    source = read("src/Bot/ChromeNs/QNRpa.cs")
    reliable = read("src/Bot/ChromeNs/QNRpa.ReliableSend.cs")
    ctor = source[source.index("public QNRpa(QN qn)"):source.index("private bool IsSendButtonName")]
    assert "Application.Attach(Desk.Inst.ProcessId)" not in ctor
    assert "EnsureSellerDeskBinding(false)" in ctor

    assert ".GetAwaiter().GetResult()" not in source
    assert "ProbeInputboxEmptyAsync" in source
    assert "Task.WhenAny" in source
    assert "ConfigureAwait(false)" in source
    assert "已放弃等待且不会阻塞UI线程" in source

    cdp = source[source.index("private async Task<bool> TrySetPlainTextByCdpAsync"):source.index("private async Task<bool> OpenAndSendText")]
    assert "RunCdpActionAsync" in cdp
    assert "CDP写入由UIA严格确认" in cdp
    assert "进入UIA定位发送主按钮动作" in cdp
    assert "检测到本次Bot草稿仍在输入框，重试直接复用且不再次追加" in cdp
    nonempty = cdp.index("if (!before.IsEmpty)")
    insert = cdp.index("准备通过CDP写入输入框")
    assert nonempty < insert

    assert "TryPressEnterTextSendAsync" not in source
    assert "TryFocusEditorForEnterFast" not in source
    assert "PressEnter()" not in source
    assert "keybd_event(0x0D" not in source
    assert "Enter主发送开始" not in source

    button = source[source.index("private async Task<bool> TrySendTextViaUiaAsync"):source.index("public async Task SendImageAsync")]
    assert "TryInvokeCachedSendButtonNow" in source
    assert "AsButton().Invoke()" in source
    assert "TryClickCachedSendButtonNow" in button
    assert "_sendMessageButtonRect" in source
    assert "arrowGuard" in source
    assert "发送主按钮左侧区域坐标点击" in source
    assert "sellerDesk.BringTop()" in source
    assert "发送主按钮坐标" in button
    assert "_lastSendButtonCoordinateClickRejected" in source
    assert "发送主按钮坐标输入被系统拒绝，准备仅回退一次UIA Invoke" in button
    assert "if (!_lastSendButtonCoordinateClickRejected)" in button
    assert "HasExpectedDraftFastAsync(text, 900)" in button
    assert "坐标点击异常后目标草稿已不存在或无法确认，禁止UIA二次动作" in button
    assert button.index("TryClickCachedSendButtonNow") < button.index("TryInvokeCachedSendButtonNow")
    click_method = source[source.index("private bool TryClickCachedSendButtonNow"):source.index("private bool TryInvokeCachedSendButtonNow")]
    assert "_lastSendButtonCoordinateClickRejected = false" in click_method
    assert "_lastSendButtonCoordinateClickRejected = true" in click_method
    assert "sendRect=" in reliable

    open_send = source[source.index("private async Task<bool> OpenAndSendText"):]
    assert "HasExpectedDraftFastAsync" in open_send
    assert "TrySendTextViaUiaAsync" in open_send
    assert "SetPlainText(text)" not in open_send
    assert "method=UIA定位+发送主按钮坐标" in open_send


def test_order_first_event_has_first_inquiry_delivery_bridge():
    source = read("src/Bot/ChromeNs/FirstInquiryDeliveryBridge.cs")
    assert "EvRecieveNewMessage += Qn_EvRecieveNewMessage" in source
    assert "FirstInquiryFixedReplyService.HasPending" in source
    assert "FirstInquiryFixedReplyService.MarkDelivered" in source
    assert "OrderEventType.Created" in source
    assert "OrderEventType.Paid" in source
    assert "SendOrderFirstGreetingAsync" in source
    assert "qn.SendTextWithRetryAsync" in source