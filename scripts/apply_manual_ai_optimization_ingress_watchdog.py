from pathlib import Path


def read(path):
    return Path(path).read_text(encoding="utf-8-sig")


def write(path, text):
    Path(path).write_text(text, encoding="utf-8")


def replace_once(text, old, new, label):
    if old not in text:
        raise RuntimeError("missing patch anchor: " + label)
    if text.count(old) != 1:
        raise RuntimeError("non-unique patch anchor: %s count=%s" % (label, text.count(old)))
    return text.replace(old, new, 1)


# 1) Compile newly added optimization service/control in both normal and WPF temp projects.
path = "src/Directory.Build.targets"
s = read(path)
anchor = "</Project>"
addition = r'''  <ItemGroup Condition="Exists('$(MSBuildProjectDirectory)\ChromeNs\AiManualReplyOptimizationService.cs')">
    <Compile Include="$(MSBuildProjectDirectory)\ChromeNs\AiManualReplyOptimizationService.cs" />
  </ItemGroup>
  <ItemGroup Condition="Exists('$(MSBuildProjectDirectory)\Knowledge\AiOptimizationHistoryControl.cs')">
    <Compile Include="$(MSBuildProjectDirectory)\Knowledge\AiOptimizationHistoryControl.cs" />
  </ItemGroup>
'''
if "AiManualReplyOptimizationService.cs" not in s:
    s = replace_once(s, anchor, addition + anchor, "Directory.Build.targets end")
write(path, s)

# 2) Human intervention no longer destroys the pending AI task; it becomes compare-only.
path = "src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs"
s = read(path)
old = '''        public void CancelBuyer(string seller, string buyer, string reason)
        {
            var key = Key(seller, buyer);
            BurstState state;
            if (!_states.TryGetValue(key, out state) || state == null) return;

            lock (state.Sync)
            {
                state.Version++;
                state.Items.Clear();
                state.StartedAt = DateTime.MinValue;
                try { state.DelayCancellation.Cancel(); } catch { }
                state.DelayCancellation.Dispose();
                state.DelayCancellation = new CancellationTokenSource();
                state.WorkerRunning = false;
                DisposeActivity(state);
            }

            BurstState ignored;
            _states.TryRemove(key, out ignored);
            Log.Info("买家自动回复任务已因人工介入失效: seller=" + seller
                + ", buyer=" + buyer + ", reason=" + (reason ?? string.Empty));
        }
'''
new = '''        public void CancelBuyer(string seller, string buyer, string reason)
        {
            // 人工客服回复时，不再销毁已经排队/正在生成的AI任务。AI继续完成，但QN发送层会
            // 识别人工回复证据，将最终AI答案仅展示并进入对比学习，绝不会再自动发给买家。
            // 买家新增消息等真正 supersede 场景仍使用原取消逻辑。
            if ((reason ?? string.Empty).IndexOf("检测到客服回复", StringComparison.Ordinal) >= 0)
            {
                Log.Info("检测到人工客服介入：保留AI后台生成用于答案对比，已禁止该答案自动发送。seller="
                    + seller + ", buyer=" + buyer);
                return;
            }

            var key = Key(seller, buyer);
            BurstState state;
            if (!_states.TryGetValue(key, out state) || state == null) return;

            lock (state.Sync)
            {
                state.Version++;
                state.Items.Clear();
                state.StartedAt = DateTime.MinValue;
                try { state.DelayCancellation.Cancel(); } catch { }
                state.DelayCancellation.Dispose();
                state.DelayCancellation = new CancellationTokenSource();
                state.WorkerRunning = false;
                DisposeActivity(state);
            }

            BurstState ignored;
            _states.TryRemove(key, out ignored);
            Log.Info("买家自动回复任务已取消: seller=" + seller
                + ", buyer=" + buyer + ", reason=" + (reason ?? string.Empty));
        }
'''
s = replace_once(s, old, new, "CancelBuyer manual preserve")
write(path, s)

