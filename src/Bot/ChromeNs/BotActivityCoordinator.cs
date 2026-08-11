using Bot.ChatRecord;
using Bot.Options;
using Bot.ShopScope;
using BotLib;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal sealed class BotActivityLease : IDisposable
    {
        private readonly long _id;
        private int _disposed;

        internal BotActivityLease(long id)
        {
            _id = id;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            BotActivityCoordinator.End(_id);
        }
    }

    internal sealed class BotActivitySnapshot
    {
        public int ActiveCount { get; set; }
        public DateTime LastHumanInteractionAt { get; set; }
        public string LastHumanInteractionReason { get; set; }
        public string BusyReason { get; set; }
    }

    internal static class BotActivityCoordinator
    {
        private sealed class ActivityRecord
        {
            public long Id;
            public string Kind;
            public string Seller;
            public string Buyer;
            public DateTime StartedAt;
        }

        private sealed class HumanRecord
        {
            public DateTime At;
            public string Reason;
        }

        private static long _nextId;
        private static readonly ConcurrentDictionary<long, ActivityRecord> Activities =
            new ConcurrentDictionary<long, ActivityRecord>();
        private static readonly ConcurrentDictionary<string, HumanRecord> HumanInteractions =
            new ConcurrentDictionary<string, HumanRecord>(StringComparer.OrdinalIgnoreCase);

        public static BotActivityLease Begin(string kind, string seller, string buyer)
        {
            var id = Interlocked.Increment(ref _nextId);
            Activities[id] = new ActivityRecord
            {
                Id = id,
                Kind = (kind ?? string.Empty).Trim(),
                Seller = Normalize(seller),
                Buyer = Normalize(buyer),
                StartedAt = DateTime.Now
            };
            return new BotActivityLease(id);
        }

        internal static void End(long id)
        {
            ActivityRecord ignored;
            Activities.TryRemove(id, out ignored);
        }

        public static void MarkHumanInteraction(string seller, string reason)
        {
            seller = Normalize(seller);
            if (seller.Length == 0) return;
            HumanInteractions[seller] = new HumanRecord
            {
                At = DateTime.Now,
                Reason = (reason ?? string.Empty).Trim()
            };
            Log.Info("已记录人工操作保护: seller=" + seller + ", reason=" + (reason ?? string.Empty));
        }

        public static bool IsSafeToAutoFocus(string seller, out string reason)
        {
            seller = Normalize(seller);
            var active = Activities.Values
                .Where(x => x != null && (x.Seller.Length == 0 || x.Seller == seller))
                .OrderBy(x => x.StartedAt)
                .ToList();
            if (active.Count > 0)
            {
                reason = "Bot当前有任务：" + string.Join("、", active.Select(x => x.Kind).Distinct().Take(3));
                return false;
            }

            HumanRecord human;
            var protectSeconds = OrderAttentionSettings.GetHumanProtectionSeconds();
            if (HumanInteractions.TryGetValue(seller, out human)
                && human != null
                && DateTime.Now - human.At < TimeSpan.FromSeconds(protectSeconds))
            {
                var remain = Math.Max(1, protectSeconds - (int)(DateTime.Now - human.At).TotalSeconds);
                reason = "人工操作保护中（约" + remain + "秒）：" + human.Reason;
                return false;
            }

            reason = string.Empty;
            return true;
        }

        public static BotActivitySnapshot GetSnapshot(string seller)
        {
            seller = Normalize(seller);
            HumanRecord human;
            HumanInteractions.TryGetValue(seller, out human);
            var active = Activities.Values
                .Where(x => x != null && (x.Seller.Length == 0 || x.Seller == seller))
                .OrderBy(x => x.StartedAt)
                .ToList();
            return new BotActivitySnapshot
            {
                ActiveCount = active.Count,
                LastHumanInteractionAt = human == null ? DateTime.MinValue : human.At,
                LastHumanInteractionReason = human == null ? string.Empty : human.Reason,
                BusyReason = active.Count == 0
                    ? string.Empty
                    : string.Join("、", active.Select(x => x.Kind).Distinct().Take(3))
            };
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim().ToLowerInvariant();
        }
    }
}

namespace Bot
{
    public partial class App
    {
        // OrderEventHub is the authoritative order-recognition boundary. Some Qianniu builds can
        // publish a confirmed paid/created order (and therefore show the right-side order card)
        // without the raw chat-card path ever creating OrderPlacedReplyPlan. Keep a small runtime
        // fallback attached to the hub's persisted state so a confirmed order cannot stop at UI only.
        private readonly object _orderEventAutoReplyFallbackBootstrap =
            ChromeNs.OrderEventAutoReplyFallback.InitializeForApp();
    }
}

