using Bot.ChatRecord;
using Bot.Knowledge;
using Bot.ShopScope;
using BotLib;
using DbEntity;
using DbEntity.Response;
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
                    if (buyer.Length == 0) continue;

                    // A genuine seller message means this conversation has advanced on the seller side.
                    // If the composer still contains the exact draft that this Bot previously wrote,
                    // clear only that owned draft. The RPA helper verifies both the active buyer and
                    // exact editor contents, so human typing or a different conversation is untouched.
                    if (qn.Rpa != null)
                    {
                        qn.Rpa.TryClearCanceledBotDraft(
                            buyer,
                            "检测到同会话卖家消息，清理已取消但仍残留在输入框的Bot草稿");
                    }

                    if (!FirstInquiryFixedReplyService.HasPending(seller, buyer)) continue;

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

namespace Bot
{
    public partial class App
    {
        private readonly object _backgroundOrderPanelRecoveryBootstrap =
            ChromeNs.BackgroundOrderPanelRecoveryBridge.InitializeForApp();
    }
}

namespace Bot.ChromeNs
{
    /// <summary>
    /// Qianniu sometimes emits only a background conversation notification and refreshes the
    /// right-side order panel later, without a receiveNewMsg order card or messageCenter order ID.
    /// Keep probing that buyer for a short bounded window. Passive probes run first; only when the
    /// Bot is idle and human-protection allows it may we open the target buyer to read the panel.
    /// </summary>
    internal static class BackgroundOrderPanelRecoveryBridge
    {
        private sealed class ProbeState
        {
            public DateTime StartedAt;
            public int Running;
        }