# 3) Keep right-side progress card alive after human intervention and add compare-only final answer API.
path = "src/Bot/ChromeNs/ResponseProgressTracker.cs"
s = read(path)
old = '''        public static void MarkManualIntervention(string seller, string buyer, string sellerReply)
        {
            SendDeliveryWatchdog.CancelConversation(seller, buyer, "检测到客服人工回复");
            MessageProcessingTraceService.RecordManualIntervention(seller, buyer, sellerReply);
            Entry entry;
            if (!Entries.TryRemove(Key(seller, buyer), out entry) || entry == null) return;
            lock (entry.Sync)
            {
                if (entry.AnswerReadyAt == DateTime.MinValue && entry.Control != null)
                    entry.Control.SetStatus("检测到客服已人工回复，停止等待旧AI答案", true);
                else if (entry.Control != null)
                    entry.Control.SetStatus("检测到客服已人工回复，旧答案不再自动发送", true);
            }
            ReplyQualityMetricsService.RecordCancellation(false);
            Log.Info("本店回复进度因人工客服介入结束: seller=" + seller + ", buyer=" + buyer
                + ", reply=" + (sellerReply ?? string.Empty));
        }
'''
new = '''        public static void MarkManualIntervention(string seller, string buyer, string sellerReply)
        {
            SendDeliveryWatchdog.CancelConversation(seller, buyer, "检测到客服人工回复");
            MessageProcessingTraceService.RecordManualIntervention(seller, buyer, sellerReply);
            Entry entry;
            if (Entries.TryGetValue(Key(seller, buyer), out entry) && entry != null)
            {
                lock (entry.Sync)
                {
                    if (entry.Control != null)
                    {
                        entry.Control.SetStatus(
                            entry.AnswerReadyAt == DateTime.MinValue
                                ? "检测到客服已人工回复；继续获取AI最终答案用于对比学习，不会自动发送"
                                : "检测到客服已人工回复；AI答案仅保留用于对比学习，不会自动发送",
                            true);
                    }
                }
            }
            ReplyQualityMetricsService.RecordCancellation(false);
            Log.Info("检测到人工客服回复，AI流程切换为仅对比学习: seller=" + seller + ", buyer=" + buyer);
        }

        public static CtlConversation SetAnswerReadyAfterManual(
            string seller,
            string buyer,
            string question,
            string answer,
            string source,
            DateTime detectedAt,
            DateTime answerReadyAt)
        {
            if (answerReadyAt == DateTime.MinValue) answerReadyAt = DateTime.Now;
            var detected = detectedAt == DateTime.MinValue ? answerReadyAt : detectedAt;
            var control = SetExactQuestion(seller, buyer, question, detected);
            var key = Key(seller, buyer);
            Entry entry;
            if (Entries.TryGetValue(key, out entry) && entry != null)
            {
                lock (entry.Sync)
                {
                    entry.AnswerReadyAt = answerReadyAt;
                    entry.Answer = answer ?? string.Empty;
                }
            }
            if (control != null)
            {
                control.SetAnswer(answer, (source ?? "AI生成") + "（人工已回复，仅供对比）", answerReadyAt);
                control.SetStatus("客服已人工回复；上方为AI最终答案，仅用于准确性/知识库优化对比，未发送", true);
            }
            MessageProcessingTraceService.RecordAnswerReady(
                seller, buyer, question, answer,
                (source ?? "AI生成") + "（人工已回复，仅供对比）",
                Math.Max(0, (long)(answerReadyAt - detected).TotalMilliseconds));
            Log.Info("人工介入后的AI最终答案已保留到Bot界面: seller=" + seller
                + ", buyer=" + buyer + ", source=" + (source ?? string.Empty));
            return control;
        }
'''
s = replace_once(s, old, new, "manual intervention progress retention")
write(path, s)

# 4) Record true manual replies before switching current AI work to compare-only mode.
path = "src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs"
s = read(path)
s = replace_once(s, "private const int HeartbeatIntervalSeconds = 60;", "private const int HeartbeatIntervalSeconds = 300;", "heartbeat noise reduction")
old = '''                    qn.CancelActiveBuyerGeneration(seller, buyer, "检测到客服回复：" + Short(text, 120));
                    ResponseProgressTracker.MarkManualIntervention(seller, buyer, text);
'''
new = '''                    AiManualReplyOptimizationService.ObserveManualReply(seller, buyer, text);
                    qn.CancelActiveBuyerGeneration(seller, buyer, "检测到客服回复：" + Short(text, 120));
                    ResponseProgressTracker.MarkManualIntervention(seller, buyer, text);
'''
s = replace_once(s, old, new, "observe manual reply")

