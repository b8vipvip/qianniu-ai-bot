using Bot.ChatRecord;
using Bot.Options;
using BotLib;
using DbEntity;
using DbEntity.Response;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bot
{
    /// <summary>
    /// receiveNewMsg 订单卡片可能早于 messageCenterNotify 到达。
    /// 必须在旧 DirectOrderEventBridge 之前取得计划并补齐交易详情，否则固定模板会先用稀疏快照发送。
    /// </summary>
    public partial class App
    {
        private static readonly object OrderTemplateReceiveNewMessageTradeDetailBootstrap =
            ChromeNs.OrderTemplateReceiveNewMessageTradeDetailBridge.InitializeForApp();
    }
}

namespace Bot.ChromeNs
{
    internal static class OrderTemplateReceiveNewMessageTradeDetailBridge
    {
        private static readonly ConcurrentDictionary<QN, byte> Attached =
            new ConcurrentDictionary<QN, byte>();
        private static readonly ConcurrentDictionary<string, string> BuyerSecurityIds =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        private static readonly DateTime StartedAt = DateTime.Now;

        private static Timer _timer;
        private static int _initialized;

        private static readonly Regex OrderIdTextRegex = new Regex(
            @"(?:订单号|订单编号|主订单号|子订单号|交易号|订单)\s*[:：#]?\s*(\d{8,})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                // 先于 100ms 的原始字段桥接和 750ms 的旧直接订单桥接订阅。
                _timer = new Timer(_ => Attach(), null, 0, 10);
            }
            return new object();
        }

        private static void Attach()
        {
            try
            {
                foreach (var qn in QN.GetRuntimeSafetySnapshot())
                {
                    if (qn == null || !Attached.TryAdd(qn, 0)) continue;
                    qn.EvRecieveNewMessage += OnReceiveNewMessage;
                    Log.Info("receiveNewMsg订单模板交易详情补全桥接已绑定: seller="
                        + (qn.Seller == null ? string.Empty : qn.Seller.Nick));
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("绑定 receiveNewMsg 订单模板补全桥接失败：" + ex.Message, 10);
            }
        }

        private static void OnReceiveNewMessage(object sender, RecieveNewMessageEventArgs e)
        {
            var qn = sender as QN;
            var raw = e == null ? string.Empty : (e.Message ?? string.Empty);
            if (qn == null || raw.Length < 8 || !LooksPotential(raw)) return;

            try
            {
                var chat = JsonConvert.DeserializeObject<ChatResponse>(raw);
                var messages = chat == null || chat.result == null
                    ? new List<QNChatMessage>()
                    : chat.result.Where(x => x != null).ToList();

                foreach (var message in messages)
                {
                    if (!MessageLooksPotential(message)) continue;
                    TryOwnPlan(qn, message);
                }
            }
            catch (Exception ex)
            {
                Log.Info("receiveNewMsg 订单模板补全解析失败: length=" + raw.Length
                    + ", error=" + ex.Message);
            }
        }

        private static void TryOwnPlan(QN qn, QNChatMessage message)
        {
            var seller = DirectOrderIdentityResolver.ResolveSeller(qn, message, null);
            var buyer = DirectOrderIdentityResolver.ResolveBuyer(qn, message, null);
            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(buyer)) return;

            OrderPlacedReplyPlan plan;
            if (!OrderPlacedAutoReplyService.TryCreatePlan(
                message,
                BuildMessageText(message),
                seller,
                buyer,
                StartedAt,
                out plan))
            {
                return;
            }

            if (plan == null || plan.IsBuyerFollowUp) return;

            // TryCreatePlan 已执行严格订单证据检查、历史消息保护、OrderEventHub 发布和短期去重。
            // 这里把同一 seller+buyer+order 的发送保留到交易详情查询结束，旧桥接随后只能命中去重。
            OrderPlacedAutoReplyService.Complete(plan, true);
            Log.Info("receiveNewMsg 稀疏订单模板已抢先接管: seller=" + plan.Seller
                + ", buyer=" + plan.Buyer + ", orderId=" + plan.OrderId);

            Task.Run(async () => await EnrichAndSendAsync(qn, plan));
        }

