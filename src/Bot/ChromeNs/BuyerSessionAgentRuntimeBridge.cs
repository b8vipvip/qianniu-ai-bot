using Bot.ChatRecord;
using BotLib;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace Bot.ChromeNs
{
    /// <summary>
    /// Feeds raw QN/CDP events into the shared BuyerSessionAgent before the independent reply/order
    /// pipelines process them. This gives one ordered seller+buyer timeline for text, image, product,
    /// order, withdrawal, system and seller reply events without replacing the existing stable send
    /// pipeline. Human seller replies are observational learning evidence and never cancel Bot work;
    /// explicit hard invalidation remains reserved for withdrawal/session/send safety paths.
    /// </summary>
    internal static class BuyerSessionAgentRuntimeBridge
    {
        private sealed class WatchedSession
        {
            public string Seller { get; set; }
            public string Buyer { get; set; }
            public DateTime LastSeenUtc { get; set; }
        }

        private static readonly Lazy<BuyerSessionAgent> AgentHolder =
            new Lazy<BuyerSessionAgent>(() => new BuyerSessionAgent());
        private static readonly ConcurrentDictionary<QN, byte> Attached = new ConcurrentDictionary<QN, byte>();
        private static readonly ConcurrentDictionary<string, WatchedSession> WatchedSessions =
            new ConcurrentDictionary<string, WatchedSession>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, DateTime> GenerationGeneratingSinceUtc =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private const int AbsoluteGenerationAgeSeconds = 55;
        private const int DeadlineWatchdogSleepMilliseconds = 250;
        private static Timer _timer;
        private static Thread _deadlineWatchdogThread;
        private static int _started;

        private static BuyerSessionAgent Agent
        {
            get { return AgentHolder.Value; }
        }

        public static void EnsureStarted()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            _timer = new Timer(_ => AttachExisting(), null, 300, 700);
            _deadlineWatchdogThread = new Thread(GenerationDeadlineWatchdogLoop)
            {
                IsBackground = true,
                Name = "Qianniu.GenerationDeadlineWatchdog"
            };
            _deadlineWatchdogThread.Start();
            Log.Info("BuyerSessionAgent统一事件桥已启动：原始买家/卖家/订单/撤回/系统消息进入同一seller+buyer时间线；人工回复仅记录用于学习；generation绝对年龄看门狗=55s。" );
        }

        private static void GenerationDeadlineWatchdogLoop()
        {
            while (true)
            {
                try
                {
                    SweepGenerationDeadlines();
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("generation绝对年龄看门狗扫描失败: " + ex.Message, 20);
                }
                Thread.Sleep(DeadlineWatchdogSleepMilliseconds);
            }
        }

        private static void SweepGenerationDeadlines()
        {
            var now = DateTime.UtcNow;
            foreach (var pair in WatchedSessions.ToArray())
            {
                var watched = pair.Value;
                if (watched == null) continue;
                if ((now - watched.LastSeenUtc).TotalHours > 6)
                {
                    WatchedSession removedSession;
                    WatchedSessions.TryRemove(pair.Key, out removedSession);
                    continue;
                }

                var snapshot = Agent.GetSnapshot(watched.Seller, watched.Buyer);
                if (snapshot == null || snapshot.RecentEvents == null) continue;
                foreach (var generation in snapshot.RecentEvents
                    .Where(x => x != null
                        && x.Kind == BuyerSessionEventKind.BuyerActionAccepted
                        && x.Generation > 0)
                    .Select(x => x.Generation)
                    .Distinct()
                    .ToArray())
                {
                    BuyerSessionAgentState state;
                    var watchKey = BuildGenerationWatchKey(watched.Seller, watched.Buyer, generation);
                    if (!Agent.TryGetGenerationState(watched.Seller, watched.Buyer, generation, out state))
                    {
                        DateTime ignored;
                        GenerationGeneratingSinceUtc.TryRemove(watchKey, out ignored);
                        continue;
                    }

                    if (state == BuyerSessionAgentState.Generating)
                    {
                        GenerationGeneratingSinceUtc.TryAdd(watchKey, now);
                    }

                    if (state == BuyerSessionAgentState.Completed
                        || state == BuyerSessionAgentState.Cancelled
                        || state == BuyerSessionAgentState.Failed)
                    {
                        DateTime ignored;
                        GenerationGeneratingSinceUtc.TryRemove(watchKey, out ignored);
                        continue;
                    }

                    DateTime generatingSince;
                    if (!GenerationGeneratingSinceUtc.TryGetValue(watchKey, out generatingSince)) continue;
                    var elapsed = now - generatingSince;
                    if (elapsed.TotalSeconds <= AbsoluteGenerationAgeSeconds) continue;

                    Agent.Cancel(
                        watched.Seller,
                        watched.Buyer,
                        generation,
                        "absolute_generation_age_exceeded");
                    DateTime removed;
                    GenerationGeneratingSinceUtc.TryRemove(watchKey, out removed);
                    Log.ErrorWithMaxCount(
                        "generation超过绝对年龄已由独立线程硬取消，禁止迟到结果进入Ready/Sending: seller="
                        + watched.Seller + ", buyer=" + watched.Buyer
                        + ", generation=" + generation
                        + ", elapsedMs=" + (long)elapsed.TotalMilliseconds
                        + ", limitSeconds=" + AbsoluteGenerationAgeSeconds,
                        100);
                }
            }
        }

        private static void WatchSession(string seller, string buyer)
        {
            seller = Normalize(seller);
            buyer = Normalize(buyer);
            if (seller.Length == 0 || buyer.Length == 0 || string.Equals(seller, buyer, StringComparison.Ordinal)) return;
            var key = seller + "#" + buyer;
            var now = DateTime.UtcNow;
            WatchedSessions.AddOrUpdate(
                key,
                _ => new WatchedSession { Seller = seller, Buyer = buyer, LastSeenUtc = now },
                (_, existing) =>
                {
                    existing = existing ?? new WatchedSession();
                    existing.Seller = seller;
                    existing.Buyer = buyer;
                    existing.LastSeenUtc = now;
                    return existing;
                });
        }

        private static string BuildGenerationWatchKey(string seller, string buyer, long generation)
        {
            return Normalize(seller) + "#" + Normalize(buyer) + "#" + generation;
        }

        private static void AttachExisting()
        {
            QN[] qns;
            try { qns = QN.QNSet == null ? new QN[0] : QN.QNSet.ToArray(); }
            catch { return; }

            foreach (var qn in qns)
            {
                if (qn == null || !Attached.TryAdd(qn, 0)) continue;
                try
                {
                    qn.EvRecieveNewMessage += (sender, e) =>
                    {
                        if (e != null) ObservePayload(qn, e.Message, "foreground");
                    };
                    qn.EvShopRobotReceriveNewMessage += (sender, e) =>
                    {
                        if (e == null || e.Seller == null || e.Buyer == null) return;
                        string nonBuyerReason;
                        if (NonBuyerConversationGuard.ShouldBlockConversation(e.Seller, e.Buyer, out nonBuyerReason))
                        {
                            Log.Info("BuyerSessionAgent忽略非买家后台通知: reason=" + nonBuyerReason);
                            return;
                        }
                        WatchSession(e.Seller.Nick, e.Buyer.Nick);
                        var now = DateTime.Now;
                        Agent.RecordEvent(
                            e.Seller.Nick,
                            e.Buyer.Nick,
                            BuyerSessionEventKind.BuyerSystem,
                            string.Empty,
                            0,
                            now,
                            now,
                            "background:new_message_signal",
                            false);
                    };
                    Log.Info("BuyerSessionAgent已挂载客服实例原始消息流: seller="
                        + (qn.Seller == null ? string.Empty : qn.Seller.Nick));
                }
                catch (Exception ex)
                {
                    byte ignored;
                    Attached.TryRemove(qn, out ignored);
                    Log.ErrorWithMaxCount("BuyerSessionAgent挂载消息流失败: " + ex.Message, 10);
                }
            }
        }

        private static void ObservePayload(QN qn, string payload, string source)
        {
            if (qn == null || string.IsNullOrWhiteSpace(payload)) return;
            try
            {
                var response = JsonConvert.DeserializeObject<ChatResponse>(payload);
                if (response == null || response.result == null) return;
                foreach (var message in response.result.Where(x => x != null).OrderBy(IncomingMessageSafety.GetSortValue))
                {
                    ObserveMessage(qn, message, source);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("BuyerSessionAgent解析原始消息事件失败: " + ex.Message, 20);
            }
        }

        private static void ObserveMessage(QN qn, QNChatMessage message, string source)
        {
            if (qn == null || message == null || qn.Seller == null) return;
            var seller = Normalize(qn.Seller.Nick);
            var from = Normalize(message.fromid == null ? string.Empty : message.fromid.nick);
            var to = Normalize(message.toid == null ? string.Empty : message.toid.nick);
            if (seller.Length == 0 || from.Length == 0 || to.Length == 0) return;

            var sellerMessage = string.Equals(from, seller, StringComparison.Ordinal);
            var text = GetMessageText(message);
            string nonBuyerReason;
            if (!sellerMessage && NonBuyerConversationGuard.ShouldBlockMessage(message, seller, text, out nonBuyerReason))
            {
                Log.Info("BuyerSessionAgent忽略非买家原始消息，禁止污染学习时间线: reason=" + nonBuyerReason);
                return;
            }
            var buyer = sellerMessage ? to : from;
            if (buyer.Length == 0 || string.Equals(buyer, seller, StringComparison.Ordinal)) return;
            WatchSession(seller, buyer);

            var display = IncomingMessageSafety.GetDisplayText(message, text);
            var key = IncomingMessageSafety.BuildMessageKey(message, text);
            var sort = IncomingMessageSafety.GetSortValue(message);
            DateTime sourceTime;
            if (!OrderCardParser.TryGetMessageTime(message, out sourceTime)) sourceTime = DateTime.Now;
            var now = DateTime.Now;

            if (sellerMessage)
            {
                if (ConversationContextStore.IsWithdrawalNotice(message, text))
                {
                    Agent.RecordEvent(seller, buyer, BuyerSessionEventKind.SellerWithdrawal, key, sort,
                        sourceTime, now, source + ":seller_withdrawal", false);
                    return;
                }

                var botEcho = IsRecentBotEcho(qn, text);
                var result = Agent.RecordEvent(
                    seller,
                    buyer,
                    botEcho ? BuyerSessionEventKind.SellerBotEcho : BuyerSessionEventKind.SellerHumanReply,
                    key,
                    sort,
                    sourceTime,
                    now,
                    source + (botEcho ? ":bot_echo" : ":human_reply"),
                    false);

                if (!botEcho)
                {
                    Log.Info("BuyerSessionAgent记录人工客服回复作为学习证据，未取消Bot generation: seller="
                        + seller + ", buyer=" + buyer + ", stale=" + result.StaleAgainstLatestBuyer
                        + ", sort=" + sort);
                }
                return;
            }

            OrderSnapshot order;
            if (OrderCardParser.TryParse(message, text, seller, buyer, "BuyerSessionAgent:" + source, out order))
            {
                Agent.RecordEvent(seller, buyer, MapOrderKind(order.EventType), key, sort,
                    order.EventTime, now, source + ":order:" + order.EventType, false);
                return;
            }

            var kind = ClassifyBuyerEvent(message, text, display);
            Agent.RecordEvent(seller, buyer, kind, key, sort, sourceTime, now, source + ":raw_buyer_event", false);
        }

        private static BuyerSessionEventKind ClassifyBuyerEvent(QNChatMessage message, string text, string display)
        {
            if (ConversationContextStore.IsWithdrawalNotice(message, text)) return BuyerSessionEventKind.BuyerWithdrawal;
            if (ConversationContextStore.IsPlatformSystemTip(message, text)) return BuyerSessionEventKind.BuyerSystem;
            if (ConversationContextStore.IsProductLink(message, text)) return BuyerSessionEventKind.BuyerProductCard;
            if (string.Equals(display, "[图片]", StringComparison.Ordinal)) return BuyerSessionEventKind.BuyerImage;
            if (IncomingMessageSafety.IsMediaPlaceholder(display)) return BuyerSessionEventKind.BuyerMedia;
            return BuyerSessionEventKind.BuyerText;
        }

        private static BuyerSessionEventKind MapOrderKind(OrderEventType eventType)
        {
            if (eventType == OrderEventType.Paid) return BuyerSessionEventKind.OrderPaid;
            if (eventType == OrderEventType.Closed) return BuyerSessionEventKind.OrderClosed;
            if (eventType == OrderEventType.RefundRequested) return BuyerSessionEventKind.OrderRefund;
            return BuyerSessionEventKind.OrderCreated;
        }

        private static bool IsRecentBotEcho(QN qn, string text)
        {
            try
            {
                if (qn.Rpa == null || string.IsNullOrWhiteSpace(qn.Rpa.LastSetPlainText)) return false;
                if ((DateTime.Now - qn.Rpa.LatestSetTextTime).TotalSeconds > 45) return false;
                var expected = NormalizeReplyText(qn.Rpa.LastSetPlainText);
                var actual = NormalizeReplyText(text);
                return expected.Length > 0 && actual.Length > 0
                    && (string.Equals(expected, actual, StringComparison.Ordinal)
                        || actual.Contains(expected)
                        || expected.Contains(actual));
            }
            catch { return false; }
        }

        private static string GetMessageText(QNChatMessage message)
        {
            if (message == null) return string.Empty;
            try
            {
                if (message.originalData != null)
                {
                    var text = message.originalData.text ?? string.Empty;
                    if (message.originalData.header != null)
                    {
                        text += message.originalData.header.summary ?? string.Empty;
                    }
                    if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
                }
            }
            catch
            {
            }
            return (message.summary ?? string.Empty).Trim();
        }

        private static string NormalizeReplyText(string value)
        {
            value = Normalize(value);
            value = value.Replace("[AI]", string.Empty)
                .Replace("【AI】", string.Empty)
                .Replace("[本地知识库]", string.Empty)
                .Replace("【本地知识库】", string.Empty)
                .Trim();
            return value;
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim();
        }
    }
}