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
        private readonly object _returningBuyerFirstReplyBridge =
            ChromeNs.ReturningBuyerFirstReplyBridge.InitializeForApp();
    }
}

namespace Bot.ChromeNs
{
    internal static class ReturningBuyerFirstReplyBridge
    {
        internal const int ReturningBuyerIdleMinutes = 10;
        private const int ExistingSessionResetMinutes = 30;
        private static readonly ConcurrentDictionary<QN, byte> Qns = new ConcurrentDictionary<QN, byte>();
        private static readonly ConcurrentDictionary<string, DateTime> Reservations = new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private static Timer _timer;
        private static int _initialized;
        private static int _running;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                _timer = new Timer(_ => Tick(), null, 350, 700);
                Log.Info("回访买家首条回复已启用：超过10分钟无互动后再次来询，重新满足首次回复。");
            }
            return new object();
        }

        private static void Tick()
        {
            if (Interlocked.Exchange(ref _running, 1) != 0) return;
            try
            {
                foreach (var qn in QN.GetRuntimeSafetySnapshot())
                {
                    if (qn == null || !Qns.TryAdd(qn, 1)) continue;
                    qn.EvRecieveNewMessage += OnMessage;
                }
                var cutoff = DateTime.Now.AddMinutes(-ReturningBuyerIdleMinutes);
                foreach (var x in Reservations)
                {
                    if (x.Value >= cutoff) continue;
                    DateTime ignored;
                    Reservations.TryRemove(x.Key, out ignored);
                }
            }
            catch (Exception ex) { Log.ErrorWithMaxCount("回访首答检查失败：" + ex.Message, 10); }
            finally { Interlocked.Exchange(ref _running, 0); }
        }

        private static void OnMessage(object sender, RecieveNewMessageEventArgs e)
        {
            var qn = sender as QN;
            if (qn == null || e == null || string.IsNullOrWhiteSpace(e.Message)) return;
            try
            {
                var data = JsonConvert.DeserializeObject<ChatResponse>(e.Message);
                var seller = qn.Seller == null ? "" : (qn.Seller.Nick ?? "").Trim();
                if (data == null || data.result == null || seller.Length == 0) return;
                foreach (var m in data.result.Where(x => x != null && x.fromid != null && x.toid != null))
                {
                    var buyer = (m.fromid.nick ?? "").Trim();
                    var to = (m.toid.nick ?? "").Trim();
                    if (buyer.Length == 0 || to != seller || buyer == seller) continue;
                    var question = MessageText(m);
                    var prior = ConversationContextStore.GetRecentTurns(seller, buyer, question, 24)
                        .Where(x => x != null && !x.Withdrawn && !string.IsNullOrWhiteSpace(x.Text))
                        .OrderByDescending(x => x.Timestamp).FirstOrDefault();
                    if (prior == null || prior.Timestamp == DateTime.MinValue) continue;
                    var idle = DateTime.Now - prior.Timestamp;
                    if (idle.TotalMinutes <= ReturningBuyerIdleMinutes || idle.TotalMinutes >= ExistingSessionResetMinutes) continue;
                    var key = seller + "#" + buyer;
                    if (!Reservations.TryAdd(key, DateTime.Now)) continue;
                    Task.Run(async () => await SendAsync(qn, seller, buyer, question, key));
                }
            }
            catch (Exception ex) { Log.ErrorWithMaxCount("回访首答事件处理失败：" + ex.Message, 10); }
        }

        private static async Task SendAsync(QN qn, string seller, string buyer, string question, string key)
        {
            try
            {
                await Task.Delay(120);
                if (!Params.Robot.CanUseRobotReal || !Params.Robot.GetIsAutoReply() || FirstInquiryFixedReplyService.HasPending(seller, buyer))
                {
                    Release(key);
                    return;
                }
                var cfg = FirstInquiryFixedReplyService.Load(seller);
                if (cfg == null || !cfg.Enabled || string.IsNullOrWhiteSpace(cfg.Answer)) { Release(key); return; }
                var answer = BotFeatureStore.ApplyOutputPolicy(cfg.Answer.Trim()) ?? "";
                if (answer.Length == 0) { Release(key); return; }
                answer = BotOutboundMessageFormatter.EnsureAiMarker(answer);
                KnowledgeLearningService.RegisterAnswerSource(seller, buyer, question, answer, "首条咨询固定回复-10分钟回访");
                var ok = await qn.SendTextWithRetryAsync(buyer, answer, 1);
                if (ok)
                {
                    FirstInquiryFixedReplyService.MarkDelivered(seller, buyer);
                    ReplyDeduplicationService.RememberDelivered(seller, buyer, answer);
                    Log.Info("回访买家超过10分钟无互动，首次固定回复已发送: seller=" + seller + ", buyer=" + buyer);
                }
                else Release(key);
            }
            catch (Exception ex)
            {
                Release(key);
                Log.ErrorWithMaxCount("回访首答发送失败：" + ex.Message, 10);
            }
        }

        private static string MessageText(QNChatMessage m)
        {
            try
            {
                var text = m.originalData == null ? "" : (m.originalData.text ?? "");
                if (m.originalData != null && m.originalData.header != null) text += m.originalData.header.summary ?? "";
                if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
            }
            catch { }
            return (m.summary ?? "").Trim();
        }

        private static void Release(string key)
        {
            DateTime ignored;
            Reservations.TryRemove(key, out ignored);
        }
    }
}