        private static async Task EnrichAndSendAsync(QN qn, OrderPlacedReplyPlan plan)
        {
            try
            {
                using (BotActivityCoordinator.Begin("receiveNewMsg下单交易字段补全", plan.Seller, plan.Buyer))
                {
                    var enriched = !NeedsTradeEnrichment(plan.Config, plan.Snapshot)
                        || await TryEnrichFromTradeApiAsync(qn, plan, plan.Snapshot);

                    if (plan.Snapshot != null)
                    {
                        plan.Snapshot.EventText = BuildSafeEventText(plan.Snapshot);
                        plan.EventText = plan.Snapshot.EventText;
                        plan.EventTime = plan.Snapshot.EventTime;

                        // 复用同一事件键合并保存字段；不新增第二套发送和持久化状态。
                        OrderEventHub.Publish(plan.Snapshot);
                        OrderGuidanceDeliveryGuard.ObserveOrder(plan.Snapshot);
                        qn.EnqueueNewOrderAttention(plan.Snapshot);
                    }

                    Log.Info("receiveNewMsg下单模板渲染前交易详情补全完成: seller=" + plan.Seller
                        + ", buyer=" + plan.Buyer
                        + ", orderId=" + plan.OrderId
                        + ", sku=" + Safe(plan.Snapshot == null ? string.Empty : plan.Snapshot.SkuText, 160)
                        + ", quantity=" + (plan.Snapshot == null || plan.Snapshot.Quantity <= 0
                            ? "<missing>"
                            : plan.Snapshot.Quantity.ToString(CultureInfo.InvariantCulture))
                        + ", paid=" + FormatMoney(plan.Snapshot == null ? null : plan.Snapshot.PaidAmount)
                        + ", total=" + FormatMoney(plan.Snapshot == null ? null : plan.Snapshot.TotalAmount)
                        + ", enriched=" + enriched);

                    await qn.ProcessOrderTemplateTradeDetailPlanAsync(plan);
                }
            }
            catch (Exception ex)
            {
                OrderPlacedAutoReplyService.Complete(plan, false);
                Log.ErrorWithMaxCount("receiveNewMsg 订单交易详情补全及发送失败：" + ex.Message, 10);
            }
        }

        private static bool NeedsTradeEnrichment(AutoReplyRuleConfig cfg, OrderSnapshot snapshot)
        {
            if (cfg == null || snapshot == null) return false;
            var template = cfg.OrderPlacedReplyText ?? string.Empty;
            var httpMode = string.Equals(
                (cfg.OrderPlacedReplyMode ?? string.Empty).Trim(),
                "调用HTTP接口",
                StringComparison.Ordinal);

            var needsSku = template.Contains("{规格}") && string.IsNullOrWhiteSpace(snapshot.SkuText);
            var needsQuantity = template.Contains("{数量}") && snapshot.Quantity <= 0;
            var needsPaid = template.Contains("{实付}") && !snapshot.PaidAmount.HasValue;
            var needsTotal = template.Contains("{金额}") && !snapshot.TotalAmount.HasValue;
            var needsItem = template.Contains("{商品}") && string.IsNullOrWhiteSpace(snapshot.ItemTitle);

            return needsSku || needsQuantity || needsPaid || needsTotal || needsItem
                || (httpMode && IsSparse(snapshot));
        }

        private static bool IsSparse(OrderSnapshot snapshot)
        {
            return snapshot == null
                || string.IsNullOrWhiteSpace(snapshot.SkuText)
                || snapshot.Quantity <= 0
                || (!snapshot.PaidAmount.HasValue && !snapshot.TotalAmount.HasValue);
        }