# Add low-frequency active-conversation reconciliation state.
old = '''        private static readonly ConcurrentDictionary<QN, int> ConsecutiveProbeFailures =
            new ConcurrentDictionary<QN, int>();
'''
new = '''        private static readonly ConcurrentDictionary<QN, int> ConsecutiveProbeFailures =
            new ConcurrentDictionary<QN, int>();
        private static readonly ConcurrentDictionary<QN, DateTime> NextActiveReconciliationAt =
            new ConcurrentDictionary<QN, DateTime>();
'''
s = replace_once(s, old, new, "active reconciliation dictionary")

old = '''            if (AreSameBuyer(seller, currentNick, firstNick))
            {
                RecordProbeSuccess(qn, seller, firstNick, false);
                return;
            }
'''
new = '''            if (AreSameBuyer(seller, currentNick, firstNick))
            {
                ScheduleActiveConversationReconciliation(qn, seller, firstNick, false);
                RecordProbeSuccess(qn, seller, firstNick, false);
                return;
            }
'''
s = replace_once(s, old, new, "same buyer reconciliation")
old = '''            Log.Info("当前买家由主动探测修正: seller=" + seller
                + ", previous=" + currentNick + ", current=" + resolved);
            RecordProbeSuccess(qn, seller, resolved, true);
        }

        private static bool HasVerifiedReceptionDesk(string seller)
'''
new = '''            Log.Info("当前买家由主动探测修正: seller=" + seller
                + ", previous=" + currentNick + ", current=" + resolved);
            ScheduleActiveConversationReconciliation(qn, seller, resolved, true);
            RecordProbeSuccess(qn, seller, resolved, true);
        }

        private static void ScheduleActiveConversationReconciliation(
            QN qn,
            string seller,
            string buyer,
            bool corrected)
        {
            if (qn == null || string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(buyer)) return;
            var now = DateTime.UtcNow;
            DateTime next;
            if (!corrected && NextActiveReconciliationAt.TryGetValue(qn, out next) && next > now) return;
            NextActiveReconciliationAt[qn] = now.AddSeconds(corrected ? 8 : 30);

            Task.Run(async () =>
            {
                try
                {
                    // 连接“绿灯”只证明socket/CDP对象存活。主动会话核对同时检查最近远端聊天历史，
                    // 即使 receiveNewMsg / onShopRobotReceriveNewMsgs 整条业务推送暂时静默，也能补回当前买家的新消息。
                    var recoveredMessages = await qn.ReconcileActiveConversationHistoryAsync(
                        seller, buyer, corrected ? 240 : 90).ConfigureAwait(false);

                    // 订单可能只刷新在右侧订单面板，不进入聊天历史。当前买家核对时顺便读取面板；
                    // runtimePassive 来源对已去重订单不打印普通成功日志，避免每30秒污染日志。
                    await qn.TryRecoverVisibleOrderPanelForBackgroundProbeAsync(
                        seller,
                        buyer,
                        corrected ? "runtimeCorrected" : "runtimePassive",
                        DateTime.Now.AddMinutes(-3),
                        false).ConfigureAwait(false);

                    if (recoveredMessages > 0)
                    {
                        Log.Error("业务消息推送疑似漏事件，已从当前会话远端历史主动补回: seller="
                            + seller + ", buyer=" + buyer + ", count=" + recoveredMessages);
                    }
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("当前会话业务入站核对失败: seller=" + seller
                        + ", buyer=" + buyer + ", error=" + ex.Message, 10);
                }
            });
        }

        private static bool HasVerifiedReceptionDesk(string seller)
'''
s = replace_once(s, old, new, "active reconciliation method")
write(path, s)

