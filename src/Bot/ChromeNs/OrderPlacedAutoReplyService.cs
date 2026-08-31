using Bot.Automation.ChatDeskNs;
using Bot.ChatRecord;
using Bot.Options;
using BotLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal sealed class OrderPlacedReplyPlan
    {
        public string Seller { get; set; }
        public string Buyer { get; set; }
        public string OrderId { get; set; }
        public string EventText { get; set; }
        public DateTime EventTime { get; set; }
        public string ReservationKey { get; set; }
        public AutoReplyRuleConfig Config { get; set; }
        public OrderSnapshot Snapshot { get; set; }
        public bool IsBuyerFollowUp { get; set; }
        public string TriggerText { get; set; }
        public DateTime TriggerTime { get; set; }
    }

    internal sealed class OrderPlacedReplyResolution
    {
        public bool Success { get; set; }
        public string Reply { get; set; }
        public string Source { get; set; }
        public string Error { get; set; }
    }

    internal static class OrderPlacedAutoReplyService
    {
        private sealed class OrderReplyActionRecord
        {
            public string Seller { get; set; }
            public string Buyer { get; set; }
            public string OrderId { get; set; }
            public bool FollowUp { get; set; }
            public DateTime Until { get; set; }
            public bool Delivered { get; set; }
            public bool DeliveryUncertain { get; set; }
        }

        private sealed class OrderReplyActionState
        {
            public List<OrderReplyActionRecord> Records { get; set; }

            public OrderReplyActionState()
            {
                Records = new List<OrderReplyActionRecord>();
            }
        }

        private static readonly ConcurrentDictionary<string, DateTime> Reservations =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private static readonly object ActionSync = new object();
        private static readonly List<OrderReplyActionRecord> ActiveActions = new List<OrderReplyActionRecord>();
        private static OrderReplyActionState _actionState;

        public static bool TryCreatePlan(
            QNChatMessage message,
            string messageText,
            string seller,
            string buyer,
            DateTime botStartedAt,
            out OrderPlacedReplyPlan plan)
        {
            plan = null;
            if (!Params.Robot.CanUseRobotReal) return false;

            string classificationReason;
            if (!OrderMessageClassifier.IsConfirmedOrderEvent(message, messageText, out classificationReason))
            {
                return TryCreateBuyerFollowUpPlan(messageText, seller, buyer, out plan);
            }

            OrderSnapshot snapshot;
            if (!OrderCardParser.TryParse(
                message,
                messageText,
                seller,
                buyer,
                "千牛消息/远端历史订单卡片",
                out snapshot))
            {
                return false;
            }

            ObserveCanonicalOrderId(seller, buyer, snapshot.OrderId);
            OrderGuidanceDeliveryGuard.ObserveOrder(snapshot);
            Log.Info("订单事件通过严格证据校验: seller=" + seller
                + ", buyer=" + buyer + ", orderId=" + snapshot.OrderId
                + ", reason=" + classificationReason);

            if (snapshot.EventTime < botStartedAt.AddSeconds(-8))
            {
                Log.Info("订单事件已跳过历史卡片: orderId=" + snapshot.OrderId
                    + ", eventTime=" + snapshot.EventTime.ToString("yyyy-MM-dd HH:mm:ss"));
                return true;
            }

            var published = OrderEventHub.Publish(snapshot);
            if (!published.Accepted)
            {
                return true;
            }

            if (snapshot.EventType == OrderEventType.Created || snapshot.EventType == OrderEventType.Paid)
            {
                var qn = QN.FindExistingBySellerNick(seller);
                if (qn != null)
                {
                    qn.EnqueueNewOrderAttention(snapshot);
                }
            }

            var cfg = BotFeatureStore.GetAutoReplyRules();
            if (cfg == null || !cfg.EnableOrderPlacedReply) return true;
            if (snapshot.EventType != OrderEventType.Created && snapshot.EventType != OrderEventType.Paid) return true;

            var key = BuildReservationKey(seller, buyer, snapshot.OrderId, false);
            var now = DateTime.Now;
            DateTime until;
            if (Reservations.TryGetValue(key, out until) && until > now)
            {
                Log.Info("下单自动消息已去重: orderId=" + snapshot.OrderId + ", buyer=" + buyer);
                return true;
            }

            var reserveMinutes = Math.Max(2, Math.Min(30, cfg.OrderPlacedApiTimeoutSeconds <= 0 ? 2 : cfg.OrderPlacedApiTimeoutSeconds / 2 + 2));
            Reservations[key] = now.AddMinutes(reserveMinutes);
            plan = new OrderPlacedReplyPlan
            {
                Seller = (seller ?? string.Empty).Trim(),
                Buyer = (buyer ?? string.Empty).Trim(),
                OrderId = snapshot.OrderId,
                EventText = snapshot.EventText,
                EventTime = snapshot.EventTime,
                ReservationKey = key,
                Config = cfg,
                Snapshot = snapshot,
                IsBuyerFollowUp = false,
                TriggerText = string.Empty,
                TriggerTime = DateTime.MinValue
            };
            Log.Info("下单自动回复规则已建立强制发送计划: seller=" + seller
                + ", buyer=" + buyer + ", orderId=" + snapshot.OrderId
                + ", manualReplyDoesNotSuppress=true");
            return true;
        }

        private static bool TryCreateBuyerFollowUpPlan(
            string messageText,
            string seller,
            string buyer,
            out OrderPlacedReplyPlan plan)
        {
            plan = null;
            var cfg = BotFeatureStore.GetAutoReplyRules();
            if (cfg == null || !cfg.EnableOrderPlacedReply) return false;

            OrderSnapshot snapshot;
            string reason;
            if (!OrderGuidanceDeliveryGuard.CanCreateFollowUp(seller, buyer, messageText, out snapshot, out reason))
                return false;

            ObserveCanonicalOrderId(seller, buyer, snapshot.OrderId);
            var trigger = (messageText ?? string.Empty).Trim();
            var key = BuildReservationKey(seller, buyer, snapshot.OrderId, true);
            DateTime until;
            if (Reservations.TryGetValue(key, out until) && until > DateTime.Now)
            {
                Log.Info("买家充值流程续问已去重: buyer=" + buyer + ", orderId=" + snapshot.OrderId);
                return true;
            }
            Reservations[key] = DateTime.Now.AddMinutes(5);
            plan = new OrderPlacedReplyPlan
            {
                Seller = (seller ?? string.Empty).Trim(), Buyer = (buyer ?? string.Empty).Trim(),
                OrderId = snapshot.OrderId, EventText = trigger, EventTime = snapshot.EventTime,
                ReservationKey = key, Config = cfg, Snapshot = snapshot, IsBuyerFollowUp = true,
                TriggerText = trigger, TriggerTime = DateTime.Now
            };
            Log.Info("买家明确询问充值流程，允许额外补发一次: seller=" + seller
                + ", buyer=" + buyer + ", orderId=" + snapshot.OrderId + ", trigger=" + trigger);
            return true;
        }

        private static List<string> MissingTemplateFields(string template, OrderPlacedReplyPlan plan)
        {
            var missing = new List<string>();
            var snapshot = plan == null ? null : plan.Snapshot;
            template = template ?? string.Empty;
            if (template.Contains("{客服}") && (plan == null || string.IsNullOrWhiteSpace(plan.Seller))) missing.Add("seller");
            if (template.Contains("{买家}") && (plan == null || string.IsNullOrWhiteSpace(plan.Buyer))) missing.Add("buyer");
            if (template.Contains("{订单号}") && (plan == null || string.IsNullOrWhiteSpace(plan.OrderId))) missing.Add("order_id");
            if (template.Contains("{时间}") && (plan == null || plan.EventTime == DateTime.MinValue)) missing.Add("event_time");
            if ((template.Contains("{sku}") || template.Contains("{规格}")) && (snapshot == null || string.IsNullOrWhiteSpace(snapshot.SkuText))) missing.Add("sku");
            if (template.Contains("{买家备注}") && (snapshot == null || string.IsNullOrWhiteSpace(snapshot.BuyerRemark))) missing.Add("buyer_remark");
            if (template.Contains("{数量}") && (snapshot == null || snapshot.Quantity <= 0)) missing.Add("quantity");
            if (template.Contains("{金额}") && (snapshot == null || !snapshot.TotalAmount.HasValue)) missing.Add("total");
            if (template.Contains("{实付}") && (snapshot == null || !snapshot.PaidAmount.HasValue)) missing.Add("paid");
            if (template.Contains("{商品}") && (snapshot == null || string.IsNullOrWhiteSpace(snapshot.ItemTitle))) missing.Add("item");
            if (template.Contains("{订单状态}") && (snapshot == null || string.IsNullOrWhiteSpace(snapshot.TradeStatus))) missing.Add("status");
            return missing;
        }

        private static List<string> PresentTemplateFields(string template, OrderPlacedReplyPlan plan)
        {
            var present = new List<string>();
            var snapshot = plan == null ? null : plan.Snapshot;
            template = template ?? string.Empty;
            if (template.Contains("{客服}") && plan != null && !string.IsNullOrWhiteSpace(plan.Seller)) present.Add("seller");
            if (template.Contains("{买家}") && plan != null && !string.IsNullOrWhiteSpace(plan.Buyer)) present.Add("buyer");
            if (template.Contains("{订单号}") && plan != null && !string.IsNullOrWhiteSpace(plan.OrderId)) present.Add("order_id");
            if (template.Contains("{时间}") && plan != null && plan.EventTime != DateTime.MinValue) present.Add("event_time");
            if ((template.Contains("{sku}") || template.Contains("{规格}")) && snapshot != null && !string.IsNullOrWhiteSpace(snapshot.SkuText)) present.Add("sku");
            if (template.Contains("{买家备注}") && snapshot != null && !string.IsNullOrWhiteSpace(snapshot.BuyerRemark)) present.Add("buyer_remark");
            if (template.Contains("{数量}") && snapshot != null && snapshot.Quantity > 0) present.Add("quantity");
            if (template.Contains("{金额}") && snapshot != null && snapshot.TotalAmount.HasValue) present.Add("total");
            if (template.Contains("{实付}") && snapshot != null && snapshot.PaidAmount.HasValue) present.Add("paid");
            if (template.Contains("{商品}") && snapshot != null && !string.IsNullOrWhiteSpace(snapshot.ItemTitle)) present.Add("item");
            if (template.Contains("{订单状态}") && snapshot != null && !string.IsNullOrWhiteSpace(snapshot.TradeStatus)) present.Add("status");
            return present;
        }

        private static List<string> BuildRenderMissingReasons(IList<string> missing, OrderPlacedReplyPlan plan)
        {
            var reasons = new List<string>();
            var snapshot = plan == null ? null : plan.Snapshot;
            foreach (var field in missing ?? new List<string>())
            {
                string reason;
                if (plan == null) reason = "plan_null";
                else if (snapshot == null && field != "seller" && field != "buyer" && field != "order_id" && field != "event_time") reason = "snapshot_null";
                else
                {
                    switch (field)
                    {
                        case "seller": reason = "seller_empty"; break;
                        case "buyer": reason = "buyer_empty"; break;
                        case "order_id": reason = "order_id_empty"; break;
                        case "event_time": reason = "event_time_min_value"; break;
                        case "sku": reason = "snapshot_sku_empty"; break;
                        case "buyer_remark": reason = "snapshot_buyer_remark_empty"; break;
                        case "quantity": reason = "snapshot_quantity_zero"; break;
                        case "total": reason = "snapshot_total_amount_null"; break;
                        case "paid": reason = "snapshot_paid_amount_null"; break;
                        case "item": reason = "snapshot_item_title_empty"; break;
                        case "status": reason = "snapshot_trade_status_empty"; break;
                        default: reason = "field_unavailable"; break;
                    }
                }
                reasons.Add(field + ":" + reason);
            }
            return reasons;
        }

        public static async Task<OrderPlacedReplyResolution> ResolveAsync(OrderPlacedReplyPlan plan)
        {
            if (plan == null || plan.Config == null) return Fail("下单自动回复计划为空");
            var cfg = plan.Config;
            var mode = string.IsNullOrWhiteSpace(cfg.OrderPlacedReplyMode) ? "固定预设答案" : cfg.OrderPlacedReplyMode.Trim();
            if (string.Equals(mode, "调用HTTP接口", StringComparison.Ordinal))
            {
                var api = await CallReplyApiAsync(plan);
                if (api.Success)
                {
                    if (plan.IsBuyerFollowUp) api.Source += "（买家明确续问）";
                    return api;
                }
                var fallback = RenderTemplate(cfg.OrderPlacedReplyText, plan, "http-fallback");
                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    Log.Info("下单回复接口失败，使用固定预设兜底: orderId=" + plan.OrderId + ", error=" + api.Error);
                    return new OrderPlacedReplyResolution { Success = true, Reply = fallback, Source = plan.IsBuyerFollowUp ? "下单自动回复-接口失败兜底（买家明确续问）" : "下单自动回复-接口失败兜底" };
                }
                return api;
            }
            var reply = RenderTemplate(cfg.OrderPlacedReplyText, plan, "fixed-preset");
            if (string.IsNullOrWhiteSpace(reply)) return Fail("下单固定预设答案为空");
            return new OrderPlacedReplyResolution { Success = true, Reply = reply, Source = plan.IsBuyerFollowUp ? "下单自动回复-固定预设（买家明确续问）" : "下单自动回复-固定预设" };
        }

        public static void Complete(OrderPlacedReplyPlan plan, bool delivered)
        {
            if (plan == null || string.IsNullOrWhiteSpace(plan.ReservationKey)) return;
            if (!delivered)
            {
                DateTime ignored;
                Reservations.TryRemove(plan.ReservationKey, out ignored);
                return;
            }
            var hours = plan.IsBuyerFollowUp ? 720 : (plan.Config == null ? 24 : Math.Max(1, Math.Min(720, plan.Config.OrderPlacedDedupHours)));
            Reservations[plan.ReservationKey] = DateTime.Now.AddHours(hours);
        }

        internal static bool TryBeginExecution(OrderPlacedReplyPlan plan, out string reason)
        {
            reason = string.Empty;
            if (plan == null || string.IsNullOrWhiteSpace(plan.Seller)
                || string.IsNullOrWhiteSpace(plan.Buyer) || string.IsNullOrWhiteSpace(plan.OrderId))
            {
                reason = "invalid_plan";
                return false;
            }

            lock (ActionSync)
            {
                EnsureActionStateLoadedLocked();
                var now = DateTime.Now;
                ActiveActions.RemoveAll(x => x == null || x.Until <= now);
                _actionState.Records.RemoveAll(x => x == null || x.Until <= now);

                var canonical = FindCanonicalOrderIdLocked(plan.Seller, plan.Buyer, plan.OrderId);
                if (!string.IsNullOrWhiteSpace(canonical)
                    && !string.Equals(canonical, plan.OrderId, StringComparison.Ordinal))
                {
                    Log.Info("订单号精度别名已归一化: orderId=" + plan.OrderId + ", canonicalOrderId=" + canonical);
                    plan.OrderId = canonical;
                    if (plan.Snapshot != null) plan.Snapshot.OrderId = canonical;
                    plan.ReservationKey = BuildReservationKey(plan.Seller, plan.Buyer, canonical, plan.IsBuyerFollowUp);
                }

                if (IsSuspiciousRoundedOrderId(plan.OrderId)
                    && string.IsNullOrWhiteSpace(FindCanonicalOrderIdLocked(plan.Seller, plan.Buyer, plan.OrderId, true)))
                {
                    reason = "precision_risk_order_id";
                    Log.ErrorWithMaxCount("订单自动回复已阻止：检测到疑似 JavaScript Number 精度损失的长订单号，等待精确字符串订单事件补偿。 orderId="
                        + plan.OrderId, 50);
                    return false;
                }

                if (ActiveActions.Any(x => SameAction(x, plan)))
                {
                    reason = "action_inflight";
                    return false;
                }
                if (_actionState.Records.Any(x => x.Delivered && SameAction(x, plan)))
                {
                    reason = "action_already_delivered";
                    return false;
                }
                if (_actionState.Records.Any(x => x.DeliveryUncertain && SameAction(x, plan)))
                {
                    // A send action was physically triggered but live echo and remote verification
                    // were both unavailable. Never blind-resend on another Created/Paid ingress.
                    reason = "action_delivery_uncertain";
                    return false;
                }

                ActiveActions.Add(new OrderReplyActionRecord
                {
                    Seller = Normalize(plan.Seller),
                    Buyer = NormalizeBuyer(plan.Seller, plan.Buyer),
                    OrderId = plan.OrderId.Trim(),
                    FollowUp = plan.IsBuyerFollowUp,
                    Until = now.AddMinutes(10),
                    Delivered = false
                });
                SaveActionStateLocked();
                return true;
            }
        }

        internal static void MarkDeliveryUncertain(OrderPlacedReplyPlan plan, string reason)
        {
            if (plan == null) return;
            lock (ActionSync)
            {
                EnsureActionStateLoadedLocked();
                var existing = _actionState.Records.FirstOrDefault(x => x != null && SameAction(x, plan));
                if (existing == null)
                {
                    existing = new OrderReplyActionRecord();
                    _actionState.Records.Add(existing);
                }
                existing.Seller = Normalize(plan.Seller);
                existing.Buyer = NormalizeBuyer(plan.Seller, plan.Buyer);
                existing.OrderId = (plan.OrderId ?? string.Empty).Trim();
                existing.FollowUp = plan.IsBuyerFollowUp;
                existing.Until = DateTime.Now.AddMinutes(10);
                existing.Delivered = false;
                existing.DeliveryUncertain = true;
                SaveActionStateLocked();
            }
            Log.ErrorWithMaxCount(
                "订单发送状态不确定，10分钟内禁止自动重发以避免重复: seller=" + plan.Seller
                + ", buyer=" + plan.Buyer + ", orderId=" + plan.OrderId
                + ", reason=" + (reason ?? string.Empty),
                20);
        }

        internal static void FinishExecution(OrderPlacedReplyPlan plan, bool delivered, int sentSegments)
        {
            if (plan == null) return;
            lock (ActionSync)
            {
                EnsureActionStateLoadedLocked();
                ActiveActions.RemoveAll(x => x != null && SameAction(x, plan));
                if (delivered || sentSegments > 0)
                {
                    var now = DateTime.Now;
                    var hours = plan.IsBuyerFollowUp
                        ? 720
                        : (plan.Config == null ? 24 : Math.Max(1, Math.Min(720, plan.Config.OrderPlacedDedupHours)));
                    var until = delivered ? now.AddHours(hours) : now.AddMinutes(10);
                    var existing = _actionState.Records.FirstOrDefault(x => x != null && SameAction(x, plan));
                    if (existing == null)
                    {
                        existing = new OrderReplyActionRecord();
                        _actionState.Records.Add(existing);
                    }
                    existing.Seller = Normalize(plan.Seller);
                    existing.Buyer = NormalizeBuyer(plan.Seller, plan.Buyer);
                    existing.OrderId = plan.OrderId.Trim();
                    existing.FollowUp = plan.IsBuyerFollowUp;
                    existing.Until = until;
                    existing.Delivered = delivered || sentSegments > 0;
                    existing.DeliveryUncertain = false;
                }
                _actionState.Records.RemoveAll(x => x == null || x.Until <= DateTime.Now);
                SaveActionStateLocked();
            }
        }

        private static void ObserveCanonicalOrderId(string seller, string buyer, string orderId)
        {
            orderId = (orderId ?? string.Empty).Trim();
            if (orderId.Length < 8 || IsSuspiciousRoundedOrderId(orderId)) return;
            lock (ActionSync)
            {
                EnsureActionStateLoadedLocked();
                var exists = _actionState.Records.Any(x => x != null
                    && !x.FollowUp
                    && Normalize(x.Seller) == Normalize(seller)
                    && NormalizeBuyer(x.Seller, x.Buyer) == NormalizeBuyer(seller, buyer)
                    && string.Equals(x.OrderId, orderId, StringComparison.Ordinal));
                if (!exists)
                {
                    _actionState.Records.Add(new OrderReplyActionRecord
                    {
                        Seller = Normalize(seller),
                        Buyer = NormalizeBuyer(seller, buyer),
                        OrderId = orderId,
                        FollowUp = false,
                        Until = DateTime.Now.AddHours(2),
                        Delivered = false
                    });
                    SaveActionStateLocked();
                }
            }
        }

        private static string FindCanonicalOrderIdLocked(string seller, string buyer, string orderId, bool requireExactCandidate = false)
        {
            var normalizedSeller = Normalize(seller);
            var normalizedBuyer = NormalizeBuyer(seller, buyer);
            orderId = (orderId ?? string.Empty).Trim();
            var candidates = ActiveActions.Concat(_actionState == null ? new List<OrderReplyActionRecord>() : _actionState.Records)
                .Where(x => x != null
                    && Normalize(x.Seller) == normalizedSeller
                    && NormalizeBuyer(x.Seller, x.Buyer) == normalizedBuyer
                    && !string.IsNullOrWhiteSpace(x.OrderId))
                .Select(x => x.OrderId.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var exact = candidates.FirstOrDefault(x => string.Equals(x, orderId, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(exact) && !requireExactCandidate) return exact;
            return candidates.FirstOrDefault(x => !IsSuspiciousRoundedOrderId(x) && ArePrecisionAliases(x, orderId)) ?? string.Empty;
        }

        private static bool SameAction(OrderReplyActionRecord record, OrderPlacedReplyPlan plan)
        {
            if (record == null || plan == null) return false;
            return record.FollowUp == plan.IsBuyerFollowUp
                && Normalize(record.Seller) == Normalize(plan.Seller)
                && NormalizeBuyer(record.Seller, record.Buyer) == NormalizeBuyer(plan.Seller, plan.Buyer)
                && (string.Equals((record.OrderId ?? string.Empty).Trim(), (plan.OrderId ?? string.Empty).Trim(), StringComparison.Ordinal)
                    || ArePrecisionAliases(record.OrderId, plan.OrderId));
        }

        internal static bool ArePrecisionAliases(string left, string right)
        {
            left = (left ?? string.Empty).Trim();
            right = (right ?? string.Empty).Trim();
            if (left.Length < 16 || right.Length != left.Length) return false;
            if (!Regex.IsMatch(left, @"^\d+$") || !Regex.IsMatch(right, @"^\d+$")) return false;
            if (!IsSuspiciousRoundedOrderId(left) && !IsSuspiciousRoundedOrderId(right)) return false;
            ulong a;
            ulong b;
            if (!ulong.TryParse(left, out a) || !ulong.TryParse(right, out b)) return false;
            var delta = a >= b ? a - b : b - a;
            return delta > 0 && delta <= 4096UL;
        }

        internal static bool IsSuspiciousRoundedOrderId(string value)
        {
            value = (value ?? string.Empty).Trim();
            return value.Length >= 16
                && Regex.IsMatch(value, @"^\d+$")
                && Regex.IsMatch(value, @"0{3,}$");
        }

        private static string BuildReservationKey(string seller, string buyer, string orderId, bool followUp)
        {
            return Normalize(seller) + "#" + NormalizeBuyer(seller, buyer) + "#" + (orderId ?? string.Empty).Trim()
                + (followUp ? "#guidance-followup" : string.Empty);
        }

        private static void EnsureActionStateLoadedLocked()
        {
            if (_actionState != null) return;
            try
            {
                var path = GetActionStatePath();
                _actionState = File.Exists(path)
                    ? JsonConvert.DeserializeObject<OrderReplyActionState>(File.ReadAllText(path, Encoding.UTF8))
                    : new OrderReplyActionState();
            }
            catch
            {
                _actionState = new OrderReplyActionState();
            }
            if (_actionState == null) _actionState = new OrderReplyActionState();
            if (_actionState.Records == null) _actionState.Records = new List<OrderReplyActionRecord>();
        }

        private static void SaveActionStateLocked()
        {
            try
            {
                if (_actionState == null) return;
                var path = GetActionStatePath();
                var directory = Path.GetDirectoryName(path);
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(_actionState, Formatting.Indented), new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("保存订单自动回复动作幂等状态失败：" + Short(ex.Message, 220), 10);
            }
        }

        private static string GetActionStatePath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QianniuAiBot",
                "data",
                "order-reply-action-state.json");
        }

        private static async Task<OrderPlacedReplyResolution> CallReplyApiAsync(OrderPlacedReplyPlan plan)
        {
            Uri uri;
            if (!Uri.TryCreate((plan.Config.OrderPlacedApiUrl ?? string.Empty).Trim(), UriKind.Absolute, out uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return Fail("下单回复接口地址无效");
            var timeout = Math.Max(3, Math.Min(60, plan.Config.OrderPlacedApiTimeoutSeconds));
            var snapshot = plan.Snapshot;
            var payload = new JObject
            {
                ["event"] = plan.IsBuyerFollowUp ? "buyer_order_guidance_followup" : (snapshot != null && snapshot.EventType == OrderEventType.Paid ? "buyer_order_paid" : "buyer_order_created"),
                ["seller"] = plan.Seller, ["buyer"] = plan.Buyer, ["orderId"] = plan.OrderId,
                ["eventTime"] = plan.EventTime.ToString("yyyy-MM-dd HH:mm:ss"), ["message"] = Short(plan.EventText, 1200),
                ["buyerFollowUp"] = plan.IsBuyerFollowUp, ["triggerText"] = plan.TriggerText ?? string.Empty
            };
            if (snapshot != null)
            {
                payload["itemId"] = snapshot.ItemId ?? string.Empty; payload["itemTitle"] = snapshot.ItemTitle ?? string.Empty;
                payload["skuId"] = snapshot.SkuId ?? string.Empty; payload["skuText"] = snapshot.SkuText ?? string.Empty;
                payload["quantity"] = snapshot.Quantity;
                payload["totalAmount"] = snapshot.TotalAmount.HasValue ? (JToken)snapshot.TotalAmount.Value : JValue.CreateNull();
                payload["paidAmount"] = snapshot.PaidAmount.HasValue ? (JToken)snapshot.PaidAmount.Value : JValue.CreateNull();
                payload["tradeStatus"] = snapshot.TradeStatus ?? string.Empty;
                payload["isPaid"] = snapshot.IsPaid.HasValue ? (JToken)snapshot.IsPaid.Value : JValue.CreateNull();
                payload["productUrl"] = snapshot.ProductUrl ?? string.Empty;
            }
            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeout) })
                {
                    var token = (plan.Config.OrderPlacedApiToken ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(token)) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using (var content = new StringContent(payload.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json"))
                    using (var response = await http.PostAsync(uri, content))
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        if (!response.IsSuccessStatusCode) return Fail("HTTP " + (int)response.StatusCode + " " + Short(body, 300));
                        var reply = ExtractReply(body);
                        if (string.IsNullOrWhiteSpace(reply)) return Fail("接口成功但未返回 reply/answer/message");
                        return new OrderPlacedReplyResolution { Success = true, Reply = RenderTemplate(reply, plan, "http-response"), Source = "下单自动回复-HTTP接口" };
                    }
                }
            }
            catch (Exception ex) { return Fail(ex.Message); }
        }

        private static string ExtractReply(string body)
        {
            body = (body ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(body)) return string.Empty;
            try
            {
                var token = JToken.Parse(body);
                var reply = token["reply"] ?? token["answer"] ?? token["message"] ?? token["data"]?["reply"] ?? token["data"]?["answer"] ?? token["data"]?["message"];
                return reply == null ? string.Empty : reply.ToString().Trim();
            }
            catch { return body.Length <= 1000 ? body : string.Empty; }
        }

        private static string RenderTemplate(string template, OrderPlacedReplyPlan plan, string source)
        {
            var snapshot = plan == null ? null : plan.Snapshot;
            var missing = MissingTemplateFields(template, plan);
            var present = PresentTemplateFields(template, plan);
            var missingReasons = BuildRenderMissingReasons(missing, plan);
            var rendered = (template ?? string.Empty)
                .Replace("{客服}", plan == null ? string.Empty : plan.Seller ?? string.Empty)
                .Replace("{买家}", plan == null ? string.Empty : plan.Buyer ?? string.Empty)
                .Replace("{订单号}", plan == null ? string.Empty : plan.OrderId ?? string.Empty)
                .Replace("{时间}", plan == null || plan.EventTime == DateTime.MinValue ? string.Empty : plan.EventTime.ToString("yyyy-MM-dd HH:mm:ss"))
                .Replace("{商品}", snapshot == null ? string.Empty : snapshot.ItemTitle ?? string.Empty)
                .Replace("{sku}", snapshot == null ? string.Empty : snapshot.SkuText ?? string.Empty)
                .Replace("{规格}", snapshot == null ? string.Empty : snapshot.SkuText ?? string.Empty)
                .Replace("{买家备注}", snapshot == null ? string.Empty : snapshot.BuyerRemark ?? string.Empty)
                .Replace("{数量}", snapshot == null || snapshot.Quantity <= 0 ? string.Empty : snapshot.Quantity.ToString())
                .Replace("{金额}", snapshot == null || !snapshot.TotalAmount.HasValue ? string.Empty : snapshot.TotalAmount.Value.ToString("0.00"))
                .Replace("{实付}", snapshot == null || !snapshot.PaidAmount.HasValue ? string.Empty : snapshot.PaidAmount.Value.ToString("0.00"))
                .Replace("{订单状态}", snapshot == null ? string.Empty : snapshot.TradeStatus ?? string.Empty);
            var allRequestedFieldsMissing = missing.Count > 0 && present.Count == 0;
            Log.Info("order_template_render source=" + source + " orderId=" + (plan == null ? string.Empty : plan.OrderId)
                + " partial=" + (missing.Count > 0 && present.Count > 0).ToString().ToLowerInvariant()
                + " all_requested_fields_missing=" + allRequestedFieldsMissing.ToString().ToLowerInvariant()
                + " present=" + string.Join(",", present) + " missing=" + string.Join(",", missing)
                + " missing_reason=" + string.Join("|", missingReasons)
                + " snapshot_source=" + Short(snapshot == null ? string.Empty : snapshot.Source, 100)
                + " rendered_length=" + rendered.Length);
            return allRequestedFieldsMissing ? string.Empty : rendered;
        }

        private static OrderPlacedReplyResolution Fail(string error) { return new OrderPlacedReplyResolution { Success = false, Error = Short(error, 500) }; }
        private static string Normalize(string value) { return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", string.Empty); }
        private static string NormalizeBuyer(string seller, string buyer)
        {
            var canonical = BuyerIdentityAliasService.ResolveInternalNick(
                (seller ?? string.Empty).Trim(),
                (buyer ?? string.Empty).Trim());
            return Normalize(string.IsNullOrWhiteSpace(canonical) ? buyer : canonical);
        }
        private static string Short(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }

    public partial class QN
    {
        private const string OrderPresetSegmentToken = "{分段符}";

        private sealed class OrderPresetSendResult
        {
            public bool Success { get; set; }
            public int SentSegments { get; set; }
        }

        private static List<string> SplitOrderPresetSegments(string answer)
        {
            var result = new List<string>();
            foreach (var part in (answer ?? string.Empty).Split(new[] { OrderPresetSegmentToken }, StringSplitOptions.None))
                if (!string.IsNullOrWhiteSpace(part)) result.Add(part);
            return result;
        }

        private async Task<bool> SendMandatoryOrderTextAsync(OrderPlacedReplyPlan plan, string text)
        {
            if (plan == null || string.IsNullOrWhiteSpace(text)) return false;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                // The generic chat path intentionally yields to a human agent. Configured order
                // business rules are different: once a Created/Paid event has reserved a plan,
                // manual replies must never consume or cancel this configured message.
                var sendStartedAt = DateTime.Now;
                KnowledgeLearningService.AllowNextManualSend(plan.Seller, plan.Buyer, text);
                var sent = await SendTextWithRetryAsync(plan.Buyer, text, 0);
                if (sent) return true;

                // Live seller echo can be lost when the authoritative CDP page reconnects. Before
                // retrying a mandatory order message, query the verified buyer conversation history.
                // This prevents a false-negative live echo from becoming a duplicate customer send.
                var remote = await VerifySellerEchoInRemoteHistoryAsync(
                    plan.Seller,
                    plan.Buyer,
                    text,
                    sendStartedAt).ConfigureAwait(false);
                if (remote == RemoteSellerEchoVerification.Delivered)
                {
                    Log.Info("订单发送已由远端历史二次确认，取消自动重试: seller=" + plan.Seller
                        + ", buyer=" + plan.Buyer + ", orderId=" + plan.OrderId);
                    return true;
                }
                if (remote == RemoteSellerEchoVerification.Unavailable)
                {
                    OrderPlacedAutoReplyService.MarkDeliveryUncertain(
                        plan,
                        "live_echo_missing_and_remote_history_unavailable");
                    return false;
                }

                if (attempt == 0)
                {
                    Log.Info("强制订单规则发送失败且远端历史确认未送达，准备单次安全重试: seller="
                        + plan.Seller + ", buyer=" + plan.Buyer + ", orderId=" + plan.OrderId
                        + ", attempt=1");
                    await Task.Delay(180).ConfigureAwait(false);
                }
            }
            return false;
        }

        private async Task<OrderPresetSendResult> SendOrderPresetAnswerAsync(OrderPlacedReplyPlan plan, string answer)
        {
            var result = new OrderPresetSendResult();
            var segments = SplitOrderPresetSegments(answer);
            if (segments.Count == 0) return result;
            for (var i = 0; i < segments.Count; i++)
            {
                if (i > 0) await Task.Delay(220);
                Log.Info("下单固定预设分段强制自动发送: buyer=" + plan.Buyer
                    + ", segment=" + (i + 1) + "/" + segments.Count
                    + ", manualReplyDoesNotSuppress=true");
                if (!await SendMandatoryOrderTextAsync(plan, segments[i]))
                {
                    result.Success = false;
                    return result;
                }
                result.SentSegments++;
            }
            result.Success = true;
            return result;
        }

        private async Task ProcessOrderPlacedReplyAsync(OrderPlacedReplyPlan plan)
        {
            string actionReason;
            if (!OrderPlacedAutoReplyService.TryBeginExecution(plan, out actionReason))
            {
                if (plan != null)
                {
                    // Only a durably delivered action may extend the normal long reservation.
                    // In-flight/precision-risk/uncertain outcomes are not delivery success. In
                    // particular, delivery-uncertain has its own 10-minute durable safety window;
                    // converting it to Complete(true) here would suppress a legitimate retry for
                    // the full order dedup period (often 24h).
                    if (string.Equals(actionReason, "action_already_delivered", StringComparison.Ordinal))
                    {
                        OrderPlacedAutoReplyService.Complete(plan, true);
                    }
                    else if (!string.Equals(actionReason, "action_inflight", StringComparison.Ordinal))
                    {
                        OrderPlacedAutoReplyService.Complete(plan, false);
                    }
                    Log.Info("下单自动回复动作级幂等已阻止重复执行: seller=" + plan.Seller
                        + ", buyer=" + plan.Buyer + ", orderId=" + plan.OrderId
                        + ", reason=" + actionReason);
                }
                return;
            }

            using (BotActivityCoordinator.Begin("下单自动回复", plan == null ? string.Empty : plan.Seller, plan == null ? string.Empty : plan.Buyer))
            {
                try
                {
                    var resolution = await OrderPlacedAutoReplyService.ResolveAsync(plan);
                    if (!resolution.Success || string.IsNullOrWhiteSpace(resolution.Reply))
                    {
                        OrderPlacedAutoReplyService.Complete(plan, false);
                        OrderPlacedAutoReplyService.FinishExecution(plan, false, 0);
                        OrderAttentionUiService.SetReplyResult(plan == null ? null : plan.Snapshot, false);
                        var note = "下单自动回复未发送：" + (string.IsNullOrWhiteSpace(resolution.Error) ? "未生成回复" : resolution.Error);
                        AddSkippedConversation(plan.Seller, plan.Buyer, BuildPlanQuestion(plan), note);
                        Log.Info(note + ", buyer=" + plan.Buyer + ", orderId=" + plan.OrderId);
                        return;
                    }

                    var preserveTemplateLayout = !string.IsNullOrWhiteSpace(resolution.Source)
                        && (resolution.Source.IndexOf("固定预设", StringComparison.Ordinal) >= 0
                            || resolution.Source.IndexOf("接口失败兜底", StringComparison.Ordinal) >= 0);
                    var rawReply = resolution.Reply ?? string.Empty;
                    var answer = preserveTemplateLayout
                        ? (Regex.IsMatch(rawReply, @"(?:\[AI\]|【AI】|［AI］)\s*$", RegexOptions.IgnoreCase) ? rawReply : rawReply + " [AI]")
                        : BotOutboundMessageFormatter.EnsureAiMarker(BotFeatureStore.ApplyOutputPolicy(rawReply));

                    var autoSend = Params.Robot.GetIsAutoReply();
                    KnowledgeLearningService.RegisterAnswerSource(
                        plan.Seller, plan.Buyer, BuildPlanQuestion(plan),
                        BotOutboundMessageFormatter.StripAiMarker(answer), resolution.Source);
                    var ctl = Desk.Inst == null ? null : Desk.Inst.AddConversation(
                        plan.Seller, plan.Buyer, BuildPlanQuestion(plan), answer, autoSend, resolution.Source);

                    if (!autoSend)
                    {
                        OrderPlacedAutoReplyService.Complete(plan, false);
                        OrderPlacedAutoReplyService.FinishExecution(plan, false, 0);
                        if (ctl != null) ctl.SetSendResult(false, "未发送：自动回复开关已关闭");
                        return;
                    }

                    var delaySeconds = OrderPlacedReplyDelaySettings.GetSeconds();
                    if (delaySeconds > 0)
                    {
                        Log.Info("下单自动回复等待延时发送: seller=" + plan.Seller
                            + ", buyer=" + plan.Buyer + ", orderId=" + plan.OrderId
                            + ", delaySeconds=" + delaySeconds + ", manualReplyDoesNotSuppress=true");
                        await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
                        if (!Params.Robot.CanUseRobotReal || !Params.Robot.GetIsAutoReply())
                        {
                            OrderPlacedAutoReplyService.Complete(plan, false);
                            OrderPlacedAutoReplyService.FinishExecution(plan, false, 0);
                            if (ctl != null) ctl.SetSendResult(false, "未发送：延时期间 Bot 或自动回复开关已关闭");
                            return;
                        }
                    }

                    // EventHub is lifecycle-event dedupe; the action ledger above is the source of truth
                    // for the business side effect. Created -> Paid may update the same order snapshot,
                    // but can never execute the configured initial order reply twice.
                    OrderPresetSendResult presetSendResult = null;
                    bool sendOk;
                    if (preserveTemplateLayout)
                    {
                        presetSendResult = await SendOrderPresetAnswerAsync(plan, answer);
                        sendOk = presetSendResult.Success;
                    }
                    else
                    {
                        sendOk = await SendMandatoryOrderTextAsync(plan, answer);
                    }

                    OrderPlacedAutoReplyService.Complete(plan, sendOk);
                    OrderAttentionUiService.SetReplyResult(plan.Snapshot, sendOk);
                    if (sendOk)
                    {
                        OrderGuidanceDeliveryGuard.MarkDelivered(plan, plan.IsBuyerFollowUp ? "Bot强制补发" : "Bot强制订单规则发送");
                        ReplyDeduplicationService.RememberDelivered(plan.Seller, plan.Buyer, answer);
                    }
                    OrderPlacedAutoReplyService.FinishExecution(
                        plan,
                        sendOk,
                        presetSendResult == null ? (sendOk ? 1 : 0) : presetSendResult.SentSegments);
                    if (ctl != null)
                    {
                        var successDetail = plan.IsBuyerFollowUp
                            ? "已发送（买家明确续问，订单规则强制补发一次）"
                            : "已发送（订单自动回复规则强制执行，订单号 " + plan.OrderId + "）";
                        ctl.SetSendResult(sendOk, sendOk ? successDetail : "发送失败：" + rpa.GetSendFailureReason());
                    }
                    Log.Info("下单自动回复规则执行完成: seller=" + plan.Seller
                        + ", buyer=" + plan.Buyer + ", orderId=" + plan.OrderId
                        + ", delivered=" + sendOk + ", manualReplyIgnored=true");
                }
                catch
                {
                    OrderPlacedAutoReplyService.Complete(plan, false);
                    OrderPlacedAutoReplyService.FinishExecution(plan, false, 0);
                    throw;
                }
            }
        }

        private static string BuildPlanQuestion(OrderPlacedReplyPlan plan)
        {
            if (plan == null) return "[买家下单]";
            return plan.IsBuyerFollowUp
                ? "[买家续问充值流程] " + (plan.TriggerText ?? string.Empty)
                : "[买家下单] 订单号 " + plan.OrderId;
        }
    }
}