        private static readonly ConcurrentDictionary<QN, byte> AttachedQns =
            new ConcurrentDictionary<QN, byte>();
        private static readonly ConcurrentDictionary<string, ProbeState> Probes =
            new ConcurrentDictionary<string, ProbeState>(StringComparer.Ordinal);
        private static readonly int[] ProbeDelaysMs =
            { 500, 1500, 3200, 6000, 10000, 16000, 24000, 36000 };
        private static Timer _timer;
        private static int _initialized;
        private static int _tickRunning;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                _timer = new Timer(_ => Tick(), null, 250, 650);
                Log.Info("后台订单面板延迟兜底已启动：后台买家通知将持续36秒安全补扫新订单。");
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
                    if (qn == null || !AttachedQns.TryAdd(qn, 1)) continue;
                    qn.EvShopRobotReceriveNewMessage += Qn_EvShopRobotReceriveNewMessage;
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("后台订单面板延迟兜底绑定失败：" + ex.Message, 10);
            }
            finally
            {
                Interlocked.Exchange(ref _tickRunning, 0);
            }
        }

        private static void Qn_EvShopRobotReceriveNewMessage(object sender, ShopRobotReceriveNewMessageEventArgs e)
        {
            var qn = sender as QN;
            if (qn == null || e == null || e.Seller == null || e.Buyer == null) return;
            var seller = (e.Seller.Nick ?? string.Empty).Trim();
            var buyer = (e.Buyer.Nick ?? string.Empty).Trim();
            if (seller.Length == 0 || buyer.Length == 0) return;

            var normalizedBuyer = BuyerIdentityAliasService.ResolveInternalNick(seller, buyer);
            if (!string.IsNullOrWhiteSpace(normalizedBuyer)) buyer = normalizedBuyer;
            var key = seller.ToLowerInvariant() + "#" + buyer.ToLowerInvariant();
            var now = DateTime.Now;
            var state = Probes.AddOrUpdate(
                key,
                _ => new ProbeState { StartedAt = now },
                (_, old) => old == null || old.StartedAt < now.AddSeconds(-50)
                    ? new ProbeState { StartedAt = now }
                    : old);
            if (Interlocked.Exchange(ref state.Running, 1) != 0) return;

            Log.Info("后台买家通知已进入订单延迟补扫: seller=" + seller + ", buyer=" + buyer);
            Task.Run(async () =>
            {
                var elapsed = 0;
                try
                {
                    foreach (var targetDelay in ProbeDelaysMs)
                    {
                        var wait = Math.Max(0, targetDelay - elapsed);
                        if (wait > 0) await Task.Delay(wait).ConfigureAwait(false);
                        elapsed = targetDelay;
                        var mayActivateBuyer = targetDelay >= 3200;
                        if (await qn.TryRecoverVisibleOrderPanelForBackgroundProbeAsync(
                            seller,
                            buyer,
                            "shopRobot后台延迟补扫",
                            state.StartedAt,
                            mayActivateBuyer).ConfigureAwait(false))
                        {
                            return;
                        }
                    }
                    Log.Info("后台订单面板延迟补扫结束：36秒内未发现可确认的新订单。seller="
                        + seller + ", buyer=" + buyer);
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("后台订单面板延迟补扫异常: seller=" + seller
                        + ", buyer=" + buyer + ", error=" + ex.Message, 20);
                }
                finally
                {
                    ProbeState current;
                    if (Probes.TryGetValue(key, out current) && ReferenceEquals(current, state))
                    {
                        ProbeState ignored;
                        Probes.TryRemove(key, out ignored);
                    }
                }
            });
        }
    }

    public partial class QN
    {
        internal async Task<bool> TryRecoverVisibleOrderPanelForBackgroundProbeAsync(
            string sellerHint,
            string buyerHint,
            string source,
            DateTime probeStartedAt,
            bool mayActivateBuyer)
        {
            var runtimeSeller = Seller == null ? string.Empty : (Seller.Nick ?? string.Empty).Trim();
            sellerHint = (sellerHint ?? string.Empty).Trim();
            buyerHint = (buyerHint ?? string.Empty).Trim();
            if (runtimeSeller.Length == 0 || buyerHint.Length == 0 || cdp == null) return false;
            if (sellerHint.Length > 0 && !DirectOrderIdentityResolver.IdentityEquals(runtimeSeller, sellerHint)) return true;

            Conversation before = null;
            try
            {
                var current = await GetCurrentConversationID().ConfigureAwait(false);
                before = current == null ? null : current.Result;
            }
            catch { }

            var targetActive = before != null
                && !string.IsNullOrWhiteSpace(before.Nick)
                && BuyerIdentityAliasService.AreEquivalent(runtimeSeller, before.Nick, buyerHint);
            if (!targetActive && mayActivateBuyer)
            {
                string blockedReason;
                if (!BotActivityCoordinator.IsSafeToAutoFocus(runtimeSeller, out blockedReason))
                {
                    Log.Info("后台订单补扫暂不切换买家：" + blockedReason + ", target=" + buyerHint);
                    return false;
                }

                var openNick = BuyerIdentityAliasService.ResolveInternalNick(runtimeSeller, buyerHint);
                if (string.IsNullOrWhiteSpace(openNick)) openNick = buyerHint;
                OpenChat(openNick);
                if (!string.Equals(openNick, buyerHint, StringComparison.Ordinal))
                {
                    await Task.Delay(180).ConfigureAwait(false);
                    OpenChat(buyerHint);
                }

                for (var attempt = 0; attempt < 10; attempt++)
                {
                    await Task.Delay(220).ConfigureAwait(false);
                    try
                    {
                        var current = await GetCurrentConversationID().ConfigureAwait(false);
                        before = current == null ? null : current.Result;
                    }
                    catch
                    {
                        before = null;
                    }
                    targetActive = before != null
                        && !string.IsNullOrWhiteSpace(before.Nick)
                        && BuyerIdentityAliasService.AreEquivalent(runtimeSeller, before.Nick, buyerHint);
                    if (targetActive) break;
                }
                if (targetActive)
                {
                    Log.Info("后台订单补扫已在Bot空闲时切换到目标买家读取订单面板: seller="
                        + runtimeSeller + ", buyer=" + buyerHint);
                }
            }

            if (!targetActive || before == null) return false;
            BuyerIdentityAliasService.Observe(runtimeSeller, before.Nick, before.Display, before.TargetId);
            var verifiedBuyer = BuyerIdentityAliasService.ResolveInternalNick(runtimeSeller, before.Nick);
            if (string.IsNullOrWhiteSpace(verifiedBuyer)) verifiedBuyer = buyerHint;

            string raw;
            try
            {
                raw = await cdp.EvaluateExpressionAsync(
                    VisibleOrderPanelExpression,
                    "后台延迟读取当前买家右侧近3个月订单面板").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Info("后台订单补扫DOM读取失败: seller=" + runtimeSeller
                    + ", buyer=" + verifiedBuyer + ", error=" + ex.Message);
                return false;
            }

            var panelText = ExtractVisibleOrderPanelText(raw);
            if (string.IsNullOrWhiteSpace(panelText)) return false;

            Conversation after;
            try
            {
                var current = await GetCurrentConversationID().ConfigureAwait(false);
                after = current == null ? null : current.Result;
            }
            catch
            {
                return false;
            }
            if (after == null || string.IsNullOrWhiteSpace(after.Nick)
                || !BuyerIdentityAliasService.AreEquivalent(runtimeSeller, after.Nick, verifiedBuyer))
            {
                Log.Info("后台订单补扫已取消：读取期间当前买家变化。seller=" + runtimeSeller
                    + ", expectedBuyer=" + verifiedBuyer
                    + ", currentBuyer=" + (after == null ? string.Empty : after.Nick));
                return false;
            }

            var candidates = ParseVisibleOrderPanelCandidates(panelText)
                .OrderByDescending(x => x.PaidAt ?? x.CreatedAt ?? DateTime.MinValue)
                .Take(3)
                .ToList();
            if (candidates.Count == 0) return false;

            var now = DateTime.Now;
            var freshFloor = (probeStartedAt == DateTime.MinValue ? now : probeStartedAt).AddSeconds(-20);
            var sawFreshSupportedOrder = false;
            var sawFreshUnsupportedOrder = false;
            foreach (var candidate in candidates)
            {
                if (candidate == null || string.IsNullOrWhiteSpace(candidate.OrderId)) continue;
                var eventTime = candidate.PaidAt ?? candidate.CreatedAt;
                if (!eventTime.HasValue) continue;
                if (eventTime.Value > now.AddMinutes(2)) continue;
                if (eventTime.Value < freshFloor) continue;

                if (VisiblePanelUnsupportedStatuses.Any(x => string.Equals(x, candidate.TradeStatus, StringComparison.Ordinal)))
                {
                    sawFreshUnsupportedOrder = true;
                    continue;
                }

                var paid = candidate.PaidAt.HasValue
                    || VisiblePanelPaidStatuses.Any(x => string.Equals(x, candidate.TradeStatus, StringComparison.Ordinal));
                var eventType = paid ? OrderEventType.Paid : OrderEventType.Created;
                var text = new StringBuilder();
                text.Append(paid ? "买家已付款 " : "买家已下单 ")
                    .Append("订单号：").Append(candidate.OrderId);
                if (!string.IsNullOrWhiteSpace(candidate.TradeStatus))
                    text.Append(" 订单状态：").Append(candidate.TradeStatus);
                if (candidate.CreatedAt.HasValue)
                    text.Append(" 下单时间：").Append(candidate.CreatedAt.Value.ToString("yyyy-MM-dd HH:mm:ss"));
                if (candidate.PaidAt.HasValue)
                    text.Append(" 付款时间：").Append(candidate.PaidAt.Value.ToString("yyyy-MM-dd HH:mm:ss"));

                var snapshot = new OrderSnapshot
                {
                    Seller = runtimeSeller,
                    Buyer = verifiedBuyer,
                    OrderId = candidate.OrderId,
                    TradeStatus = candidate.TradeStatus,
                    IsPaid = paid,
                    CreatedAt = candidate.CreatedAt,
                    PaidAt = candidate.PaidAt,
                    Source = "千牛右侧订单面板后台延迟兜底",
                    DetectedAt = now,
                    EventTime = eventTime.Value,
                    EventType = eventType,
                    EventText = text.ToString()
                };

                var publish = OrderEventHub.Publish(snapshot);
                if (publish != null && publish.Detected)
                {
                    sawFreshSupportedOrder = true;
                    Log.Info((publish.Accepted
                        ? "后台订单面板延迟兜底识别并发布"
                        : "后台订单面板延迟兜底订单已由其他通道处理/去重")
                        + ": seller=" + runtimeSeller + ", buyer=" + verifiedBuyer
                        + ", orderId=" + candidate.OrderId + ", event=" + eventType
                        + ", trigger=" + (source ?? string.Empty));
                }
            }

            return sawFreshSupportedOrder || sawFreshUnsupportedOrder;
        }
    }
}