# 5) Passive reconciliation of active buyer remote history, safe through existing dedup.
path = "src/Bot/ChromeNs/QN.MessageRecovery.cs"
s = read(path)
anchor = '''        private async Task<bool> RecoverMissedBuyerMessagesAsync(
'''
method = '''        internal async Task<int> ReconcileActiveConversationHistoryAsync(
            string seller,
            string buyer,
            int lookbackSeconds)
        {
            seller = (seller ?? string.Empty).Trim();
            buyer = BuyerIdentityAliasService.ResolveInternalNick(seller, buyer);
            if (seller.Length == 0 || buyer.Length == 0 || cdp == null || !Params.Robot.CanUseRobotReal) return 0;
            var lookback = Math.Max(30, Math.Min(300, lookbackSeconds));
            if (HasBuyerMessageAfter(seller, buyer, DateTime.Now.AddSeconds(-lookback))) return 0;

            DbEntity.Conversation current;
            try
            {
                var response = await GetCurrentConversationID().ConfigureAwait(false);
                current = response == null ? null : response.Result;
            }
            catch
            {
                return 0;
            }
            if (current == null || string.IsNullOrWhiteSpace(current.Nick)
                || !BuyerIdentityAliasService.AreEquivalent(seller, current.Nick, buyer)
                || string.IsNullOrWhiteSpace(current.Ccode)) return 0;

            JObject history;
            try
            {
                history = await cdp.Invoke<JObject>("im.singlemsg.GetRemoteHisMsg", new
                {
                    cid = new { ccode = current.Ccode, type = 1 },
                    count = 30,
                    gohistory = 1,
                    msgid = "-1",
                    msgtime = "-1"
                }).ConfigureAwait(false);
            }
            catch
            {
                return 0;
            }
            if (history == null) return 0;

            var threshold = DateTime.Now.AddSeconds(-lookback).Ticks;
            var messages = history["result"]?["msgs"]?.ToObject<List<QNChatMessage>>() ?? new List<QNChatMessage>();
            var candidates = messages
                .Where(m => m != null)
                .Where(m =>
                    (IsBuyerMessage(m) && m.fromid != null
                        && BuyerIdentityAliasService.AreEquivalent(seller, m.fromid.nick, buyer))
                    || IsPotentialRecoveredOrderCard(m))
                .Where(m =>
                {
                    var sort = IncomingMessageSafety.GetSortValue(m);
                    return sort <= 0 || sort >= threshold;
                })
                .OrderBy(IncomingMessageSafety.GetSortValue)
                .ToList();
            if (candidates.Count == 0) return 0;

            var processed = 0;
            foreach (var message in candidates)
            {
                // Do not bypass normal message dedup in this passive path. If the push path already handled
                // the message it remains a no-op; if the push event never reached Bot, it enters normally.
                await ProcessRecoveredMessageWithKnownBuyerAsync(message, seller, buyer, false).ConfigureAwait(false);
                processed++;
                await Task.Delay(20).ConfigureAwait(false);
            }
            return processed;
        }

'''
if "ReconcileActiveConversationHistoryAsync" not in s:
    s = replace_once(s, anchor, method + anchor, "active history reconcile insertion")
write(path, s)

# 6) If a human replied, finish AI generation, show it, compare it, but never send it.
path = "src/Bot/ChromeNs/QN.cs"
s = read(path)
old = '''            if (!lease.IsCurrent)
            {
                Log.Info("买家在AI生成期间发送了新消息，旧文本草稿已作废。buyer=" + burst.BuyerNick);
                return;
            }
'''
new = '''            if (!lease.IsCurrent)
            {
                if (CompleteAiAfterManualIntervention(burst, answer, detectedAt, aiStartedAt)) return;
                Log.Info("买家在AI生成期间发送了新消息，旧文本草稿已作废。buyer=" + burst.BuyerNick);
                return;
            }
'''
s = replace_once(s, old, new, "text invalid lease manual compare")
old = '''            if (!await lease.ConfirmStableAsync(220))
            {
                Log.Info("发送前发现买家补充了新消息，旧文本答案未展示也未发送。buyer=" + burst.BuyerNick);
                return;
            }

            var answerReadyAt = DateTime.Now;
'''
new = '''            string manualReply;
            DateTime manualReplyAt;
            if (AiManualReplyOptimizationService.TryGetRecentManualReply(
                burst.SellerNick, burst.BuyerNick, detectedAt, out manualReply, out manualReplyAt))
            {
                CompleteAiAfterManualIntervention(burst, answer, detectedAt, aiStartedAt, manualReply, manualReplyAt);
                return;
            }

            if (!await lease.ConfirmStableAsync(220))
            {
                if (CompleteAiAfterManualIntervention(burst, answer, detectedAt, aiStartedAt)) return;
                Log.Info("发送前发现买家补充了新消息，旧文本答案未展示也未发送。buyer=" + burst.BuyerNick);
                return;
            }

            var answerReadyAt = DateTime.Now;
'''
s = replace_once(s, old, new, "text pre-send manual compare")
write(path, s)

