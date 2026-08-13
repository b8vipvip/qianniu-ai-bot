using Bot.ChatRecord;
using Bot.ShopScope;
using BotLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Bot
{
    public partial class App
    {
        private readonly object _firstInquiryDeliveryBridgeBootstrap =
            ChromeNs.FirstInquiryDeliveryBridge.InitializeForApp();
    }
}

namespace Bot.ChromeNs
{
    /// <summary>
    /// Keeps the first-consultation greeting reliable across the two paths Qianniu can use:
    /// ordinary buyer messages and order/system events that are claimed by the order router first.
    /// It also confirms the greeting from the seller echo so a successful send, rather than merely
    /// generating an answer, owns the 30-minute first-reply dedup window.
    /// </summary>
    internal static class FirstInquiryDeliveryBridge
    {
        private static readonly DateTime StartedAt = DateTime.Now;
        private static readonly ConcurrentDictionary<QN, byte> SubscribedQns =
            new ConcurrentDictionary<QN, byte>();
        private static readonly ConcurrentDictionary<string, DateTime> ScheduledOrders =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private static Timer _timer;
        private static int _initialized;
        private static int _tickRunning;
        private static string _lastOrderStateStamp = string.Empty;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                _timer = new Timer(_ => Tick(), null, 300, 500);
                Log.Info("首条咨询送达确认桥已启动：普通消息按卖家回显确认，订单首事件优先补发首条固定回复。");
            }
            return new object();
        }

        private static void Tick()
        {
            if (Interlocked.Exchange(ref _tickRunning, 1) != 0) return;
            try
            {
                AttachQnEchoObservers();
                ScanFreshOrderEvents();
                CleanupScheduledOrders();
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("首条咨询送达确认桥检查失败：" + ex.Message, 10);
            }
            finally
            {
                Interlocked.Exchange(ref _tickRunning, 0);
            }
        }

        private static void AttachQnEchoObservers()
        {
            foreach (var qn in QN.GetRuntimeSafetySnapshot())
            {
                if (qn == null || !SubscribedQns.TryAdd(qn, 1)) continue;
                qn.EvRecieveNewMessage += Qn_EvRecieveNewMessage;
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

                foreach (var message in payload.result.Where(x => x != null && x.fromid != null && x.toid != null))
                {
                    if (!string.Equals((message.fromid.nick ?? string.Empty).Trim(), seller, StringComparison.Ordinal)) continue;
                    var buyer = (message.toid.nick ?? string.Empty).Trim();
                    if (buyer.Length == 0 || !FirstInquiryFixedReplyService.HasPending(seller, buyer)) continue;

                    var text = ExtractMessageText(message);
                    var settings = FirstInquiryFixedReplyService.Load(seller);
                    if (settings == null || string.IsNullOrWhiteSpace(settings.Answer)) continue;
                    if (!GreetingEchoMatches(text, settings.Answer)) continue;

                    FirstInquiryFixedReplyService.MarkDelivered(seller, buyer);
                    Log.Info("首条咨询固定回复已由千牛卖家回显确认送达: seller=" + seller
                        + ", buyer=" + buyer);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("首条咨询卖家回显确认失败：" + ex.Message, 10);
            }
        }

        private static bool GreetingEchoMatches(string actual, string configured)
        {
            actual = NormalizeGreeting(actual);
            configured = NormalizeGreeting(configured);
            if (actual.Length == 0 || configured.Length == 0) return false;
            return string.Equals(actual, configured, StringComparison.Ordinal)
                || actual.StartsWith(configured, StringComparison.Ordinal)
                || actual.EndsWith(configured, StringComparison.Ordinal);
        }

        private static string NormalizeGreeting(string value)
        {
            value = (value ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();
            value = value.Replace("[AI]", string.Empty).Replace("【AI】", string.Empty).Trim();
            return value;
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

        private static void ScanFreshOrderEvents()
        {
            var path = GetOrderEventStatePath();
            if (!File.Exists(path)) return;
            var info = new FileInfo(path);
            var stamp = info.LastWriteTimeUtc.Ticks + "#" + info.Length;
            if (string.Equals(stamp, _lastOrderStateStamp, StringComparison.Ordinal)) return;
            _lastOrderStateStamp = stamp;

            JObject root;
            try
            {
                root = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (IOException)
            {
                return;
            }

            var events = root["Events"] as JArray;
            if (events == null) return;
            foreach (var item in events.OfType<JObject>())
            {
                DateTime seenAt;
                if (!DateTime.TryParse(Convert.ToString(item["SeenAt"]), out seenAt)) continue;
                if (seenAt.Kind == DateTimeKind.Utc) seenAt = seenAt.ToLocalTime();
                if (seenAt < StartedAt.AddSeconds(-2)) continue;

                OrderSnapshot snapshot;
                try { snapshot = item["Snapshot"] == null ? null : item["Snapshot"].ToObject<OrderSnapshot>(); }
                catch { snapshot = null; }
                if (snapshot == null
                    || (snapshot.EventType != OrderEventType.Created && snapshot.EventType != OrderEventType.Paid)
                    || string.IsNullOrWhiteSpace(snapshot.Seller)
                    || string.IsNullOrWhiteSpace(snapshot.Buyer)
                    || string.IsNullOrWhiteSpace(snapshot.OrderId)) continue;

                var key = (snapshot.Seller ?? string.Empty).Trim().ToLowerInvariant()
                    + "#" + (snapshot.OrderId ?? string.Empty).Trim() + "#first-inquiry";
                if (!ScheduledOrders.TryAdd(key, DateTime.Now)) continue;
                Task.Run(async () => await SendOrderFirstGreetingAsync(snapshot, key));
            }
        }

        private static async Task SendOrderFirstGreetingAsync(OrderSnapshot snapshot, string scheduleKey)
        {
            try
            {
                QN qn = null;
                for (var i = 0; i < 12 && qn == null; i++)
                {
                    qn = QN.FindExistingBySellerNick(snapshot.Seller);
                    if (qn == null) await Task.Delay(200);
                }
                if (qn == null)
                {
                    DateTime ignored;
                    ScheduledOrders.TryRemove(scheduleKey, out ignored);
                    return;
                }

                ShopContext shop = null;
                try { shop = ShopContextLocator.ResolveBySellerNick(snapshot.Seller); }
                catch { shop = null; }
                if (shop == null) return;

                using (ShopSettingsScope.Enter(shop))
                {
                    if (!Params.Robot.CanUseRobotReal || !Params.Robot.GetIsAutoReply()) return;

                    var buyer = BuyerIdentityAliasService.ResolveInternalNick(snapshot.Seller, snapshot.Buyer);
                    if (string.IsNullOrWhiteSpace(buyer)) buyer = snapshot.Buyer;
                    var question = string.IsNullOrWhiteSpace(snapshot.EventText)
                        ? "[新订单] " + snapshot.OrderId
                        : snapshot.EventText;
                    string greeting;
                    if (!FirstInquiryFixedReplyService.TryResolve(snapshot.Seller, buyer, question, out greeting))
                    {
                        // Ordinary receiveNewMsg may already own the first greeting. Do not race it.
                        return;
                    }

                    greeting = BotOutboundMessageFormatter.EnsureAiMarker(greeting);
                    KnowledgeLearningService.RegisterAnswerSource(
                        snapshot.Seller,
                        buyer,
                        question,
                        greeting,
                        "首条咨询固定回复-订单首事件");
                    var ok = await qn.SendTextWithRetryAsync(buyer, greeting, 1);
                    if (ok)
                    {
                        FirstInquiryFixedReplyService.MarkDelivered(snapshot.Seller, buyer);
                        ReplyDeduplicationService.RememberDelivered(snapshot.Seller, buyer, greeting);
                    }
                    else
                    {
                        FirstInquiryFixedReplyService.ReleaseReservation(
                            snapshot.Seller,
                            buyer,
                            qn.Rpa == null ? "发送失败" : qn.Rpa.GetSendFailureReason());
                    }
                    Log.Info("订单首事件首条咨询固定回复完成: seller=" + snapshot.Seller
                        + ", buyer=" + buyer + ", orderId=" + snapshot.OrderId + ", success=" + ok);
                }
            }
            catch (Exception ex)
            {
                DateTime ignored;
                ScheduledOrders.TryRemove(scheduleKey, out ignored);
                Log.ErrorWithMaxCount("订单首事件首条咨询固定回复失败：" + ex.Message, 10);
            }
        }

        private static string GetOrderEventStatePath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QianniuAiBot",
                "data",
                "order-event-state.json");
        }

        private static void CleanupScheduledOrders()
        {
            var cutoff = DateTime.Now.AddHours(-24);
            foreach (var pair in ScheduledOrders)
            {
                if (pair.Value >= cutoff) continue;
                DateTime ignored;
                ScheduledOrders.TryRemove(pair.Key, out ignored);
            }
        }
    }
}
