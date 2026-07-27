using BotLib;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal static class SendDeliveryWatchdog
    {
        private const int VerifyDelayMilliseconds = 9000;
        private static readonly ConcurrentDictionary<string, PendingDelivery> Pending =
            new ConcurrentDictionary<string, PendingDelivery>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, DateTime> KnownBotAnswers =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);

        private sealed class PendingDelivery
        {
            public string Id;
            public string Seller;
            public string Buyer;
            public string Question;
            public string Answer;
            public string Source;
            public DateTime DetectedAt;
            public DateTime AnswerReadyAt;
            public DateTime WatchStartedAt;
            public int Started;
        }

        public static void OnBuyerMessageObserved(string seller, string buyer, DateTime observedAt)
        {
            // 新买家消息不能取消上一条答案的送达核验；已经真正按下发送键的尝试仍须等待回显。
            // 但尚未真正发送的旧答案是否仍允许重试，由 QNRpa 的答案时效租约单独判断。
        }

        public static void ExpectDelivery(
            string seller,
            string buyer,
            string question,
            string answer,
            string source,
            DateTime detectedAt,
            DateTime answerReadyAt,
            bool force = false)
        {
            if (!Params.Robot.CanUseRobotReal) return;
            if (!force && !Params.Robot.GetIsAutoReply()) return;
            answer = (answer ?? string.Empty).Trim();
            if (answer.Length == 0 || answer.StartsWith("错误：", StringComparison.Ordinal)) return;

            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();
            if (seller.Length == 0 || buyer.Length == 0) return;

            var existing = FindPending(seller, buyer, answer);
            if (existing != null)
            {
                existing.Question = question ?? existing.Question;
                existing.Source = source ?? existing.Source;
                return;
            }

            var readyAt = answerReadyAt == DateTime.MinValue ? DateTime.Now : answerReadyAt;
            var pending = new PendingDelivery
            {
                Id = Guid.NewGuid().ToString("N"),
                Seller = seller,
                Buyer = buyer,
                Question = question ?? string.Empty,
                Answer = answer,
                Source = source ?? string.Empty,
                DetectedAt = detectedAt == DateTime.MinValue ? readyAt : detectedAt,
                AnswerReadyAt = readyAt,
                WatchStartedAt = DateTime.MinValue,
                Started = 0
            };
            Pending[pending.Id] = pending;
            Log.Info("已准备真实发送回显监控（尚未开始计时）: seller=" + pending.Seller
                + ", buyer=" + pending.Buyer + ", watchdogId=" + pending.Id + ", force=" + force);
        }

        public static string EnsurePending(string seller, string buyer, string answer)
        {
            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();
            answer = (answer ?? string.Empty).Trim();
            if (seller.Length == 0 || buyer.Length == 0 || answer.Length == 0) return string.Empty;

            var pending = FindPending(seller, buyer, answer);
            if (pending == null)
            {
                var now = DateTime.Now;
                pending = new PendingDelivery
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Seller = seller,
                    Buyer = buyer,
                    Question = string.Empty,
                    Answer = answer,
                    Source = "自动发送",
                    DetectedAt = now,
                    AnswerReadyAt = now,
                    WatchStartedAt = DateTime.MinValue,
                    Started = 0
                };
                Pending[pending.Id] = pending;
            }

            Activate(pending);
            return pending.Id;
        }

        public static int CancelPending(string seller, string buyer, string answer, string reason)
        {
            var normalized = Normalize(answer);
            var matches = Pending
                .Where(pair => pair.Value != null
                    && string.Equals(pair.Value.Seller, (seller ?? string.Empty).Trim(), StringComparison.Ordinal)
                    && string.Equals(pair.Value.Buyer, (buyer ?? string.Empty).Trim(), StringComparison.Ordinal)
                    && pair.Value.Started == 0
                    && Normalize(pair.Value.Answer) == normalized)
                .ToList();
            var removedCount = 0;
            foreach (var pair in matches)
            {
                PendingDelivery removed;
                if (Pending.TryRemove(pair.Key, out removed)) removedCount++;
            }
            if (removedCount > 0)
            {
                Log.Info("已取消未开始/不再有效的发送回显监控: seller=" + seller
                    + ", buyer=" + buyer + ", count=" + removedCount + ", reason=" + (reason ?? string.Empty));
            }
            return removedCount;
        }

        public static int CancelConversation(string seller, string buyer, string reason)
        {
            var matches = Pending
                .Where(pair => pair.Value != null
                    && string.Equals(pair.Value.Seller, (seller ?? string.Empty).Trim(), StringComparison.Ordinal)
                    && string.Equals(pair.Value.Buyer, (buyer ?? string.Empty).Trim(), StringComparison.Ordinal))
                .ToList();
            var removedCount = 0;
            foreach (var pair in matches)
            {
                PendingDelivery removed;
                if (Pending.TryRemove(pair.Key, out removed)) removedCount++;
            }
            if (removedCount > 0)
            {
                Log.Info("人工介入后已取消发送回显监控: seller=" + seller
                    + ", buyer=" + buyer + ", count=" + removedCount + ", reason=" + (reason ?? string.Empty));
            }
            return removedCount;
        }

        private static PendingDelivery FindPending(string seller, string buyer, string answer)
        {
            var normalized = Normalize(answer);
            return Pending.Values.FirstOrDefault(value => value != null
                && string.Equals(value.Seller, seller, StringComparison.Ordinal)
                && string.Equals(value.Buyer, buyer, StringComparison.Ordinal)
                && Normalize(value.Answer) == normalized);
        }

        private static void Activate(PendingDelivery pending)
        {
            if (pending == null || Interlocked.CompareExchange(ref pending.Started, 1, 0) != 0) return;
            pending.WatchStartedAt = DateTime.Now;
            Log.Info("已启动真实发送回显监控: seller=" + pending.Seller
                + ", buyer=" + pending.Buyer + ", watchdogId=" + pending.Id);

            Task.Run(async () =>
            {
                await Task.Delay(VerifyDelayMilliseconds);
                PendingDelivery current;
                if (!Pending.TryGetValue(pending.Id, out current)
                    || current == null
                    || !ReferenceEquals(current, pending))
                {
                    return;
                }

                var delivered = false;
                try
                {
                    var qn = QN.FindExistingBySellerNick(pending.Seller);
                    delivered = qn != null
                        && qn.HasRecentSellerEcho(pending.Buyer, pending.Answer, pending.WatchStartedAt);
                }
                catch (Exception ex)
                {
                    Log.Info("发送回显监控检查异常: " + ex.Message);
                }

                PendingDelivery removed;
                if (!Pending.TryRemove(pending.Id, out removed) || !ReferenceEquals(removed, pending)) return;
                if (!delivered)
                {
                    ReplyQualityMetricsService.RecordSendResult(
                        false,
                        Math.Max(0, (long)(DateTime.Now - pending.DetectedAt).TotalMilliseconds));
                    var reason = "答案已经生成并进入自动发送流程，并且已真正进入发送动作，但在 "
                        + (VerifyDelayMilliseconds / 1000) + " 秒内未检测到相同内容的卖家消息回显。"
                        + "可能是输入框/发送按钮操作未真正送达、回显事件缺失，或发送结果被错误判定。";
                    ResponseProgressTracker.MarkDeliveryTimedOut(
                        pending.Seller, pending.Buyer, pending.Answer, reason);
                    Log.Error("[发送异常] seller=" + pending.Seller
                        + ", buyer=" + pending.Buyer + ", watchdogId=" + pending.Id
                        + ", reason=" + reason);
                    SendFailureAnomalyService.Queue(
                        pending.Seller,
                        pending.Buyer,
                        pending.Question,
                        pending.Answer,
                        pending.Source,
                        reason,
                        pending.DetectedAt,
                        pending.AnswerReadyAt,
                        DateTime.Now);
                    return;
                }

                ReplyQualityMetricsService.RecordSendResult(
                    true,
                    Math.Max(0, (long)(DateTime.Now - pending.DetectedAt).TotalMilliseconds));
                ResponseProgressTracker.MarkDeliveryConfirmed(
                    pending.Seller, pending.Buyer, pending.Answer, "延迟回显确认已发送");
                Log.Info("发送回显监控确认成功: seller=" + pending.Seller
                    + ", buyer=" + pending.Buyer + ", watchdogId=" + pending.Id);
            });
        }

        public static bool ConfirmDelivery(string seller, string buyer, string answer)
        {
            var normalized = Normalize(answer);
            if (normalized.Length == 0) return false;

            var matched = Pending
                .Where(pair => pair.Value != null
                    && pair.Value.Started != 0
                    && string.Equals(pair.Value.Seller, (seller ?? string.Empty).Trim(), StringComparison.Ordinal)
                    && string.Equals(pair.Value.Buyer, (buyer ?? string.Empty).Trim(), StringComparison.Ordinal)
                    && Normalize(pair.Value.Answer) == normalized)
                .ToList();

            var confirmed = false;
            foreach (var pair in matched)
            {
                PendingDelivery removed;
                if (Pending.TryRemove(pair.Key, out removed))
                {
                    confirmed = true;
                    if (removed != null)
                    {
                        ReplyQualityMetricsService.RecordSendResult(
                            true,
                            Math.Max(0, (long)(DateTime.Now - removed.DetectedAt).TotalMilliseconds));
                        ResponseProgressTracker.MarkDeliveryConfirmed(
                            removed.Seller, removed.Buyer, removed.Answer, "已通过卖家消息回显确认真实发送");
                    }
                }
            }
            if (confirmed)
            {
                KnownBotAnswers[AnswerKey(seller, buyer, answer)] = DateTime.Now.AddMinutes(2);
                CleanupKnownAnswers();
                Log.Info("通过卖家消息回显确认Bot真实发送: seller=" + seller
                    + ", buyer=" + buyer + ", matchedWatchdogs=" + matched.Count);
                return true;
            }

            DateTime expiresAt;
            if (KnownBotAnswers.TryGetValue(AnswerKey(seller, buyer, answer), out expiresAt)
                && expiresAt >= DateTime.Now)
            {
                return true;
            }
            return false;
        }

        public static bool IsKnownBotAnswer(string seller, string buyer, string answer)
        {
            DateTime expiresAt;
            return KnownBotAnswers.TryGetValue(AnswerKey(seller, buyer, answer), out expiresAt)
                && expiresAt >= DateTime.Now;
        }

        private static void CleanupKnownAnswers()
        {
            var now = DateTime.Now;
            foreach (var pair in KnownBotAnswers)
            {
                if (pair.Value >= now) continue;
                DateTime ignored;
                KnownBotAnswers.TryRemove(pair.Key, out ignored);
            }
        }

        private static string ConversationKey(string seller, string buyer)
        {
            return (seller ?? string.Empty).Trim() + "#" + (buyer ?? string.Empty).Trim();
        }

        private static string AnswerKey(string seller, string buyer, string answer)
        {
            return ConversationKey(seller, buyer) + "#" + Normalize(answer);
        }

        private static string Normalize(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim(), @"\s+", string.Empty);
        }
    }
}