# 7) QN partial helper for compare-only UI + learning.
path = "src/Bot/ChromeNs/QN.RuntimeSafety.cs"
s = read(path)
old = '''        internal bool HasBuyerMessageAfter(string seller, string buyer, DateTime threshold)
        {
            DateTime observedAt;
            return _latestBuyerMessageObserved.TryGetValue(RecoveryKey(seller, buyer), out observedAt)
                && observedAt > threshold.AddMilliseconds(5);
        }
    }
}
'''
new = '''        internal bool HasBuyerMessageAfter(string seller, string buyer, DateTime threshold)
        {
            DateTime observedAt;
            return _latestBuyerMessageObserved.TryGetValue(RecoveryKey(seller, buyer), out observedAt)
                && observedAt > threshold.AddMilliseconds(5);
        }

        private bool CompleteAiAfterManualIntervention(
            BuyerMessageBurst burst,
            string answer,
            DateTime detectedAt,
            DateTime aiStartedAt)
        {
            string manualReply;
            DateTime manualReplyAt;
            if (burst == null || !AiManualReplyOptimizationService.TryGetRecentManualReply(
                burst.SellerNick, burst.BuyerNick, detectedAt, out manualReply, out manualReplyAt)) return false;
            return CompleteAiAfterManualIntervention(
                burst, answer, detectedAt, aiStartedAt, manualReply, manualReplyAt);
        }

        private bool CompleteAiAfterManualIntervention(
            BuyerMessageBurst burst,
            string answer,
            DateTime detectedAt,
            DateTime aiStartedAt,
            string manualReply,
            DateTime manualReplyAt)
        {
            if (burst == null || string.IsNullOrWhiteSpace(answer) || answer.StartsWith("错误：", StringComparison.Ordinal)) return false;
            var answerReadyAt = DateTime.Now;
            var source = KnowledgeLearningService.ResolveAnswerSource(
                burst.SellerNick, burst.BuyerNick, burst.CombinedQuestion, answer);
            ResponseProgressTracker.SetAnswerReadyAfterManual(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                answer,
                source,
                detectedAt,
                answerReadyAt);
            AiManualReplyOptimizationService.QueueCompare(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                answer,
                manualReply,
                detectedAt,
                manualReplyAt);
            ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick);
            Log.Info("人工客服已回复，本轮AI最终答案仅展示并进入优化对比，未发送: seller="
                + burst.SellerNick + ", buyer=" + burst.BuyerNick
                + ", aiMs=" + Math.Max(0, (long)(answerReadyAt - aiStartedAt).TotalMilliseconds));
            return true;
        }
    }
}
'''
s = replace_once(s, old, new, "QN manual compare helper")
write(path, s)

# 8) Suppress duplicate runtime-passive order logs while preserving real recovered-order logs.
path = "src/Bot/ChromeNs/FirstInquiryDeliveryBridge.cs"
s = read(path)
old = '''                if (publish != null && publish.Detected)
                {
                    sawFreshSupportedOrder = true;
                    Log.Info((publish.Accepted
                        ? "后台订单面板延迟兜底识别并发布"
                        : "后台订单面板延迟兜底订单已由其他通道处理/去重")
                        + ": seller=" + runtimeSeller + ", buyer=" + verifiedBuyer
                        + ", orderId=" + candidate.OrderId + ", event=" + eventType
                        + ", trigger=" + (source ?? string.Empty));
                }
'''
new = '''                if (publish != null && publish.Detected)
                {
                    sawFreshSupportedOrder = true;
                    var passiveDuplicate = !publish.Accepted
                        && (source ?? string.Empty).IndexOf("runtimePassive", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!passiveDuplicate)
                    {
                        Log.Info((publish.Accepted
                            ? "后台订单面板延迟兜底识别并发布"
                            : "后台订单面板延迟兜底订单已由其他通道处理/去重")
                            + ": seller=" + runtimeSeller + ", buyer=" + verifiedBuyer
                            + ", orderId=" + candidate.OrderId + ", event=" + eventType
                            + ", trigger=" + (source ?? string.Empty));
                    }
                }
'''
s = replace_once(s, old, new, "passive order log suppression")
write(path, s)

# 9) Remove settings migration chatter; migration remains functional.
path = "src/Bot/Options/FeatureSettingsOptionsControl.cs"
s = read(path)
for line in [
    '                Log.Info("设置界面已将“人工客服工作时间与下班回复”迁移为自动回复规则中的“下班自动回复”。");\n',
    '                Log.Info("设置界面已直接构造“转人工策略”页面并迁移转人工规则（兼容布局）。");\n',
    '            Log.Info("设置界面已在构造阶段将“启用转人工规则”及关键词/话术移动到“转人工策略”。");\n',
]:
    s = s.replace(line, "")