        private static async Task<bool> TryEnrichFromTradeApiAsync(
            QN qn,
            OrderPlacedReplyPlan plan,
            OrderSnapshot snapshot)
        {
            if (qn == null || plan == null || snapshot == null) return false;

            var securityBuyerUid = GetCachedBuyerSecurityId(plan.Seller, plan.Buyer);
            var delays = new[] { 0, 500, 1000, 2000, 3000, 4000, 5000 };
            Exception lastError = null;

            for (var attempt = 0; attempt < delays.Length; attempt++)
            {
                if (delays[attempt] > 0) await Task.Delay(delays[attempt]);

                try
                {
                    var response = await qn.GetBuyerTrades(securityBuyerUid ?? string.Empty, plan.OrderId);
                    var trade = FindExactTrade(response, plan.OrderId);

                    if (trade == null && string.IsNullOrWhiteSpace(securityBuyerUid))
                    {
                        securityBuyerUid = await ResolveBuyerSecurityIdAsync(qn, plan.Seller, plan.Buyer);
                        if (!string.IsNullOrWhiteSpace(securityBuyerUid))
                        {
                            response = await qn.GetBuyerTrades(securityBuyerUid, plan.OrderId);
                            trade = FindExactTrade(response, plan.OrderId);
                        }
                    }

                    if (trade == null) continue;
                    MergeTrade(snapshot, trade);
                    if (!NeedsTradeEnrichment(plan.Config, snapshot)) return true;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }

            if (lastError != null)
            {
                Log.Info("receiveNewMsg订单交易详情查询未完全成功: orderId=" + plan.OrderId
                    + ", error=" + lastError.Message);
            }
            else
            {
                Log.Info("receiveNewMsg订单交易详情查询暂未返回完整字段: orderId=" + plan.OrderId);
            }
            return !IsSparse(snapshot);
        }

        private static async Task<string> ResolveBuyerSecurityIdAsync(
            QN qn,
            string seller,
            string buyer)
        {
            var cacheKey = BuildBuyerKey(seller, buyer);
            string cached;
            if (BuyerSecurityIds.TryGetValue(cacheKey, out cached)) return cached;

            var response = await qn.SearchBuyerUser(buyer);
            var accounts = response == null || response.Data == null || response.Data.Data == null
                ? new List<Account>()
                : response.Data.Data.Where(x => x != null).ToList();
            if (accounts.Count == 0) return string.Empty;

            var account = accounts.FirstOrDefault(x =>
                    DirectOrderIdentityResolver.IdentityEquals(x.Nick, buyer))
                ?? accounts.FirstOrDefault(x =>
                    string.Equals((x.Nick ?? string.Empty).Trim(), (buyer ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? accounts.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.EncryptAccountId));

            var id = account == null ? string.Empty : (account.EncryptAccountId ?? string.Empty).Trim();
            if (id.Length > 0) BuyerSecurityIds[cacheKey] = id;
            return id;
        }

        private static string GetCachedBuyerSecurityId(string seller, string buyer)
        {
            string value;
            return BuyerSecurityIds.TryGetValue(BuildBuyerKey(seller, buyer), out value)
                ? value
                : string.Empty;
        }

        private static ZnkfTrade FindExactTrade(ZnkfTradeQueryResponse response, string orderId)
        {
            var orders = response == null || response.data == null || response.data.orders == null
                ? new List<ZnkfTrade>()
                : response.data.orders.Where(x => x != null).ToList();
            if (orders.Count == 0) return null;

            var normalizedOrderId = DigitsOnly(orderId, 8, 40);
            var exact = orders.FirstOrDefault(x =>
                string.Equals(DigitsOnly(x.bizOrderId, 8, 40), normalizedOrderId, StringComparison.Ordinal));
            if (exact != null) return exact;

            exact = orders.FirstOrDefault(x => (x.itemList ?? new List<ZnkfTradeItem>()).Any(item =>
                item != null && (
                    string.Equals(DigitsOnly(item.bizOrderId, 8, 40), normalizedOrderId, StringComparison.Ordinal)
                    || string.Equals(DigitsOnly(item.subOrderId, 8, 40), normalizedOrderId, StringComparison.Ordinal))));
            if (exact != null) return exact;

            return orders.Count == 1 ? orders[0] : null;
        }

        private static void MergeTrade(OrderSnapshot snapshot, ZnkfTrade trade)
        {
            if (snapshot == null || trade == null) return;
            var items = (trade.itemList ?? new List<ZnkfTradeItem>())
                .Where(x => x != null)
                .ToList();

            var sku = string.Join("；", items
                .Select(x => NormalizeSku(x.sku))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(snapshot.SkuText) && sku.Length > 0)
            {
                snapshot.SkuText = Safe(sku, 240);
            }

            var quantity = items.Sum(x => x.buyAmount > 0 ? x.buyAmount : Math.Max(0, x.buyerAmount));
            if (quantity <= 0) quantity = Math.Max(0, trade.buyAmount);
            if (snapshot.Quantity <= 0 && quantity > 0) snapshot.Quantity = quantity;

            var title = string.Join("；", items
                .Select(x => Safe(x.auctionTitle, 180))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(snapshot.ItemTitle) && title.Length > 0)
            {
                snapshot.ItemTitle = Safe(title, 300);
            }

            var first = items.FirstOrDefault();
            if (first != null)
            {
                if (string.IsNullOrWhiteSpace(snapshot.ItemId)) snapshot.ItemId = Safe(first.auctionId, 64);
                if (string.IsNullOrWhiteSpace(snapshot.ProductUrl)) snapshot.ProductUrl = Safe(first.auctionUrl, 500);
                if (string.IsNullOrWhiteSpace(snapshot.ImageUrl)) snapshot.ImageUrl = Safe(first.picUrl, 500);
            }

            var total = ParseMoney(trade.orderPrice);
            if (!total.HasValue)
            {
                decimal sum = 0m;
                var hasPrice = false;
                foreach (var item in items)
                {
                    var price = ParseMoney(FirstNonEmpty(item.price, item.auctionPrice));
                    var count = item.buyAmount > 0 ? item.buyAmount : Math.Max(1, item.buyerAmount);
                    if (!price.HasValue) continue;
                    sum += price.Value * count;
                    hasPrice = true;
                }
                if (hasPrice) total = decimal.Round(sum, 2);
            }

            if (!snapshot.TotalAmount.HasValue && total.HasValue) snapshot.TotalAmount = total;

            var itemPayTime = items
                .Where(x => x.payTime.HasValue)
                .Select(x => x.payTime)
                .FirstOrDefault();
            var paidAt = trade.payTime ?? itemPayTime;
            var paid = paidAt.HasValue
                || snapshot.EventType == OrderEventType.Paid
                || snapshot.IsPaid == true;
            if (paid && !snapshot.PaidAmount.HasValue && total.HasValue)
            {
                snapshot.PaidAmount = total;
            }
            if (paidAt.HasValue)
            {
                snapshot.PaidAt = paidAt;
                snapshot.IsPaid = true;
                snapshot.TradeStatus = "已付款";
            }
            else if (string.IsNullOrWhiteSpace(snapshot.TradeStatus))
            {
                snapshot.TradeStatus = paid ? "已付款" : "新下单";
            }
        }

        private static bool MessageLooksPotential(QNChatMessage message)
        {
            if (message == null) return false;
            try { return LooksPotential(JObject.FromObject(message).ToString(Formatting.None)); }
            catch { return LooksPotential(BuildMessageText(message)); }
        }

        private static bool LooksPotential(string raw)
        {
            raw = raw ?? string.Empty;
            if (raw.Length < 8) return false;
            var lower = raw.ToLowerInvariant();
            var cue = raw.Contains("订单") || raw.Contains("下单") || raw.Contains("已付款")
                || raw.Contains("支付成功") || raw.Contains("待发货") || raw.Contains("交易时间")
                || (raw.Contains("件商品") && raw.Contains("合计"))
                || lower.Contains("orderid") || lower.Contains("bizorderid")
                || lower.Contains("tradeid") || lower.Contains("wait_seller_send_goods");
            if (!cue) return false;
            return OrderIdTextRegex.IsMatch(raw)
                || Regex.IsMatch(lower,
                    "(?:orderid|bizorderid|mainorderid|suborderid|tradeid|\\\"tid\\\")\\s*[\\\"']?\\s*[:=]\\s*[\\\"']?\\d{8,}");
        }

        private static string BuildMessageText(QNChatMessage message)
        {
            if (message == null) return string.Empty;
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(message.summary)) parts.Add(message.summary.Trim());
            if (message.originalData != null && !string.IsNullOrWhiteSpace(message.originalData.text))
            {
                parts.Add(message.originalData.text.Trim());
            }
            return Regex.Replace(string.Join(" ", parts.Distinct(StringComparer.OrdinalIgnoreCase)), @"\s+", " ").Trim();
        }

        private static string BuildSafeEventText(OrderSnapshot snapshot)
        {
            if (snapshot == null) return string.Empty;
            var sb = new StringBuilder();
            sb.Append(snapshot.EventType == OrderEventType.Paid ? "买家已付款 " : "买家已下单 ");
            sb.Append("订单号：").Append(snapshot.OrderId);
            if (!string.IsNullOrWhiteSpace(snapshot.ItemTitle)) sb.Append(" 商品：").Append(Safe(snapshot.ItemTitle, 220));
            if (!string.IsNullOrWhiteSpace(snapshot.SkuText)) sb.Append(" 规格：").Append(Safe(snapshot.SkuText, 180));
            if (snapshot.Quantity > 0) sb.Append(" 数量：").Append(snapshot.Quantity);
            if (snapshot.PaidAmount.HasValue) sb.Append(" 实付：").Append(snapshot.PaidAmount.Value.ToString("0.00", CultureInfo.InvariantCulture));
            else if (snapshot.TotalAmount.HasValue) sb.Append(" 金额：").Append(snapshot.TotalAmount.Value.ToString("0.00", CultureInfo.InvariantCulture));
            sb.Append(" 订单状态：").Append(string.IsNullOrWhiteSpace(snapshot.TradeStatus)
                ? (snapshot.EventType == OrderEventType.Paid ? "已付款" : "新下单")
                : snapshot.TradeStatus);
            return sb.ToString();
        }

        private static string NormalizeSku(string value)
        {
            value = Regex.Replace((value ?? string.Empty).Replace('：', ':'), @"\s+", " ").Trim();
            if (value.Length == 0) return string.Empty;
            if (!value.Contains(":"))
            {
                var known = Regex.Match(value,
                    @"^(专辑名称|套餐名称|套餐|期限|时长|会员类型)\s*(.+)$",
                    RegexOptions.IgnoreCase);
                if (known.Success)
                {
                    value = known.Groups[1].Value.Trim() + ":" + known.Groups[2].Value.Trim();
                }
            }
            return Safe(value, 240);
        }

        private static decimal? ParseMoney(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            decimal amount;
            var match = Regex.Match(value.Replace(",", string.Empty), @"-?\d+(?:\.\d{1,4})?");
            if (!match.Success
                || !decimal.TryParse(match.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out amount)
                || amount < 0
                || amount > 100000000m)
            {
                return null;
            }
            return decimal.Round(amount, 2);
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return (values ?? new string[0]).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
        }

        private static string DigitsOnly(string value, int min, int max)
        {
            var digits = Regex.Replace(value ?? string.Empty, @"\D", string.Empty);
            return digits.Length >= min && digits.Length <= max ? digits : string.Empty;
        }

        private static string BuildBuyerKey(string seller, string buyer)
        {
            return NormalizeIdentity(seller) + "#" + NormalizeIdentity(buyer);
        }

        private static string NormalizeIdentity(string value)
        {
            value = Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", string.Empty);
            if (value.StartsWith("cntaobao", StringComparison.Ordinal)) value = value.Substring("cntaobao".Length);
            return value;
        }

        private static string Safe(string value, int max)
        {
            value = Regex.Replace((value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim(), @"\s+", " ");
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }

        private static string FormatMoney(decimal? value)
        {
            return value.HasValue ? value.Value.ToString("0.00", CultureInfo.InvariantCulture) : "<missing>";
        }
    }
}
