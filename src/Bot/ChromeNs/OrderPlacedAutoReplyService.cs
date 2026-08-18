using Bot.Automation.ChatDeskNs;
using Bot.ChatRecord;
using Bot.Options;
using BotLib;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        private static readonly ConcurrentDictionary<string, DateTime> Reservations =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);

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

            // 订单识别和空闲自动切换独立于“下单后自动发送”开关。
            // 关闭自动发送时仍会生成右侧订单摘要并在空闲时切换，但不会给买家发消息。
            var cfg = BotFeatureStore.GetAutoReplyRules();
            if (cfg == null || !cfg.EnableOrderPlacedReply) return true;
            if (snapshot.EventType != OrderEventType.Created && snapshot.EventType != OrderEventType.Paid) return true;

            var key = Normalize(seller) + "#" + Normalize(buyer) + "#" + snapshot.OrderId;
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
            if (!OrderGuidanceDeliveryGuard.CanCreateFollowUp(
                seller,
                buyer,
                messageText,
                out snapshot,
                out reason))
            {
                return false;
            }

            var trigger = (messageText ?? string.Empty).Trim();
            var key = Normalize(seller) + "#" + Normalize(buyer) + "#" + snapshot.OrderId + "#guidance-followup";
            DateTime until;
            if (Reservations.TryGetValue(key, out until) && until > DateTime.Now)
            {
                Log.Info("买家充值流程续问已去重: buyer=" + buyer + ", orderId=" + snapshot.OrderId);
                return true;
            }
            Reservations[key] = DateTime.Now.AddMinutes(5);
            plan = new OrderPlacedReplyPlan
            {
                Seller = (seller ?? string.Empty).Trim(),
                Buyer = (buyer ?? string.Empty).Trim(),
                OrderId = snapshot.OrderId,
                EventText = trigger,
                EventTime = snapshot.EventTime,
                ReservationKey = key,
                Config = cfg,
                Snapshot = snapshot,
                IsBuyerFollowUp = true,
                TriggerText = trigger,
                TriggerTime = DateTime.Now
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
            if ((template.Contains("{sku}") || template.Contains("{规格}"))
                && (snapshot == null || string.IsNullOrWhiteSpace(snapshot.SkuText))) missing.Add("sku");
            if (template.Contains("{买家备注}")
                && (snapshot == null || string.IsNullOrWhiteSpace(snapshot.BuyerRemark))) missing.Add("buyer_remark");
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
            if ((template.Contains("{sku}") || template.Contains("{规格}"))
                && snapshot != null && !string.IsNullOrWhiteSpace(snapshot.SkuText)) present.Add("sku");
            if (template.Contains("{买家备注}")
                && snapshot != null && !string.IsNullOrWhiteSpace(snapshot.BuyerRemark)) present.Add("buyer_remark");
            if (template.Contains("{数量}") && snapshot != null && snapshot.Quantity > 0) present.Add("quantity");
            if (template.Contains("{金额}") && snapshot != null && snapshot.TotalAmount.HasValue) present.Add("total");
            if (template.Contains("{实付}") && snapshot != null && snapshot.PaidAmount.HasValue) present.Add("paid");
            if (template.Contains("{商品}") && snapshot != null && !string.IsNullOrWhiteSpace(snapshot.ItemTitle)) present.Add("item");
            if (template.Contains("{订单状态}") && snapshot != null && !string.IsNullOrWhiteSpace(snapshot.TradeStatus)) present.Add("status");
            return present;
        }

        private static List<string> BuildRenderMissingReasons(
            IList<string> missing,
            OrderPlacedReplyPlan plan)
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
            if (plan == null || plan.Config == null)
            {
                return Fail("下单自动回复计划为空");
            }

            var cfg = plan.Config;
            var mode = string.IsNullOrWhiteSpace(cfg.OrderPlacedReplyMode)
                ? "固定预设答案"
                : cfg.OrderPlacedReplyMode.Trim();
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
                    return new OrderPlacedReplyResolution
                    {
                        Success = true,
                        Reply = fallback,
                        Source = plan.IsBuyerFollowUp
                            ? "下单自动回复-接口失败兜底（买家明确续问）"
                            : "下单自动回复-接口失败兜底"
                    };
                }
                return api;
            }

            var reply = RenderTemplate(cfg.OrderPlacedReplyText, plan, "fixed-preset");
            if (string.IsNullOrWhiteSpace(reply)) return Fail("下单固定预设答案为空");
            return new OrderPlacedReplyResolution
            {
                Success = true,
                Reply = reply,
                Source = plan.IsBuyerFollowUp
                    ? "下单自动回复-固定预设（买家明确续问）"
                    : "下单自动回复-固定预设"
            };
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
            var hours = plan.IsBuyerFollowUp
                ? 720
                : (plan.Config == null ? 24 : Math.Max(1, Math.Min(720, plan.Config.OrderPlacedDedupHours)));
            Reservations[plan.ReservationKey] = DateTime.Now.AddHours(hours);
        }

        private static async Task<OrderPlacedReplyResolution> CallReplyApiAsync(OrderPlacedReplyPlan plan)
        {
            Uri uri;
            if (!Uri.TryCreate((plan.Config.OrderPlacedApiUrl ?? string.Empty).Trim(), UriKind.Absolute, out uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return Fail("下单回复接口地址无效");
            }

            var timeout = Math.Max(3, Math.Min(60, plan.Config.OrderPlacedApiTimeoutSeconds));
            var snapshot = plan.Snapshot;
            var payload = new JObject
            {
                ["event"] = plan.IsBuyerFollowUp
                    ? "buyer_order_guidance_followup"
                    : (snapshot != null && snapshot.EventType == OrderEventType.Paid
                        ? "buyer_order_paid"
                        : "buyer_order_created"),
                ["seller"] = plan.Seller,
                ["buyer"] = plan.Buyer,
                ["orderId"] = plan.OrderId,
                ["eventTime"] = plan.EventTime.ToString("yyyy-MM-dd HH:mm:ss"),
                ["message"] = Short(plan.EventText, 1200),
                ["buyerFollowUp"] = plan.IsBuyerFollowUp,
                ["triggerText"] = plan.TriggerText ?? string.Empty
            };
            if (snapshot != null)
            {
                payload["itemId"] = snapshot.ItemId ?? string.Empty;
                payload["itemTitle"] = snapshot.ItemTitle ?? string.Empty;
                payload["skuId"] = snapshot.SkuId ?? string.Empty;
                payload["skuText"] = snapshot.SkuText ?? string.Empty;
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
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    }
                    using (var content = new StringContent(payload.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json"))
                    using (var response = await http.PostAsync(uri, content))
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        if (!response.IsSuccessStatusCode)
                        {
                            return Fail("HTTP " + (int)response.StatusCode + " " + Short(body, 300));
                        }
                        var reply = ExtractReply(body);
                        if (string.IsNullOrWhiteSpace(reply)) return Fail("接口成功但未返回 reply/answer/message");
                        return new OrderPlacedReplyResolution
                        {
                            Success = true,
                            Reply = RenderTemplate(reply, plan, "http-response"),
                            Source = "下单自动回复-HTTP接口"
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        }

        private static string ExtractReply(string body)
        {
            body = (body ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(body)) return string.Empty;
            try
            {
                var token = JToken.Parse(body);
                var reply = token["reply"] ?? token["answer"] ?? token["message"]
                    ?? token["data"]?["reply"] ?? token["data"]?["answer"] ?? token["data"]?["message"];
                return reply == null ? string.Empty : reply.ToString().Trim();
            }
            catch
            {
                return body.Length <= 1000 ? body : string.Empty;
            }
        }

        private static string RenderTemplate(
            string template,
            OrderPlacedReplyPlan plan,
            string source)
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

            // 固定预设/兜底答案保留商家编辑的换行、连续空格和缩进；只替换变量，不重排版。

            var allRequestedFieldsMissing = missing.Count > 0 && present.Count == 0;
            Log.Info("order_template_render"
                + " source=" + source
                + " orderId=" + (plan == null ? string.Empty : plan.OrderId)
                + " partial=" + (missing.Count > 0 && present.Count > 0).ToString().ToLowerInvariant()
                + " all_requested_fields_missing=" + allRequestedFieldsMissing.ToString().ToLowerInvariant()
                + " present=" + string.Join(",", present)
                + " missing=" + string.Join(",", missing)
                + " missing_reason=" + string.Join("|", missingReasons)
                + " snapshot_source=" + Short(snapshot == null ? string.Empty : snapshot.Source, 100)
                + " rendered_length=" + rendered.Length);

            // 若模板引用的所有动态字段都缺失，禁止发送仅剩静态标点的空壳消息。
            return allRequestedFieldsMissing ? string.Empty : rendered;
        }

        private static OrderPlacedReplyResolution Fail(string error)
        {
            return new OrderPlacedReplyResolution { Success = false, Error = Short(error, 500) };
        }

        private static string Normalize(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", string.Empty);
        }

        private static string Short(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }

    public partial class QN
    {
        private async Task ProcessOrderPlacedReplyAsync(OrderPlacedReplyPlan plan)
        {
            using (BotActivityCoordinator.Begin("下单自动回复", plan == null ? string.Empty : plan.Seller, plan == null ? string.Empty : plan.Buyer))
            {
                var resolution = await OrderPlacedAutoReplyService.ResolveAsync(plan);
                if (!resolution.Success || string.IsNullOrWhiteSpace(resolution.Reply))
                {
                    OrderPlacedAutoReplyService.Complete(plan, false);
                    OrderAttentionUiService.SetReplyResult(plan == null ? null : plan.Snapshot, false);
                    var note = "下单自动回复未发送：" + (string.IsNullOrWhiteSpace(resolution.Error) ? "未生成回复" : resolution.Error);
                    AddSkippedConversation(plan.Seller, plan.Buyer, BuildPlanQuestion(plan), note);
                    Log.Info(note + ", buyer=" + plan.Buyer + ", orderId=" + plan.OrderId);
                    return;
                }

                var answer = BotOutboundMessageFormatter.EnsureAiMarker(
                    BotFeatureStore.ApplyOutputPolicy(resolution.Reply));

                string duplicateReason;
                if (OrderGuidanceDeliveryGuard.ShouldSuppressBeforeSend(this, plan, answer, out duplicateReason))
                {
                    OrderPlacedAutoReplyService.Complete(plan, true);
                    OrderAttentionUiService.SetReplyResult(plan.Snapshot, true);
                    AddSkippedConversation(plan.Seller, plan.Buyer, BuildPlanQuestion(plan), "未重复发送：" + duplicateReason);
                    Log.Info("下单固定预设已抑制重复发送: seller=" + plan.Seller
                        + ", buyer=" + plan.Buyer + ", orderId=" + plan.OrderId
                        + ", reason=" + duplicateReason);
                    return;
                }

                var autoSend = Params.Robot.GetIsAutoReply();
                KnowledgeLearningService.RegisterAnswerSource(
                    plan.Seller,
                    plan.Buyer,
                    BuildPlanQuestion(plan),
                    BotOutboundMessageFormatter.StripAiMarker(answer),
                    resolution.Source);
                var ctl = Desk.Inst == null
                    ? null
                    : Desk.Inst.AddConversation(
                        plan.Seller,
                        plan.Buyer,
                        BuildPlanQuestion(plan),
                        answer,
                        autoSend,
                        resolution.Source);

                if (!autoSend)
                {
                    OrderPlacedAutoReplyService.Complete(plan, false);
                    if (ctl != null) ctl.SetSendResult(false, "未发送：自动回复开关已关闭");
                    return;
                }

                var delaySeconds = OrderPlacedReplyDelaySettings.GetSeconds();
                if (delaySeconds > 0)
                {
                    Log.Info("下单自动回复等待延时发送: seller=" + plan.Seller
                        + ", buyer=" + plan.Buyer + ", orderId=" + plan.OrderId
                        + ", delaySeconds=" + delaySeconds);
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds));

                    if (!Params.Robot.CanUseRobotReal || !Params.Robot.GetIsAutoReply())
                    {
                        OrderPlacedAutoReplyService.Complete(plan, false);
                        if (ctl != null) ctl.SetSendResult(false, "未发送：延时期间 Bot 或自动回复开关已关闭");
                        Log.Info("下单自动回复延时后取消发送: seller=" + plan.Seller
                            + ", buyer=" + plan.Buyer + ", orderId=" + plan.OrderId);
                        return;
                    }
                }

                // 延时期间人工客服可能已经发送了同一条充值流程，因此发送前必须再次检查。
                if (OrderGuidanceDeliveryGuard.ShouldSuppressBeforeSend(this, plan, answer, out duplicateReason))
                {
                    OrderPlacedAutoReplyService.Complete(plan, true);
                    OrderAttentionUiService.SetReplyResult(plan.Snapshot, true);
                    if (ctl != null) ctl.SetSendResult(false, "未发送：" + duplicateReason);
                    Log.Info("下单固定预设在发送前被人工回复抑制: seller=" + plan.Seller
                        + ", buyer=" + plan.Buyer + ", orderId=" + plan.OrderId
                        + ", reason=" + duplicateReason);
                    return;
                }

                KnowledgeLearningService.AllowNextManualSend(plan.Seller, plan.Buyer, answer);
                Log.Info("下单自动回复已登记精确发送豁免: seller=" + plan.Seller
                    + ", buyer=" + plan.Buyer + ", orderId=" + plan.OrderId);

                var sendOk = await SendTextWithRetryAsync(plan.Buyer, answer, 1);
                OrderPlacedAutoReplyService.Complete(plan, sendOk);
                OrderAttentionUiService.SetReplyResult(plan.Snapshot, sendOk);
                if (sendOk)
                {
                    OrderGuidanceDeliveryGuard.MarkDelivered(
                        plan,
                        plan.IsBuyerFollowUp ? "Bot补发" : "Bot首次发送");
                    ReplyDeduplicationService.RememberDelivered(plan.Seller, plan.Buyer, answer);
                }
                if (ctl != null)
                {
                    ctl.SetSendResult(
                        sendOk,
                        sendOk
                            ? (plan.IsBuyerFollowUp
                                ? "已发送（买家明确续问，充值流程仅补发一次）"
                                : "已发送（买家下单自动消息，订单号 " + plan.OrderId + "）")
                            : "发送失败：" + rpa.GetSendFailureReason());
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