write(path, s)

# 10) Successful UIA refresh is routine and was flooding logs many times per second. Keep failures only.
path = "src/Bot/ChromeNs/QNRpa.ReliableSend.cs"
s = read(path)
old = '''                    if (!inputFound)
                    {
                        SetSendFailure("UIA扫描", "当前客服千牛窗口内未找到聊天输入框；hwnd="
                            + expectedHwnd + ", descendants=" + descendants.Length);
                    }
                    else
                    {
                        Log.Info("UIA控件刷新成功: seller=" + SellerNick
                            + ", hwnd=" + expectedHwnd
                            + ", inputAutomationId=" + SafeAutomationId(inputElement)
                            + ", inputClass=" + SafeClassName(inputElement)
                            + ", sendAutomationId=" + SafeAutomationId(sendElement)
                            + ", sendName=" + SafeName(sendElement)
                            + ", sendRect=" + FormatRect(_sendMessageButtonRect));
                    }
                    return inputFound;
'''
new = '''                    if (!inputFound)
                    {
                        SetSendFailure("UIA扫描", "当前客服千牛窗口内未找到聊天输入框；hwnd="
                            + expectedHwnd + ", descendants=" + descendants.Length);
                    }
                    return inputFound;
'''
s = replace_once(s, old, new, "UIA success spam removal")
write(path, s)

# Static regression tests.
test = Path("tests/test_manual_ai_optimization_ingress_watchdog_static.py")
test.write_text(r'''from pathlib import Path

ROOT = Path("src/Bot")
QN = (ROOT / "ChromeNs/QN.cs").read_text(encoding="utf-8-sig")
RUNTIME = (ROOT / "ChromeNs/QnRuntimeSafetyMonitor.cs").read_text(encoding="utf-8-sig")
PROGRESS = (ROOT / "ChromeNs/ResponseProgressTracker.cs").read_text(encoding="utf-8-sig")
BURST = (ROOT / "ChromeNs/BuyerMessageBurstCoordinator.cs").read_text(encoding="utf-8-sig")
RECOVERY = (ROOT / "ChromeNs/QN.MessageRecovery.cs").read_text(encoding="utf-8-sig")
OPT = (ROOT / "ChromeNs/AiManualReplyOptimizationService.cs").read_text(encoding="utf-8-sig")
UI = (ROOT / "Knowledge/KnowledgeCenterWindow.cs").read_text(encoding="utf-8-sig")
HISTORY_UI = (ROOT / "Knowledge/AiOptimizationHistoryControl.cs").read_text(encoding="utf-8-sig")
SETTINGS = (ROOT / "Options/FeatureSettingsOptionsControl.cs").read_text(encoding="utf-8-sig")
RPA = (ROOT / "ChromeNs/QNRpa.ReliableSend.cs").read_text(encoding="utf-8-sig")


def test_manual_reply_keeps_ai_generation_for_compare_only():
    assert "保留AI后台生成用于答案对比" in BURST
    assert "AiManualReplyOptimizationService.ObserveManualReply" in RUNTIME
    assert "SetAnswerReadyAfterManual" in PROGRESS
    assert "CompleteAiAfterManualIntervention" in QN
    assert "QueueCompare" in OPT


def test_ai_optimization_history_is_exposed():
    assert 'Header = "AI优化记录"' in UI
    assert "AiManualReplyOptimizationService.GetRecords" in HISTORY_UI
    assert "ConversationSessionLearningService.GetReports" in HISTORY_UI
    assert "accuracy_score" in OPT
    assert "human_reply_reason" in OPT
    assert "knowledge_strategy" in OPT


def test_active_conversation_has_business_event_reconciliation():
    assert "ScheduleActiveConversationReconciliation" in RUNTIME
    assert "ReconcileActiveConversationHistoryAsync" in RECOVERY
    assert '"runtimePassive"' in RUNTIME
    assert "TryRecoverVisibleOrderPanelForBackgroundProbeAsync" in RUNTIME


def test_noisy_success_and_migration_logs_removed():
    assert "UIA控件刷新成功:" not in RPA
    assert "设置界面已将“人工客服工作时间与下班回复”迁移" not in SETTINGS
    assert "设置界面已在构造阶段将“启用转人工规则”" not in SETTINGS
    assert "HeartbeatIntervalSeconds = 300" in RUNTIME
''', encoding="utf-8")
print("manual AI optimization and ingress watchdog patch applied")
