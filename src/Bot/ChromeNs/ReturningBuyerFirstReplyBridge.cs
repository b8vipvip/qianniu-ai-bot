using Bot.ChatRecord;
using BotLib;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Bot
{
    public partial class App
    {
        private readonly object _returningBuyerFirstReplyBridgeBootstrap =
            ChromeNs.ReturningBuyerFirstReplyBridge.InitializeForApp();
    }
}

namespace Bot.ChromeNs
{
    /// <summary>
    /// Existing first-inquiry logic treats a delivered greeting as one 30-minute session.
    /// This bridge adds the requested returning-buyer rule without weakening that normal dedup:
    /// if a previously served buyer has been fully idle for more than 10 minutes but less than
    /// the existing 30-minute reset window, the next buyer message starts a new reception round
    /// and may send the configured first-inquiry fixed reply again.
    /// </summary>
    internal static class ReturningBuyerFirstReplyBridge
    {
        internal const int ReturningBuyerIdleMinutes = 10;
        private const int ExistingSessionResetMinutes = 30;

        private static readonly ConcurrentDictionary<QN, byte> SubscribedQns =
            new ConcurrentDictionary<QN, byte>();
        private static readonly ConcurrentDictionary<string, DateTime> Reservations =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private static Timer _timer;
        private static int _initialized;
        private static int _tickRunning;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                _timer = new Timer(_ => Tick(), null, 350, 700);
                Log.Info("回访买家首条回复桥已启动：已接待买家超过10分钟无互动后再次来询，也满足首次固定回复。 ");
            }
            return new object();
        }

        private static void Tick()
        {
            if (Interlocked.Exchange(ref _tickRunning, 1) != 0) return;
            try
            {
                foreach (var qn in QN.GetRuntimeSafetySnapshot())
                {
                    if (qn == null || !SubscribedQns.TryAdd(qn, 1)) continue;
                    qn.EvRecieveNewMessage += Qn_EvRecieveNewMessage;
                }
                CleanupReservations();
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("回访买家首条回复桥检查失败：" + ex.Message, 10);
            }
            finally
            {
                Interlocked.Exchange(ref _tickRunning, 0);
            }
        }

        private static void Qn_EvRecieveNewMessage(object sender, RecieveNewMessageEventArgs e)
        {
            var qn = sender as QN;
            if (qn == null || e == null || string.IsNullOrWhiteSpace(e.Message)) return;

            try
            {
                var payload = JsonConvert.DeserializeObject<ChatResponse>(e.Message);
                if (payload == null || payload.result == null) return;
                var seller = qn.Seller == null ? string.Empty : (qn.Seller.Nick ?? string.Empty).Trim();
                if (seller.Length == 0) return;

                foreach (var message in payload.result
                    .Where(x => x != null && x.fromid != null && x.toid != null))
                {
                    var from = (message.fromid.nick ?? string.Empty).Trim();
                    var to = (message.toid.nick ?? string.Empty).Trim();
                    if (from.Length == 0 || to.Length == 0 || !string.Equals(to, seller, StringComparison.Ordinal)
                        || string.Equals(from, seller, StringComparison.Ordinal)) continue;

                    var question = ExtractMessageText(message);
                    if (string.IsNullOrWhiteSpace(question)) question = message.summary ?? string.Empty;
                    if (!IsReturningAfterRequestedIdleGap(seller, from, question, DateTime.Now)) continue;

                    var key = RuntimeKey(seller, from);
                    if (!Reservations.TryAdd(key, DateTime.Now)) continue;
                    Task.Run(async () => await SendReturningGreetingAsync(qn, seller, from, question, key));
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("回访买家首条回复事件处理失败：" + ex.Message, 10);
            }
        }

        private static bool IsReturningAfterRequestedIdleGap(
            string seller,
            string buyer,
            string currentQuestion,
            DateTime now)
        {
            var priorTurns = ConversationContextStore.GetRecentTurns(seller, buyer, currentQuestion, 24);
            var latestPrior = priorTurns
                .Where(x => x != null && !x.Withdrawn && !string.IsNullOrWhiteSpace(x.Text))
                .OrderByDescending(x => x.Timestamp)
                .FirstOrDefault();
            if (latestPrior == null || latestPrior.Timestamp == DateTime.MinValue) return false;

            var idle = now - latestPrior.Timestamp;
            return idle.TotalMinutes > ReturningBuyerIdleMinutes
                && idle.TotalMinutes < ExistingSessionResetMinutes;
        }

        private static async Task SendReturningGreetingAsync(
            QN qn,
            string seller,
            string buyer,
            string question,
            string reservationKey)
        {
            try
            {
                await Task.Delay(120);
                if (!Params.Robot.CanUseRobotReal || !Params.Robot.GetIsAutoReply()) return;

                // If the ordinary first-inquiry path already owns a pending send, it keeps priority.
                if (FirstInquiryFixedReplyService.HasPending(seller, buyer)) return;

                var settings = FirstInquiryFixedReplyService.Load(seller);
                if (settings == null || !settings.Enabled || string.IsNullOrWhiteSpace(settings.Answer)) return;

                var answer = BotFeatureStore.ApplyOutputPolicy(settings.Answer.Trim()) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(answer)) return;
                answer = BotOutboundMessageFormatter.EnsureAiMarker(answer);

                KnowledgeLearningService.RegisterAnswerSource(
                    seller,
                    buyer,
                    question,
                    answer,
                    "首条咨询固定回复-10分钟回访");

                Log.Info("回访买家超过10分钟无互动，重新满足首次回复: seller=" + seller
                    + ", buyer=" + buyer);
                var ok = await qn.SendTextWithRetryAsync(buyer, answer, 1);
                if (ok)
                {
                    FirstInquiryFixedReplyService.MarkDelivered(seller, buyer);
                    ReplyDeduplicationService.RememberDelivered(seller, buyer, answer);
                }
                else
                {
                    DateTime ignored;
                    Reservations.TryRemove(reservationKey, out ignored);
                    Log.Info("回访买家首次回复发送失败，已释放10分钟回访资格: seller=" + seller
                        + ", buyer=" + buyer + ", reason="
                        + (qn.Rpa == null ? "发送失败" : qn.Rpa.GetSendFailureReason()));
                }
            }
            catch (Exception ex)
            {
                DateTime ignored;
                Reservations.TryRemove(reservationKey, out ignored);
                Log.ErrorWithMaxCount("回访买家首次回复发送失败：" + ex.Message, 10);
            }
        }

        private static string ExtractMessageText(QNChatMessage message)
        {
            if (message == null) return string.Empty;
            try
            {
                var text = message.originalData == null ? string.Empty : (message.originalData.text ?? string.Empty);
                if (message.originalData != null && message.originalData.header != null)
                    text += message.originalData.header.summary ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
            }
            catch { }
            return (message.summary ?? string.Empty).Trim();
        }

        private static string RuntimeKey(string seller, string buyer)
        {
            return (seller ?? string.Empty).Trim() + "#" + (buyer ?? string.Empty).Trim();
        }

        private static void CleanupReservations()
        {
            var cutoff = DateTime.Now.AddMinutes(-ExistingSessionResetMinutes);
            foreach (var pair in Reservations)
            {
                if (pair.Value >= cutoff) continue;
                DateTime ignored;
                Reservations.TryRemove(pair.Key, out ignored);
            }
        }
    }
}
