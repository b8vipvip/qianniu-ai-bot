using Bot.AssistWindow.Widget.Robot;
using Bot.Automation.ChatDeskNs;
using BotLib;
using System;
using System.Collections.Concurrent;

namespace Bot.ChromeNs
{
    internal static class ResponseProgressTracker
    {
        private sealed class Entry
        {
            public readonly object Sync = new object();
            public CtlConversation Control;
            public string Question = string.Empty;
            public DateTime DetectedAt = DateTime.MinValue;
            public DateTime AnswerStartedAt = DateTime.MinValue;
            public DateTime AnswerReadyAt = DateTime.MinValue;
        }

        private static readonly ConcurrentDictionary<string, Entry> Entries =
            new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal);

        private static string Key(string seller, string buyer)
        {
            return (seller ?? string.Empty).Trim() + "#" + (buyer ?? string.Empty).Trim();
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

            SendDeliveryWatchdog.OnBuyerMessageObserved(seller, buyer, detectedAt);

            if (ShouldDeferUnsupportedMediaCard(question)) return null;

            var key = Key(seller, buyer);
            while (true)
            {
                var entry = Entries.GetOrAdd(key, _ => new Entry());
                lock (entry.Sync)
                {
                    Entry current;
                    if (!Entries.TryGetValue(key, out current) || !ReferenceEquals(current, entry))
                    {
                        continue;
                    }

                    var observedAt = detectedAt == DateTime.MinValue ? DateTime.Now : detectedAt;
                    var newerTurnDuringGeneration = entry.AnswerStartedAt != DateTime.MinValue
                        && entry.DetectedAt != DateTime.MinValue
                        && observedAt > entry.DetectedAt.AddMilliseconds(5)
                        && !string.Equals(entry.Question, question, StringComparison.Ordinal);

                    // 答案已经就绪，或者上一轮已经正式进入AI生成后又收到一条真正更新的买家消息，
                    // 都必须创建全新的进度卡片。SetExactQuestion 在同一轮内部重复同步原问题时不能触发轮换。
                    if (entry.AnswerReadyAt != DateTime.MinValue || newerTurnDuringGeneration)
                    {
                        if (entry.AnswerReadyAt == DateTime.MinValue && entry.Control != null)
                        {
                            entry.Control.SetStatus("已被买家新消息替代，旧答案不会发送", false);
                        }
                        var replacement = new Entry();
                        if (!Entries.TryUpdate(key, replacement, entry)) continue;
                        continue;
                    }

                    if (entry.DetectedAt == DateTime.MinValue || detectedAt < entry.DetectedAt)
                    {
                        entry.DetectedAt = detectedAt == DateTime.MinValue ? DateTime.Now : detectedAt;
                    }
                    entry.Question = MergeQuestion(entry.Question, question);
                    if (entry.Control == null && Desk.Inst != null)
                    {
                        try
                        {
                            entry.Control = Desk.Inst.AddConversation(
                                seller,
                                buyer,
                                entry.Question,
                                "正在识别并等待买家本轮消息结束...",
                                false,
                                "处理中");
                        }
                        catch (Exception ex)
                        {
                            Log.ErrorWithMaxCount("创建回复进度卡片失败，已忽略UI异常继续处理消息：" + ex.Message, 10);
                            entry.Control = null;
                        }
                    }
                    if (entry.Control != null)
                    {
                        entry.Control.SetQuestion(entry.Question, entry.DetectedAt);
                        entry.Control.SetProcessing("已识别，等待合并本轮消息...");
                    }
                    return entry.Control;
                }
            }
        }

        public static CtlConversation BeginAnswer(
            string seller,
            string buyer,
            string combinedQuestion,
            DateTime detectedAt)
        {
            var control = SetExactQuestion(seller, buyer, combinedQuestion, detectedAt);
            var startedAt = MarkAnswerStarted(seller, buyer, DateTime.Now);
            if (control != null) control.SetProcessing("正在获取答案...");
            Log.Info("回复进度进入答案生成: seller=" + seller + ", buyer=" + buyer
                + ", queueMs=" + Math.Max(0, (long)(startedAt - detectedAt).TotalMilliseconds));
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
                        answerStartedAt = entry.AnswerStartedAt == DateTime.MinValue
                            ? detected
                            : entry.AnswerStartedAt;
                    }
                }
            }
            if (control != null)
            {
                control.SetAnswer(answer, source, answerReadyAt);
                control.SetSendPending("答案已生成，准备发送...");
            }
            Log.Info("回复进度答案就绪: seller=" + seller + ", buyer=" + buyer
                + ", responseMs=" + Math.Max(0, (long)(answerReadyAt - detected).TotalMilliseconds)
                + ", source=" + (source ?? string.Empty));

            SlowResponseAnomalyService.QueueIfSlow(
                seller,
                buyer,
                question,
                answer,
                source,
                detected,
                answerStartedAt,
                answerReadyAt);
            SendDeliveryWatchdog.ExpectDelivery(
                seller,
                buyer,
                question,
                answer,
                source,
                detected,
                answerReadyAt);
            return control;
        }

        public static void MarkManualIntervention(string seller, string buyer, string sellerReply)
        {
            Entry entry;
            if (!Entries.TryRemove(Key(seller, buyer), out entry) || entry == null) return;
            lock (entry.Sync)
            {
                if (entry.AnswerReadyAt == DateTime.MinValue && entry.Control != null)
                {
                    entry.Control.SetStatus("检测到客服已人工回复，停止等待旧AI答案", true);
                }
            }
            Log.Info("回复进度因人工客服介入结束: seller=" + seller + ", buyer=" + buyer
                + ", reply=" + (sellerReply ?? string.Empty));
        }

        public static void Fail(string seller, string buyer, string detail)
        {
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
                {
                    entry.AnswerStartedAt = startedAt == DateTime.MinValue ? DateTime.Now : startedAt;
                }
                return entry.AnswerStartedAt;
            }
        }

        private static CtlConversation SetExactQuestion(
            string seller,
            string buyer,
            string question,
            DateTime detectedAt)
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
                {
                    entry.DetectedAt = detectedAt == DateTime.MinValue ? DateTime.Now : detectedAt;
                }
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
                && AiEndpointStore.GetVisionEnabledEndpoints().Count > 0)
            {
                return false;
            }
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
            {
                if (string.Equals(line.Trim(), latest, StringComparison.Ordinal)) return existing;
            }
            var merged = existing + "\n" + latest;
            return merged.Length <= 1600 ? merged : merged.Substring(merged.Length - 1600);
        }
    }
}
