using Bot.AssistWindow.Widget.Robot;
using Bot.Automation.ChatDeskNs;
using Bot.ShopScope;
using BotLib;
using System;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Bot.ChromeNs
{
    internal static class ResponseProgressTracker
    {
        private sealed class Entry
        {
            public readonly object Sync = new object();
            public CtlConversation Control;
            public string Question = string.Empty;
            public string Answer = string.Empty;
            public DateTime DetectedAt = DateTime.MinValue;
            public DateTime AnswerStartedAt = DateTime.MinValue;
            public DateTime AnswerReadyAt = DateTime.MinValue;
        }

        private sealed class DeliveryUiEntry
        {
            public CtlConversation Control;
            public string Source = string.Empty;
            public DateTime ExpiresAt;
        }

        private static readonly ConcurrentDictionary<string, Entry> Entries =
            new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, DeliveryUiEntry> DeliveryUi =
            new ConcurrentDictionary<string, DeliveryUiEntry>(StringComparer.Ordinal);

        private static string Key(string seller, string buyer)
        {
            return ScopeKey(seller) + "#" + (seller ?? string.Empty).Trim()
                + "#" + (buyer ?? string.Empty).Trim();
        }

        private static string ScopeKey(string seller)
        {
            var current = ShopSettingsScope.Current;
            if (current != null) return current.ShopKey;
            try { return ShopContextLocator.ResolveRuntimeBySellerNick(seller).ShopKey; }
            catch { return "legacy-" + (seller ?? string.Empty).Trim().ToLowerInvariant(); }
        }

        private static string DeliveryKey(string seller, string buyer, string answer)
        {
            return Key(seller, buyer) + "#"
                + Regex.Replace((answer ?? string.Empty).Trim(), @"\s+", string.Empty);
        }

        public static bool IsMandatoryOrderAnswer(string seller, string buyer, string answer)
        {
            DeliveryUiEntry ui;
            return DeliveryUi.TryGetValue(DeliveryKey(seller, buyer, answer), out ui)
                && ui != null
                && (ui.Source ?? string.Empty).IndexOf("下单自动回复", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static CtlConversation ObserveQuestion(
            string seller,
            string buyer,
            string question,
            DateTime detectedAt)
        {
            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();
            question = (question ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(buyer)) return null;

            ObserveNewBuyerTurn(seller, buyer);
            SendDeliveryWatchdog.OnBuyerMessageObserved(seller, buyer, detectedAt);
            CleanupDeliveryUi();
            if (ShouldDeferUnsupportedMediaCard(question)) return null;

            var key = Key(seller, buyer);
            while (true)
            {
                var entry = Entries.GetOrAdd(key, _ => new Entry());
                lock (entry.Sync)
                {
                    Entry current;
                    if (!Entries.TryGetValue(key, out current) || !ReferenceEquals(current, entry)) continue;

                    var firstObservation = entry.DetectedAt == DateTime.MinValue;
                    var observedAt = detectedAt == DateTime.MinValue ? DateTime.Now : detectedAt;
                    var newerTurnDuringGeneration = entry.AnswerStartedAt != DateTime.MinValue
                        && entry.DetectedAt != DateTime.MinValue
                        && observedAt > entry.DetectedAt.AddMilliseconds(5)
                        && !string.Equals(entry.Question, question, StringComparison.Ordinal);
                    if (entry.AnswerReadyAt != DateTime.MinValue || newerTurnDuringGeneration)
                    {
                        if (entry.Control != null)
                        {
                            entry.Control.SetStatus(
                                entry.AnswerReadyAt == DateTime.MinValue
                                    ? "买家补充了新消息，上一条Bot任务继续独立处理，发送前会再次检查相关性"
                                    : (IsMandatoryOrderAnswer(seller, buyer, entry.Answer)
                                        ? "买家已补充新消息，下单固定预设仍保持优先发送"
                                        : "买家已补充新消息，上一条答案保留并在发送前检查是否仍相关"),
                                false);
                        }
                        var replacement = new Entry();
                        if (!Entries.TryUpdate(key, replacement, entry)) continue;
                        continue;
                    }

                    if (entry.DetectedAt == DateTime.MinValue || detectedAt < entry.DetectedAt)
                        entry.DetectedAt = detectedAt == DateTime.MinValue ? DateTime.Now : detectedAt;
                    entry.Question = MergeQuestion(entry.Question, question);
                    var sellerDesk = Desk.FindExistingBySellerNick(seller);
                    if (entry.Control == null && sellerDesk != null)
                    {
                        try
                        {
                            entry.Control = sellerDesk.AddConversation(
                                seller, buyer, entry.Question,
                                "正在识别并等待买家本轮消息结束...", false, "处理中");
                        }
                        catch (Exception ex)
                        {
                            Log.ErrorWithMaxCount("创建本店回复进度卡片失败，已忽略UI异常继续处理消息：" + ex.Message, 10);
                            entry.Control = null;
                        }
                    }
                    if (entry.Control != null)
                    {
                        entry.Control.SetQuestion(entry.Question, entry.DetectedAt);
                        entry.Control.SetProcessing("已识别，等待合并本轮消息...");
                    }
                    if (firstObservation)
                        MessageProcessingTraceService.RecordQuestion(seller, buyer, entry.Question);
                    return entry.Control;
                }
            }
        }

        public static CtlConversation BeginAnswer(
            string seller, string buyer, string combinedQuestion, DateTime detectedAt)
        {
            var control = SetExactQuestion(seller, buyer, combinedQuestion, detectedAt);
            var startedAt = MarkAnswerStarted(seller, buyer, DateTime.Now);
            if (control != null) control.SetProcessing("正在获取答案...");
            var queueMs = Math.Max(0, (long)(startedAt - detectedAt).TotalMilliseconds);
            MessageProcessingTraceService.RecordGenerationStarted(
                seller, buyer, combinedQuestion, queueMs);
            Log.Info("本店回复进度进入答案生成: seller=" + seller + ", buyer=" + buyer
                + ", queueMs=" + queueMs);
            return control;
        }

        public static CtlConversation SetAnswerReady(
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
            var answerStartedAt = detected;
            var key = Key(seller, buyer);
            Entry entry;
            if (Entries.TryGetValue(key, out entry) && entry != null)
            {
                lock (entry.Sync)
                {
                    Entry current;
                    if (Entries.TryGetValue(key, out current) && ReferenceEquals(current, entry))
                    {
                        entry.AnswerReadyAt = answerReadyAt;
                        entry.Answer = answer ?? string.Empty;
                        answerStartedAt = entry.AnswerStartedAt == DateTime.MinValue ? detected : entry.AnswerStartedAt;
                    }
                }
            }
            if (control != null)
            {
                control.SetAnswer(answer, source, answerReadyAt);
                control.SetSendPending("答案已生成，准备发送...");
                DeliveryUi[DeliveryKey(seller, buyer, answer)] = new DeliveryUiEntry
                {
                    Control = control,
                    Source = source ?? string.Empty,
                    ExpiresAt = DateTime.Now.AddMinutes(3)
                };
            }
            var responseMs = Math.Max(0, (long)(answerReadyAt - detected).TotalMilliseconds);
            MessageProcessingTraceService.RecordAnswerReady(
                seller, buyer, question, answer, source, responseMs);
            Log.Info("本店回复进度答案就绪: seller=" + seller + ", buyer=" + buyer
                + ", responseMs=" + responseMs
                + ", source=" + (source ?? string.Empty));

            if (!string.IsNullOrWhiteSpace(answer) && !answer.StartsWith("错误：", StringComparison.Ordinal))
            {
                ReplyQualityMetricsService.RecordRoute(ResolveQualityRoute(source), false, 0);
                ReplyQualityMetricsService.RecordAnswerReady(
                    Math.Max(0, (long)(answerReadyAt - answerStartedAt).TotalMilliseconds),
                    Math.Max(0, (long)(answerReadyAt - detected).TotalMilliseconds),
                    Params.Robot.GetIsAutoReply());
            }

            SlowResponseAnomalyService.QueueIfSlow(
                seller, buyer, question, answer, source, detected, answerStartedAt, answerReadyAt);
            SendDeliveryWatchdog.ExpectDelivery(
                seller, buyer, question, answer, source, detected, answerReadyAt);
            return control;
        }

        public static void MarkDeliveryConfirmed(string seller, string buyer, string answer, string detail)
        {
            DeliveryUiEntry ui;
            if (DeliveryUi.TryRemove(DeliveryKey(seller, buyer, answer), out ui)
                && ui != null && ui.Control != null)
            {
                ui.Control.SetSendResult(true,
                    string.IsNullOrWhiteSpace(detail) ? "已通过卖家消息回显确认真实发送" : detail);
            }
            BotConnectionDiagnostics.RecordSendAttempt(true,
                string.IsNullOrWhiteSpace(detail) ? "卖家消息回显确认真实发送" : detail);
            MessageProcessingTraceService.RecordDelivery(seller, buyer, true, detail);
            Log.Info("本店回复卡片已按卖家回显恢复为发送成功: seller=" + seller
                + ", buyer=" + buyer + ", detail=" + (detail ?? string.Empty));
        }

        public static void MarkDeliveryTimedOut(string seller, string buyer, string answer, string detail)
        {
            DeliveryUiEntry ui;
            if (DeliveryUi.TryRemove(DeliveryKey(seller, buyer, answer), out ui)
                && ui != null && ui.Control != null)
                ui.Control.SetSendResult(false, "发送失败：" + (detail ?? string.Empty));
            MessageProcessingTraceService.RecordDelivery(seller, buyer, false, detail);
        }

        /// <summary>
        /// A human seller reply is no longer a takeover/cancellation signal. The Bot continues its
        /// already-started reply task and the human answer is retained as high-value evidence for
        /// Bot-vs-human comparison learning. Wrong-buyer/session/delivery safety remains unchanged.
        /// </summary>
        public static void MarkManualIntervention(string seller, string buyer, string sellerReply)
        {
            MessageProcessingTraceService.RecordManualObservation(seller, buyer, sellerReply);
            Entry entry;
            if (Entries.TryGetValue(Key(seller, buyer), out entry) && entry != null)
            {
                lock (entry.Sync)
                {
                    if (entry.Control != null)
                    {
                        if (entry.AnswerReadyAt == DateTime.MinValue)
                            entry.Control.SetProcessing("已观察到人工客服回复；Bot继续获取答案，稍后自动对比学习");
                        else
                            entry.Control.SetStatus("已观察到人工客服回复；Bot仍按原任务发送，并自动对比人工答案学习", false);
                    }
                }
            }
            Log.Info("已观察到人工客服回复但不取消Bot任务: seller=" + seller + ", buyer=" + buyer
                + ", reply=" + (sellerReply ?? string.Empty));
        }

        public static void ObserveNewBuyerTurn(string seller, string buyer)
        {
            // Human replies no longer create a conversation-wide intervention latch.
        }

        public static bool HasActiveManualIntervention(string seller, string buyer)
        {
            // Compatibility API: callers must not block Bot sending merely because a human replied.
            return false;
        }

        public static void Fail(string seller, string buyer, string detail)
        {
            MessageProcessingTraceService.RecordFailure(seller, buyer, detail);
            Entry entry;
            if (!Entries.TryRemove(Key(seller, buyer), out entry) || entry == null) return;
            lock (entry.Sync)
            {
                if (entry.Control != null)
                {
                    entry.Control.SetAnswer(detail ?? string.Empty, "系统", DateTime.Now);
                    entry.Control.SetSkipped(detail);
                }
            }
        }

        public static void Cancel(string seller, string buyer, string detail)
        {
            MessageProcessingTraceService.RecordCancelled(seller, buyer, detail);
            Entry entry;
            if (!Entries.TryRemove(Key(seller, buyer), out entry) || entry == null) return;
            lock (entry.Sync)
            {
                if (entry.Control != null)
                    entry.Control.SetStatus(string.IsNullOrWhiteSpace(detail) ? "回复任务已取消" : detail, false);
            }
        }

        public static void Complete(string seller, string buyer)
        {
            var key = Key(seller, buyer);
            Entry entry;
            if (!Entries.TryGetValue(key, out entry) || entry == null) return;
            lock (entry.Sync)
            {
                Entry current;
                if (!Entries.TryGetValue(key, out current) || !ReferenceEquals(current, entry)) return;
                if (entry.AnswerReadyAt == DateTime.MinValue) return;
                Entry ignored;
                Entries.TryRemove(key, out ignored);
            }
        }

        private static void CleanupDeliveryUi()
        {
            var now = DateTime.Now;
            foreach (var pair in DeliveryUi)
            {
                if (pair.Value != null && pair.Value.ExpiresAt >= now) continue;
                DeliveryUiEntry ignored;
                DeliveryUi.TryRemove(pair.Key, out ignored);
            }
        }

        private static DateTime MarkAnswerStarted(string seller, string buyer, DateTime startedAt)
        {
            var key = Key(seller, buyer);
            Entry entry;
            if (!Entries.TryGetValue(key, out entry) || entry == null) return startedAt;
            lock (entry.Sync)
            {
                Entry current;
                if (!Entries.TryGetValue(key, out current) || !ReferenceEquals(current, entry)) return startedAt;
                if (entry.AnswerStartedAt == DateTime.MinValue)
                    entry.AnswerStartedAt = startedAt == DateTime.MinValue ? DateTime.Now : startedAt;
                return entry.AnswerStartedAt;
            }
        }

        private static CtlConversation SetExactQuestion(
            string seller, string buyer, string question, DateTime detectedAt)
        {
            var control = ObserveQuestion(seller, buyer, question, detectedAt);
            var key = Key(seller, buyer);
            Entry entry;
            if (!Entries.TryGetValue(key, out entry) || entry == null) return control;
            lock (entry.Sync)
            {
                Entry current;
                if (!Entries.TryGetValue(key, out current) || !ReferenceEquals(current, entry)) return control;
                entry.Question = (question ?? string.Empty).Trim();
                if (entry.DetectedAt == DateTime.MinValue || detectedAt < entry.DetectedAt)
                    entry.DetectedAt = detectedAt == DateTime.MinValue ? DateTime.Now : detectedAt;
                if (entry.Control != null)
                {
                    entry.Control.SetQuestion(entry.Question, entry.DetectedAt);
                    control = entry.Control;
                }
            }
            return control;
        }

        private static bool ShouldDeferUnsupportedMediaCard(string question)
        {
            question = (question ?? string.Empty).Trim();
            if (!IncomingMessageSafety.IsMediaPlaceholder(question)) return false;
            if (string.Equals(question, "[图片]", StringComparison.Ordinal)
                && AiEndpointStore.GetVisionEnabledEndpoints().Count > 0) return false;
            return true;
        }

        private static string MergeQuestion(string existing, string latest)
        {
            existing = (existing ?? string.Empty).Trim();
            latest = (latest ?? string.Empty).Trim();
            if (latest.Length == 0) return existing;
            if (existing.Length == 0) return latest;
            if (string.Equals(existing, latest, StringComparison.Ordinal)) return existing;
            foreach (var line in existing.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                if (string.Equals(line.Trim(), latest, StringComparison.Ordinal)) return existing;
            var merged = existing + "\n" + latest;
            return merged.Length <= 1600 ? merged : merged.Substring(merged.Length - 1600);
        }

        private static string ResolveQualityRoute(string source)
        {
            source = (source ?? string.Empty).Trim();
            if (source.IndexOf("本地直答", StringComparison.OrdinalIgnoreCase) >= 0) return "DIRECT_KNOWLEDGE";
            if (source.IndexOf("知识上下文", StringComparison.OrdinalIgnoreCase) >= 0
                || source.IndexOf("本地知识库上下文", StringComparison.OrdinalIgnoreCase) >= 0)
                return "CONTEXTUAL_KNOWLEDGE";
            if (source.IndexOf("视觉", StringComparison.OrdinalIgnoreCase) >= 0) return "VISION";
            if (source.IndexOf("转人工", StringComparison.OrdinalIgnoreCase) >= 0
                || source.IndexOf("人工确认", StringComparison.OrdinalIgnoreCase) >= 0) return "MANUAL";
            if (source.IndexOf("本地", StringComparison.OrdinalIgnoreCase) >= 0
                || source.IndexOf("预设", StringComparison.OrdinalIgnoreCase) >= 0) return "PRESET";
            return "AI_GENERAL";
        }
    }
}