namespace Bot.ChromeNs
{
    /// <summary>
    /// Fallback consumer for newly accepted OrderEventHub events.
    ///
    /// Normal receiveNewMsg/messageCenterNotify paths still keep priority. This consumer waits for
    /// any existing order pipeline to finish, then reuses OrderGuidanceDeliveryGuard and the exact
    /// same ProcessOrderPlacedReplyAsync safe-send path. If the normal path already delivered, the
    /// guard suppresses this fallback. If only the order/attention UI was produced, this path sends
    /// the configured fixed preset/HTTP reply once.
    /// </summary>
    internal static class OrderEventAutoReplyFallback
    {
        private static readonly DateTime StartedAt = DateTime.Now;
        private static readonly ConcurrentDictionary<string, DateTime> Scheduled =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private static Timer _timer;
        private static int _initialized;
        private static int _tickRunning;
        private static string _lastFileStamp = string.Empty;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                _timer = new Timer(_ => Tick(), null, 1200, 600);
                Log.Info("订单事件Hub自动回复兜底已启动：已确认订单不会只停留在右侧待处理卡片。");
            }
            return new object();
        }

        private static void Tick()
        {
            if (Interlocked.Exchange(ref _tickRunning, 1) != 0) return;
            try
            {
                var path = GetOrderEventStatePath();
                if (!File.Exists(path)) return;

                var info = new FileInfo(path);
                var stamp = info.LastWriteTimeUtc.Ticks + "#" + info.Length;
                if (string.Equals(stamp, _lastFileStamp, StringComparison.Ordinal)) return;
                _lastFileStamp = stamp;

                JObject root;
                try
                {
                    root = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                }
                catch (IOException)
                {
                    // OrderEventHub writes atomically through a temp file. A read that races the
                    // replace is harmless; the next 600ms tick will retry.
                    return;
                }

                var events = root["Events"] as JArray;
                if (events == null || events.Count == 0) return;
                CleanupScheduled();

                foreach (var item in events.OfType<JObject>())
                {
                    DateTime seenAt;
                    if (!TryReadLocalDateTime(item["SeenAt"], out seenAt)) continue;
                    if (seenAt < StartedAt.AddSeconds(-2)) continue;

                    OrderSnapshot snapshot;
                    try
                    {
                        snapshot = item["Snapshot"] == null
                            ? null
                            : item["Snapshot"].ToObject<OrderSnapshot>();
                    }
                    catch
                    {
                        continue;
                    }
                    if (snapshot == null
                        || string.IsNullOrWhiteSpace(snapshot.Seller)
                        || string.IsNullOrWhiteSpace(snapshot.Buyer)
                        || string.IsNullOrWhiteSpace(snapshot.OrderId)) continue;
                    if (snapshot.EventType != OrderEventType.Created
                        && snapshot.EventType != OrderEventType.Paid) continue;

                    var key = BuildKey(snapshot);
                    if (!Scheduled.TryAdd(key, DateTime.Now)) continue;
                    Task.Run(async () => await HandleAsync(snapshot, seenAt, key));
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("订单事件Hub自动回复兜底检查失败：" + ex.Message, 10);
            }
            finally
            {
                Interlocked.Exchange(ref _tickRunning, 0);
            }
        }

        private static async Task HandleAsync(OrderSnapshot snapshot, DateTime seenAt, string key)
        {
            try
            {
                // Give the normal raw-message order pipeline first ownership. In the healthy case it
                // should enter “下单自动回复” almost immediately and this fallback will only verify
                // that delivery has already happened.
                await Task.Delay(1200);

                QN qn = null;
                for (var attempt = 0; attempt < 20 && qn == null; attempt++)
                {
                    qn = ResolveQn(snapshot.Seller);
                    if (qn == null) await Task.Delay(500);
                }
                if (qn == null)
                {
                    DateTime ignored;
                    Scheduled.TryRemove(key, out ignored);
                    Log.Info("[订单自动回复] Hub兜底暂未找到对应千牛实例，等待后续订单事件重试: seller="
                        + snapshot.Seller + ", orderId=" + snapshot.OrderId);
                    return;
                }

                // Never race an already-running order reply/enrichment path. Once that path exits,
                // OrderGuidanceDeliveryGuard will tell the fallback whether it already delivered.
                for (var attempt = 0; attempt < 60; attempt++)
                {
                    var activity = BotActivityCoordinator.GetSnapshot(snapshot.Seller);
                    if (!IsExistingOrderPipelineBusy(activity == null ? string.Empty : activity.BusyReason)) break;
                    if (attempt == 59)
                    {
                        DateTime ignored;
                        Scheduled.TryRemove(key, out ignored);
                        Log.Info("[订单自动回复] Hub兜底等待现有订单发送链路超过30秒，未并发抢发: seller="
                            + snapshot.Seller + ", orderId=" + snapshot.OrderId
                            + ", busy=" + (activity == null ? string.Empty : activity.BusyReason));
                        return;
                    }
                    await Task.Delay(500);
                }

                ShopContext shop;
                try
                {
                    shop = ShopContextLocator.ResolveBySellerNick(snapshot.Seller);
                }
                catch (Exception ex)
                {
                    DateTime ignored;
                    Scheduled.TryRemove(key, out ignored);
                    Log.Info("[订单自动回复] Hub兜底无法解析店铺作用域，拒绝跨店猜测: seller="
                        + snapshot.Seller + ", orderId=" + snapshot.OrderId + ", error=" + ex.Message);
                    return;
                }
                if (shop == null)
                {
                    DateTime ignored;
                    Scheduled.TryRemove(key, out ignored);
                    Log.Info("[订单自动回复] Hub兜底缺少店铺作用域，未发送: seller="
                        + snapshot.Seller + ", orderId=" + snapshot.OrderId);
                    return;
                }

                using (ShopSettingsScope.Enter(shop))
                {
                    await qn.ProcessAcceptedOrderEventFallbackAsync(snapshot, seenAt);
                }
            }
            catch (Exception ex)
            {
                DateTime ignored;
                Scheduled.TryRemove(key, out ignored);
                Log.ErrorWithMaxCount("订单事件Hub自动回复兜底执行失败：" + ex.Message, 10);
            }
        }

        private static QN ResolveQn(string seller)
        {
            var exact = QN.FindExistingBySellerNick(seller);
            if (exact != null) return exact;
            try
            {
                return QN.GetRuntimeSafetySnapshot()
                    .FirstOrDefault(x => x != null
                        && x.Seller != null
                        && DirectOrderIdentityResolver.IdentityEquals(x.Seller.Nick, seller));
            }
            catch
            {
                return null;
            }
        }

        private static bool IsExistingOrderPipelineBusy(string busyReason)
        {
            busyReason = busyReason ?? string.Empty;
            return busyReason.Contains("下单自动回复")
                || busyReason.Contains("订单模板")
                || busyReason.Contains("订单交易")
                || busyReason.Contains("下单交易");
        }

        private static bool TryReadLocalDateTime(JToken token, out DateTime value)
        {
            value = DateTime.MinValue;
            if (token == null) return false;
            if (!DateTime.TryParse(token.ToString(), out value)) return false;
            if (value.Kind == DateTimeKind.Utc) value = value.ToLocalTime();
            return true;
        }

        private static string GetOrderEventStatePath()
        {
            // Keep this exactly aligned with OrderEventHub.GetPath().
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QianniuAiBot",
                "data",
                "order-event-state.json");
        }

        private static string BuildKey(OrderSnapshot snapshot)
        {
            return (snapshot.Seller ?? string.Empty).Trim().ToLowerInvariant()
                + "#" + (snapshot.OrderId ?? string.Empty).Trim()
                + "#" + snapshot.EventType;
        }

        private static void CleanupScheduled()
        {
            var cutoff = DateTime.Now.AddHours(-24);
            foreach (var pair in Scheduled)
            {
                if (pair.Value >= cutoff) continue;
                DateTime ignored;
                Scheduled.TryRemove(pair.Key, out ignored);
            }
        }
    }

    public partial class QN
    {
        /// <summary>
        /// Consume an order that has already been accepted by OrderEventHub, without publishing it
        /// a second time. This is intentionally separate from TryCreatePlan, whose Publish call would
        /// correctly deduplicate the event and therefore never create a plan for this fallback.
        /// </summary>
        internal async Task ProcessAcceptedOrderEventFallbackAsync(OrderSnapshot snapshot, DateTime hubSeenAt)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.OrderId)) return;
            if (snapshot.EventType != OrderEventType.Created && snapshot.EventType != OrderEventType.Paid) return;

            var runtimeSeller = Seller == null ? string.Empty : (Seller.Nick ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(runtimeSeller)
                || !DirectOrderIdentityResolver.IdentityEquals(runtimeSeller, snapshot.Seller))
            {
                Log.Info("[订单自动回复] Hub兜底卖家身份不匹配，未发送: snapshotSeller="
                    + snapshot.Seller + ", runtimeSeller=" + runtimeSeller + ", orderId=" + snapshot.OrderId);
                return;
            }
            snapshot.Seller = runtimeSeller;

            var resolvedBuyer = BuyerIdentityAliasService.ResolveInternalNick(snapshot.Seller, snapshot.Buyer);
            if (!string.IsNullOrWhiteSpace(resolvedBuyer)) snapshot.Buyer = resolvedBuyer;
            if (string.IsNullOrWhiteSpace(snapshot.Buyer))
            {
                Log.Info("[订单自动回复] Hub兜底缺少可验证买家身份，未发送: orderId=" + snapshot.OrderId);
                return;
            }

            var eventTime = snapshot.EventTime == DateTime.MinValue ? hubSeenAt : snapshot.EventTime;
            if (eventTime.Kind == DateTimeKind.Utc) eventTime = eventTime.ToLocalTime();
            snapshot.EventTime = eventTime;
            if (eventTime < _messageSafetyStartedAt.AddSeconds(-8))
            {
                Log.Info("[订单自动回复] Hub兜底跳过历史订单: orderId=" + snapshot.OrderId
                    + ", eventTime=" + eventTime.ToString("yyyy-MM-dd HH:mm:ss")
                    + ", botStartedAt=" + _messageSafetyStartedAt.ToString("yyyy-MM-dd HH:mm:ss"));
                return;
            }

            if (!Params.Robot.CanUseRobotReal)
            {
                Log.Info("[订单自动回复] 未发送原因=Bot已停用, orderId=" + snapshot.OrderId
                    + ", buyer=" + snapshot.Buyer);
                return;
            }

            var cfg = BotFeatureStore.GetAutoReplyRules();
            if (cfg == null || !cfg.EnableOrderPlacedReply)
            {
                Log.Info("[订单自动回复] 未发送原因=本店下单自动发送关闭, orderId=" + snapshot.OrderId
                    + ", buyer=" + snapshot.Buyer);
                return;
            }

            if (string.IsNullOrWhiteSpace(snapshot.Source)) snapshot.Source = "OrderEventHub统一兜底";
            if (string.IsNullOrWhiteSpace(snapshot.EventText))
            {
                snapshot.EventText = (snapshot.EventType == OrderEventType.Paid ? "买家已付款 " : "买家已下单 ")
                    + "订单号：" + snapshot.OrderId
                    + " 订单状态：" + (snapshot.EventType == OrderEventType.Paid ? "已付款" : "新下单");
            }

            OrderGuidanceDeliveryGuard.ObserveOrder(snapshot);
            EnqueueNewOrderAttention(snapshot);

            var plan = new OrderPlacedReplyPlan
            {
                Seller = snapshot.Seller,
                Buyer = snapshot.Buyer,
                OrderId = snapshot.OrderId,
                EventText = snapshot.EventText,
                EventTime = snapshot.EventTime,
                ReservationKey = BuildOrderEventFallbackReservationKey(snapshot.Seller, snapshot.Buyer, snapshot.OrderId),
                Config = cfg,
                Snapshot = snapshot,
                IsBuyerFollowUp = false,
                TriggerText = string.Empty,
                TriggerTime = DateTime.MinValue
            };

            Log.Info("[订单自动回复] Hub兜底接管已确认订单: seller=" + plan.Seller
                + ", buyer=" + plan.Buyer + ", orderId=" + plan.OrderId
                + ", event=" + snapshot.EventType
                + ", source=" + snapshot.Source
                + ", hubSeenAt=" + hubSeenAt.ToString("yyyy-MM-dd HH:mm:ss.fff")
                + ", mode=" + (cfg.OrderPlacedReplyMode ?? string.Empty)
                + ", autoReply=" + Params.Robot.GetIsAutoReply());

            // Keep the existing trade-detail enrichment behavior for templates that request SKU,
            // quantity or payment fields. Static fixed presets pass through immediately.
            if (OrderTemplateRequiredFieldsV2.TryOwnExistingPlan(this, plan, "OrderEventHub统一兜底"))
            {
                Log.Info("[订单自动回复] Hub兜底已交给订单模板字段补全V2: orderId=" + plan.OrderId);
                return;
            }

            await ProcessOrderPlacedReplyAsync(plan);
        }

        private static string BuildOrderEventFallbackReservationKey(string seller, string buyer, string orderId)
        {
            return Regex.Replace((seller ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", string.Empty)
                + "#" + Regex.Replace((buyer ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", string.Empty)
                + "#" + (orderId ?? string.Empty).Trim();
        }
    }
}
