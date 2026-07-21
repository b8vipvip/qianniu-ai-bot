using BotLib;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.RegularExpressions;
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
        }

        public static void OnBuyerMessageObserved(string seller, string buyer, DateTime observedAt)
        {
            // 新买家消息不能取消上一条答案的送达核验。
            // 否则“答案其实没发出去，买家又追问了一次”的关键故障会被静默吞掉。
            // 每个待发送答案都使用独立 watchdogId，直到卖家真实回显确认或超时产生异常报告。
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

            var readyAt = answerReadyAt == DateTime.MinValue ? DateTime.Now : answerReadyAt;
            var pending = new PendingDelivery
            {
                Id = Guid.NewGuid().ToString("N"),
                Seller = (seller ?? string.Empty).Trim(),
                Buyer = (buyer ?? string.Empty).Trim(),
                Question = question ?? string.Empty,
                Answer = answer,
                Source = source ?? string.Empty,
                DetectedAt = detectedAt == DateTime.MinValue ? readyAt : detectedAt,
                AnswerReadyAt = readyAt
            };
            if (pending.Seller.Length == 0 || pending.Buyer.Length == 0) return;

            Pending[pending.Id] = pending;
            KnownBotAnswers[AnswerKey(pending.Seller, pending.Buyer, pending.Answer)] = DateTime.Now.AddMinutes(2);
            CleanupKnownAnswers();

            Log.Info("已启动真实发送回显监控: seller=" + pending.Seller
                + ", buyer=" + pending.Buyer + ", watchdogId=" + pending.Id
                + ", force=" + force);

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
                        && qn.HasRecentSellerEcho(pending.Buyer, pending.Answer, pending.AnswerReadyAt);
                }
                catch (Exception ex)
                {
                    Log.Info("发送回显监控检查异常: " + ex.Message);
                }

                PendingDelivery removed;
                if (!Pending.TryRemove(pending.Id, out removed) || !ReferenceEquals(removed, pending)) return;
                if (delivered)
                {
                    Log.Info("发送回显监控确认成功: seller=" + pending.Seller
                        + ", buyer=" + pending.Buyer + ", watchdogId=" + pending.Id);
                    return;
                }

                var reason = "答案已经生成并进入自动发送流程，但在 "
                    + (VerifyDelayMilliseconds / 1000) + " 秒内未检测到相同内容的卖家消息回显。"
                    + "可能是误走智能提示接口、会话切换失败、输入框/发送按钮操作未真正送达，或发送结果被错误判定为成功。";
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
            });
        }

        public static bool ConfirmDelivery(string seller, string buyer, string answer)
        {
            var normalized = Normalize(answer);
            if (normalized.Length == 0) return false;

            var matched = Pending
                .Where(pair => pair.Value != null
                    && string.Equals(pair.Value.Seller, (seller ?? string.Empty).Trim(), StringComparison.Ordinal)
                    && string.Equals(pair.Value.Buyer, (buyer ?? string.Empty).Trim(), StringComparison.Ordinal)
                    && Normalize(pair.Value.Answer) == normalized)
                .ToList();

            var confirmed = false;
            foreach (var pair in matched)
            {
                PendingDelivery ignored;
                if (Pending.TryRemove(pair.Key, out ignored)) confirmed = true;
            }
            if (confirmed)
            {
                KnownBotAnswers[AnswerKey(seller, buyer, answer)] = DateTime.Now.AddMinutes(2);
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
