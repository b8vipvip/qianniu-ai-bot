using Bot.Automation.ChatDeskNs;
using Bot.ChatRecord;
using Bot.Options;
using BotLib;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
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
        private static readonly Regex OrderIdRegex = new Regex(
            @"(?:订单号|订单编号|订单)\s*[:：#]?\s*(\d{8,})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
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
            var cfg = BotFeatureStore.GetAutoReplyRules();
            if (cfg == null || !cfg.EnableOrderPlacedReply) return false;

            var text = CollectText(message, messageText);
            var orderId = ExtractOrderId(text);
            if (string.IsNullOrWhiteSpace(orderId) || !LooksLikeOrderPlaced(text)) return false;

            DateTime eventTime;
            if (!TryGetMessageTime(message, out eventTime)) eventTime = DateTime.Now;
            if (eventTime < botStartedAt.AddSeconds(-8))
            {
                Log.Info("下单自动消息已跳过历史订单: orderId=" + orderId + ", eventTime=" + eventTime.ToString("yyyy-MM-dd HH:mm:ss"));
                return false;
            }

            var key = Normalize(seller) + "#" + Normalize(buyer) + "#" + orderId;
            var now = DateTime.Now;
            DateTime until;
            if (Reservations.TryGetValue(key, out until) && until > now)
            {
                Log.Info("下单自动消息已去重: orderId=" + orderId + ", buyer=" + buyer);
                return true;
            }

            var reserveMinutes = Math.Max(2, Math.Min(30, cfg.OrderPlacedApiTimeoutSeconds <= 0 ? 2 : cfg.OrderPlacedApiTimeoutSeconds / 2 + 2));
            Reservations[key] = now.AddMinutes(reserveMinutes);
            plan = new OrderPlacedReplyPlan
            {
                Seller = (seller ?? string.Empty).Trim(),
                Buyer = (buyer ?? string.Empty).Trim(),
                OrderId = orderId,
                EventText = text,
                EventTime = eventTime,
                ReservationKey = key,
                Config = cfg
            };
            return true;
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
                if (api.Success) return api;
                var fallback = RenderTemplate(cfg.OrderPlacedReplyText, plan);
                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    Log.Info("下单回复接口失败，使用固定预设兜底: orderId=" + plan.OrderId + ", error=" + api.Error);
                    return new OrderPlacedReplyResolution
                    {
                        Success = true,
                        Reply = fallback,
                        Source = "下单自动回复-接口失败兜底"
                    };
                }
                return api;
            }

            var reply = RenderTemplate(cfg.OrderPlacedReplyText, plan);
            if (string.IsNullOrWhiteSpace(reply)) return Fail("下单固定预设答案为空");
            return new OrderPlacedReplyResolution
            {
                Success = true,
                Reply = reply,
                Source = "下单自动回复-固定预设"
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
            var hours = plan.Config == null ? 24 : Math.Max(1, Math.Min(720, plan.Config.OrderPlacedDedupHours));
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
            var payload = new JObject
            {
                ["event"] = "buyer_order_created",
                ["seller"] = plan.Seller,
                ["buyer"] = plan.Buyer,
                ["orderId"] = plan.OrderId,
                ["eventTime"] = plan.EventTime.ToString("yyyy-MM-dd HH:mm:ss"),
                ["message"] = Short(plan.EventText, 1200)
            };

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
                            Reply = RenderTemplate(reply, plan),
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

        private static string RenderTemplate(string template, OrderPlacedReplyPlan plan)
        {
            return (template ?? string.Empty)
                .Replace("{客服}", plan.Seller ?? string.Empty)
                .Replace("{买家}", plan.Buyer ?? string.Empty)
                .Replace("{订单号}", plan.OrderId ?? string.Empty)
                .Replace("{时间}", plan.EventTime.ToString("yyyy-MM-dd HH:mm:ss"))
                .Trim();
        }

        private static string CollectText(QNChatMessage message, string messageText)
        {
            var sb = new StringBuilder();
            Append(sb, messageText);
            if (message != null)
            {
                Append(sb, message.summary);
                if (message.originalData != null)
                {
                    Append(sb, message.originalData.text);
                    if (message.originalData.header != null)
                    {
                        Append(sb, message.originalData.header.title);
                        Append(sb, message.originalData.header.summary);
                    }
                }
            }
            return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        }

        private static void Append(StringBuilder sb, string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length < 1) return;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(value);
        }

        private static string ExtractOrderId(string text)
        {
            var match = OrderIdRegex.Match(text ?? string.Empty);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        private static bool LooksLikeOrderPlaced(string text)
        {
            text = text ?? string.Empty;
            return (text.Contains("件商品") && text.Contains("合计"))
                || text.Contains("交易时间")
                || text.Contains("买家已下单")
                || text.Contains("订单创建成功");
        }

        private static bool TryGetMessageTime(QNChatMessage message, out DateTime localTime)
        {
            localTime = DateTime.MinValue;
            if (message == null) return false;
            return TryParseTime(message.sendTime, out localTime)
                || TryParseTime(message.sortTimeMicrosecond, out localTime);
        }

        private static bool TryParseTime(string value, out DateTime localTime)
        {
            localTime = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(value)) return false;
            long raw;
            if (long.TryParse(value.Trim(), out raw))
            {
                try
                {
                    if (raw > 1000000000000000L) localTime = DateTimeOffset.FromUnixTimeMilliseconds(raw / 1000L).LocalDateTime;
                    else if (raw > 100000000000L) localTime = DateTimeOffset.FromUnixTimeMilliseconds(raw).LocalDateTime;
                    else if (raw > 1000000000L) localTime = DateTimeOffset.FromUnixTimeSeconds(raw).LocalDateTime;
                    if (localTime != DateTime.MinValue) return true;
                }
                catch { }
            }
            DateTimeOffset dto;
            if (DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out dto)
                || DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dto))
            {
                localTime = dto.LocalDateTime;
                return true;
            }
            return false;
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
            var resolution = await OrderPlacedAutoReplyService.ResolveAsync(plan);
            if (!resolution.Success || string.IsNullOrWhiteSpace(resolution.Reply))
            {
                OrderPlacedAutoReplyService.Complete(plan, false);
                var note = "下单自动回复未发送：" + (string.IsNullOrWhiteSpace(resolution.Error) ? "未生成回复" : resolution.Error);
                AddSkippedConversation(plan.Seller, plan.Buyer, "[买家下单] 订单号 " + plan.OrderId, note);
                Log.Info(note + ", buyer=" + plan.Buyer + ", orderId=" + plan.OrderId);
                return;
            }

            var answer = BotOutboundMessageFormatter.EnsureAiMarker(
                BotFeatureStore.ApplyOutputPolicy(resolution.Reply));
            var autoSend = Params.Robot.GetIsAutoReply();
            KnowledgeLearningService.RegisterAnswerSource(
                plan.Seller,
                plan.Buyer,
                "[买家下单] 订单号 " + plan.OrderId,
                BotOutboundMessageFormatter.StripAiMarker(answer),
                resolution.Source);
            var ctl = Desk.Inst == null
                ? null
                : Desk.Inst.AddConversation(
                    plan.Seller,
                    plan.Buyer,
                    "[买家下单] 订单号 " + plan.OrderId,
                    answer,
                    autoSend,
                    resolution.Source);

            if (!autoSend)
            {
                OrderPlacedAutoReplyService.Complete(plan, false);
                if (ctl != null) ctl.SetSendResult(false, "未发送：自动回复开关已关闭");
                return;
            }

            var sendOk = await SendTextWithRetryAsync(plan.Buyer, answer, 1);
            OrderPlacedAutoReplyService.Complete(plan, sendOk);
            if (sendOk)
            {
                ReplyDeduplicationService.RememberDelivered(plan.Seller, plan.Buyer, answer);
            }
            if (ctl != null)
            {
                ctl.SetSendResult(
                    sendOk,
                    sendOk
                        ? "已发送（买家下单自动消息，订单号 " + plan.OrderId + "）"
                        : "发送失败：" + rpa.GetSendFailureReason());
            }
        }
    }
}
