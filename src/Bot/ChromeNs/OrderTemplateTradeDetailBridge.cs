using Bot.Options;
using BotLib;
using DbEntity;
using DbEntity.Response;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bot
{
    /// <summary>
    /// 在 App 构造函数启动旧订单桥接之前启动交易详情补全桥接。
    /// 千牛 messageCenterNotify 常常只带订单号、买家和状态，右侧订单面板中的
    /// SKU、数量、实付来自后续交易查询；本桥接负责在模板渲染前补齐这些字段。
    /// </summary>
    public partial class App
    {
        private static readonly object OrderTemplateTradeDetailBootstrap =
            ChromeNs.OrderTemplateTradeDetailBridge.InitializeForApp();
    }
}

namespace Bot.ChromeNs
{
    internal static class OrderTemplateTradeDetailBridge
    {
        private sealed class FlatValue
        {
            public string Path;
            public string Key;
            public string Value;
        }

        private sealed class PendingOrder
        {
            public string Key;
            public OrderSnapshot Snapshot;
            public AutoReplyRuleConfig Config;
            public DateTime Until;
        }

        private static readonly ConcurrentDictionary<QN, byte> Attached =
            new ConcurrentDictionary<QN, byte>();
        private static readonly ConcurrentDictionary<string, PendingOrder> Pending =
            new ConcurrentDictionary<string, PendingOrder>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, string> BuyerSecurityIds =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        private static Timer _timer;
        private static int _initialized;

        private static readonly Regex EventCueRegex = new Regex(
            "买家已下单|下单成功|订单创建成功|已成功下单|买家已付款|付款成功|支付成功|待卖家发货|卖家待发货|等待买家付款|已付款|WAIT_SELLER_SEND_GOODS|WAIT_BUYER_PAY|TRADE_CREATED|TRADE_PAID|TRADE_FINISHED",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex OrderIdTextRegex = new Regex(
            @"(?:订单号|订单编号|主订单号|子订单号|交易号|订单)\s*[:：#]?\s*(\d{8,})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly string[] OrderIdKeys =
        {
            "orderid", "bizorderid", "mainorderid", "suborderid", "tradeid", "tid",
            "biztradeid", "parentorderid", "bizordercode", "tradecode"
        };

        private static readonly string[] BuyerKeys =
        {
            "buyernick", "buyernickname", "buyername", "buyerid", "buyeruid",
            "customernick", "customername", "customerid", "customeruid",
            "contactnick", "contactname", "contactid", "conversationnick", "conversationname",
            "oppositenick", "oppositename", "peernick", "peername",
            "sendernick", "sendername", "fromnick", "fromname", "usernick", "username"
        };

        private static readonly string[] ItemTitleKeys =
        {
            "itemtitle", "auctiontitle", "producttitle", "goodstitle", "itemname",
            "productname", "subject", "auctionname", "goodsname"
        };

        private static readonly string[] ItemIdKeys =
        {
            "itemid", "numiid", "auctionid", "productid", "goodsid"
        };

        private static readonly string[] SkuIdKeys =
        {
            "skuid", "skuidstr", "skuidentifier", "sku_id", "subitemid"
        };

        private static readonly string[] SkuTextKeys =
        {
            "skutext", "skuname", "skutitle", "skuinfo", "skudesc", "skudescription",
            "skupropertiesname", "propertiesname", "spec", "specification", "specinfo",
            "salesproperties", "auctionprops", "outername", "sku"
        };

        private static readonly string[] QuantityKeys =
        {
            "quantity", "num", "buynum", "buyamount", "buyquantity", "itemcount",
            "itemnum", "goodsnum", "productcount", "orderquantity", "count"
        };

        private static readonly string[] TotalAmountKeys =
        {
            "totalamount", "totalfee", "orderamount", "ordertotal", "totalprice",
            "totalpayment", "shouldpay", "payableamount", "amount", "price"
        };

        private static readonly string[] PaidAmountKeys =
        {
            "paidamount", "payment", "payamount", "actualfee", "actualpay", "actualpayment",
            "paidfee", "realpay", "realpayment", "actualamount", "receivedamount",
            "receiveamount", "buyerpaid", "buyerpayment"
        };

        private static readonly string[] StatusKeys =
        {
            "tradestatus", "orderstatus", "paystatus", "status", "statustext", "trade_status"
        };

        private static readonly string[] TimeKeys =
        {
            "paidat", "paytime", "paidtime", "paymenttime", "createdat", "createtime",
            "ordertime", "createdtime", "tradecreatetime", "eventtime", "sendtime", "timestamp"
        };

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                // 10ms 轮询确保先于旧的 100ms/750ms 订单桥接订阅；附加后处理本身不忙等。
                _timer = new Timer(_ => Attach(), null, 0, 10);
            }
            return new object();
        }

        private static void Attach()
        {
            try
            {
                CleanupPending();
                foreach (var qn in QN.GetRuntimeSafetySnapshot())
                {
                    if (qn == null || !Attached.TryAdd(qn, 0)) continue;
                    qn.EvMessageNotity += OnMessageNotify;
                    Log.Info("订单模板交易详情补全桥接已绑定: seller="
                        + (qn.Seller == null ? string.Empty : qn.Seller.Nick));
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("绑定订单模板交易详情补全桥接失败：" + ex.Message, 10);
            }
        }

        private static void OnMessageNotify(object sender, MessageNotifyEventArgs e)
        {
            var qn = sender as QN;
            var raw = e == null ? string.Empty : (e.NotifyContent ?? string.Empty);
            if (qn == null || raw.Length < 8) return;

            try
            {
                OrderSnapshot snapshot;
                if (!TryParseSparseSnapshot(qn, raw, out snapshot)) return;

                var cfg = BotFeatureStore.GetAutoReplyRules();
                if (cfg == null || !cfg.EnableOrderPlacedReply || !Params.Robot.CanUseRobotReal) return;
                if (!NeedsTradeEnrichment(cfg, snapshot)) return;

                var key = BuildOrderKey(snapshot.Seller, snapshot.Buyer, snapshot.OrderId);
                PendingOrder existing;
                if (Pending.TryGetValue(key, out existing) && existing != null)
                {
                    MergeLiveEvent(existing.Snapshot, snapshot);
                    // 抢先登记该状态事件，防止后续旧桥接把付款事件作为另一条稀疏计划发送。
                    OrderEventHub.Publish(snapshot);
                    Log.Info("订单模板补全已合并后续状态事件: orderId=" + snapshot.OrderId
                        + ", event=" + snapshot.EventType);
                    return;
                }

                var pending = new PendingOrder
                {
                    Key = key,
                    Snapshot = snapshot,
                    Config = cfg,
                    Until = DateTime.Now.AddHours(Math.Max(1, Math.Min(720, cfg.OrderPlacedDedupHours)))
                };
                if (!Pending.TryAdd(key, pending)) return;

                // 在任何 await 之前先占有 OrderEventHub。旧的稀疏桥接随后看到相同事件会直接去重，
                // 不会先把空的 {规格}/{数量}/{实付} 发送出去。
                var published = OrderEventHub.Publish(snapshot);
                if (!published.Accepted)
                {
                    PendingOrder ignored;
                    Pending.TryRemove(key, out ignored);
                    return;
                }

                OrderGuidanceDeliveryGuard.ObserveOrder(snapshot);
                var plan = BuildPlan(snapshot, cfg, key);

                // 先使用现有服务的去重表占位，阻止“下单”和“付款”两个状态各发送一次。
                // 真正发送仍由 ProcessOrderPlacedReplyAsync 完成并覆盖最终成功/失败状态。
                OrderPlacedAutoReplyService.Complete(plan, true);

                Task.Run(async () => await EnrichAndSendAsync(qn, pending, plan));
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("接管稀疏订单模板事件失败：" + ex.Message, 10);
            }
        }

        private static async Task EnrichAndSendAsync(
            QN qn,
            PendingOrder pending,
            OrderPlacedReplyPlan plan)
        {
            try
            {
                using (BotActivityCoordinator.Begin("下单交易字段补全", plan.Seller, plan.Buyer))
                {
                    var enriched = await TryEnrichFromTradeApiAsync(qn, plan, pending.Snapshot);
                    pending.Snapshot.EventText = BuildSafeEventText(pending.Snapshot);
                    plan.EventText = pending.Snapshot.EventText;
                    plan.EventTime = pending.Snapshot.EventTime;
                    plan.Snapshot = pending.Snapshot;

                    // 保存补全后的字段到已有订单事件状态；Publish 会命中同一事件并执行 Merge/Save。
                    OrderEventHub.Publish(pending.Snapshot);
                    OrderGuidanceDeliveryGuard.ObserveOrder(pending.Snapshot);
                    qn.EnqueueNewOrderAttention(pending.Snapshot);

                    Log.Info("下单模板渲染前交易详情补全完成: seller=" + plan.Seller
                        + ", buyer=" + plan.Buyer
                        + ", orderId=" + plan.OrderId
                        + ", sku=" + Safe(pending.Snapshot.SkuText, 160)
                        + ", quantity=" + (pending.Snapshot.Quantity <= 0
                            ? "<missing>"
                            : pending.Snapshot.Quantity.ToString(CultureInfo.InvariantCulture))
                        + ", paid=" + FormatMoney(pending.Snapshot.PaidAmount)
                        + ", total=" + FormatMoney(pending.Snapshot.TotalAmount)
                        + ", enriched=" + enriched);

                    await qn.ProcessOrderTemplateTradeDetailPlanAsync(plan);
                }
            }
            catch (Exception ex)
            {
                OrderPlacedAutoReplyService.Complete(plan, false);
                PendingOrder ignored;
                Pending.TryRemove(pending.Key, out ignored);
                Log.ErrorWithMaxCount("订单交易详情补全及发送失败：" + ex.Message, 10);
            }
        }

        private static OrderPlacedReplyPlan BuildPlan(
            OrderSnapshot snapshot,
            AutoReplyRuleConfig cfg,
            string key)
        {
            return new OrderPlacedReplyPlan
            {
                Seller = snapshot.Seller,
                Buyer = snapshot.Buyer,
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
                    ZnkfTradeQueryResponse response = null;

                    // 部分千牛版本允许只按 bizOrderId 查询；先走这个最快路径。
                    response = await qn.GetBuyerTrades(securityBuyerUid ?? string.Empty, plan.OrderId);
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
                Log.Info("订单交易详情查询未完全成功: orderId=" + plan.OrderId
                    + ", error=" + lastError.Message);
            }
            else
            {
                Log.Info("订单交易详情查询暂未返回完整字段: orderId=" + plan.OrderId);
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

            var paid = trade.payTime.HasValue
                || snapshot.EventType == OrderEventType.Paid
                || snapshot.IsPaid == true;
            if (paid && !snapshot.PaidAmount.HasValue && total.HasValue)
            {
                snapshot.PaidAmount = total;
            }
            if (trade.payTime.HasValue)
            {
                snapshot.PaidAt = trade.payTime;
                snapshot.IsPaid = true;
                snapshot.TradeStatus = "已付款";
            }
            else if (string.IsNullOrWhiteSpace(snapshot.TradeStatus))
            {
                snapshot.TradeStatus = paid ? "已付款" : "新下单";
            }
        }

        private static bool TryParseSparseSnapshot(QN qn, string raw, out OrderSnapshot snapshot)
        {
            snapshot = null;
            var root = ParseExpanded(raw);
            var flat = Flatten(root);
            var combined = BuildCombinedText(raw, flat);
            if (!EventCueRegex.IsMatch(combined)) return false;

            var orderId = ResolveOrderId(flat, combined);
            if (string.IsNullOrWhiteSpace(orderId)) return false;

            var seller = qn.Seller == null ? string.Empty : (qn.Seller.Nick ?? string.Empty).Trim();
            var buyer = ResolveBuyer(flat, seller, orderId);
            if (seller.Length == 0 || buyer.Length == 0) return false;

            var status = FirstNonEmpty(FindValue(flat, StatusKeys), ExtractStatus(combined));
            var paid = ResolvePaidState(combined, status);
            var eventType = ResolveEventType(combined, status, paid);
            var eventTime = ParseDate(FindValue(flat, TimeKeys)) ?? DateTime.Now;

            var sku = NormalizeSku(FindValue(flat, SkuTextKeys));
            var quantity = ParsePositiveInt(FindValue(flat, QuantityKeys));
            var total = ParseMoney(FindValue(flat, TotalAmountKeys));
            var paidAmount = ParseMoney(FindValue(flat, PaidAmountKeys));
            if (eventType == OrderEventType.Paid && !paidAmount.HasValue && total.HasValue)
            {
                paidAmount = total;
            }

            snapshot = new OrderSnapshot
            {
                Seller = seller,
                Buyer = buyer,
                OrderId = orderId,
                ItemId = Safe(FindValue(flat, ItemIdKeys), 64),
                ItemTitle = Safe(FindValue(flat, ItemTitleKeys), 300),
                SkuId = Safe(FindValue(flat, SkuIdKeys), 100),
                SkuText = Safe(sku, 240),
                Quantity = quantity,
                TotalAmount = total,
                PaidAmount = paidAmount,
                TradeStatus = Safe(status, 120),
                IsPaid = paid,
                CreatedAt = eventType == OrderEventType.Created ? (DateTime?)eventTime : null,
                PaidAt = eventType == OrderEventType.Paid ? (DateTime?)eventTime : null,
                Source = "messageCenterNotify交易详情补全入口",
                RawCardHash = Hash(raw),
                DetectedAt = DateTime.Now,
                EventTime = eventTime,
                EventType = eventType
            };
            snapshot.EventText = BuildSafeEventText(snapshot);
            return true;
        }

        private static JToken ParseExpanded(string raw)
        {
            JToken token;
            try { token = JToken.Parse(raw); }
            catch { return new JValue(raw ?? string.Empty); }

            for (var i = 0; i < 5 && token.Type == JTokenType.String; i++)
            {
                var nested = token.ToString().Trim();
                if (!LooksLikeJson(nested)) break;
                try { token = JToken.Parse(nested); }
                catch { break; }
            }
            return token;
        }

        private static List<FlatValue> Flatten(JToken root)
        {
            var output = new List<FlatValue>();
            Walk(root, string.Empty, output, 0);
            return output;
        }

        private static void Walk(JToken token, string path, List<FlatValue> output, int depth)
        {
            if (token == null || depth > 18 || output.Count >= 1800) return;
            if (token.Type == JTokenType.Object)
            {
                foreach (var property in ((JObject)token).Properties())
                {
                    Walk(property.Value,
                        path.Length == 0 ? property.Name : path + "." + property.Name,
                        output,
                        depth + 1);
                    if (output.Count >= 1800) break;
                }
                return;
            }
            if (token.Type == JTokenType.Array)
            {
                var index = 0;
                foreach (var child in (JArray)token)
                {
                    Walk(child, path + "[" + index + "]", output, depth + 1);
                    if (++index >= 180 || output.Count >= 1800) break;
                }
                return;
            }
            if (token.Type == JTokenType.Null || token.Type == JTokenType.Undefined) return;

            var value = token.ToString().Trim();
            if (value.Length == 0 || value.Length > 16000) return;
            var key = path;
            var dot = key.LastIndexOf('.');
            if (dot >= 0) key = key.Substring(dot + 1);
            var bracket = key.IndexOf('[');
            if (bracket >= 0) key = key.Substring(0, bracket);
            output.Add(new FlatValue
            {
                Path = path,
                Key = key,
                Value = value.Length > 4000 ? value.Substring(0, 4000) : value
            });

            if (token.Type == JTokenType.String && depth < 16 && LooksLikeJson(value))
            {
                try { Walk(JToken.Parse(value), path + ".json", output, depth + 1); }
                catch { }
            }
        }

        private static string BuildCombinedText(string raw, IEnumerable<FlatValue> flat)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(raw) && raw.Length <= 12000) parts.Add(raw);
            parts.AddRange((flat ?? new List<FlatValue>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Value) && x.Value.Length <= 1200)
                .Select(x => x.Value.Trim())
                .Take(500));
            return Regex.Replace(string.Join(" ", parts.Distinct(StringComparer.OrdinalIgnoreCase)), @"\s+", " ").Trim();
        }

        private static string ResolveOrderId(IList<FlatValue> flat, string combined)
        {
            var value = FindValue(flat, OrderIdKeys);
            var digits = DigitsOnly(value, 8, 40);
            if (digits.Length > 0) return digits;
            var match = OrderIdTextRegex.Match(combined ?? string.Empty);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        private static string ResolveBuyer(IList<FlatValue> flat, string seller, string orderId)
        {
            var best = string.Empty;
            var bestScore = 0;
            foreach (var item in flat ?? new List<FlatValue>())
            {
                if (item == null) continue;
                var value = (item.Value ?? string.Empty).Trim();
                if (!UsableBuyer(value, seller, orderId)) continue;

                var key = NormalizeKey(item.Key);
                var path = NormalizeKey(item.Path);
                var score = 0;
                if (BuyerKeys.Contains(key)) score += 100;
                if (path.Contains("buyer")) score += 90;
                if (path.Contains("customer")) score += 85;
                if (path.Contains("contact")) score += 75;
                if (path.Contains("opposite") || path.Contains("peer")) score += 70;
                if (path.Contains("conversation")) score += 65;
                if (path.Contains("sender") || path.Contains("from")) score += 45;
                if (key.EndsWith("nick") || key.EndsWith("name")) score += 25;
                if (score <= bestScore) continue;
                bestScore = score;
                best = value;
            }
            return bestScore >= 55 ? best : string.Empty;
        }

        private static bool UsableBuyer(string value, string seller, string orderId)
        {
            if (value.Length < 2 || value.Length > 180) return false;
            if (DirectOrderIdentityResolver.IdentityEquals(value, seller)) return false;
            if (string.Equals(DigitsOnly(value, 8, 40), orderId, StringComparison.Ordinal)) return false;
            if (value.Contains("订单") || value.Contains("付款") || value.Contains("商品") || value.Contains("金额")) return false;
            if (value.StartsWith("{") || value.StartsWith("[")) return false;
            return !Regex.IsMatch(value, @"^\d{16,}$");
        }

        private static string FindValue(IList<FlatValue> flat, IEnumerable<string> aliases)
        {
            var set = new HashSet<string>((aliases ?? new string[0]).Select(NormalizeKey), StringComparer.OrdinalIgnoreCase);
            foreach (var item in flat ?? new List<FlatValue>())
            {
                if (item != null && !string.IsNullOrWhiteSpace(item.Value)
                    && set.Contains(NormalizeKey(item.Key)))
                {
                    return item.Value.Trim();
                }
            }
            foreach (var item in flat ?? new List<FlatValue>())
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Value)) continue;
                var path = NormalizeKey(item.Path);
                if (set.Any(alias => path.EndsWith(alias, StringComparison.OrdinalIgnoreCase)))
                {
                    return item.Value.Trim();
                }
            }
            return string.Empty;
        }

        private static string NormalizeSku(string value)
        {
            value = Safe(value, 500).Replace('：', ':');
            value = Regex.Replace(value, @"^(?:SKU|规格名称|规格|销售属性|套餐|属性)\s*[:：]\s*", string.Empty, RegexOptions.IgnoreCase);
            value = Regex.Replace(value, @"\s*:\s*", ":");
            if (!value.Contains(":"))
            {
                var known = Regex.Match(
                    value,
                    @"^(专辑名称|套餐名称|套餐|期限|时长|会员类型|充值类型|账号类型|商品规格|版本)\s*(.+)$");
                if (known.Success && known.Groups[2].Value.Trim().Length > 0)
                {
                    value = known.Groups[1].Value.Trim() + ":" + known.Groups[2].Value.Trim();
                }
            }
            return value;
        }

        private static int ParsePositiveInt(string value)
        {
            int result;
            var match = Regex.Match(value ?? string.Empty, @"\d+");
            return match.Success && int.TryParse(match.Value, out result)
                && result > 0 && result <= 10000
                ? result
                : 0;
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

        private static DateTime? ParseDate(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            long raw;
            if (long.TryParse(value.Trim(), out raw))
            {
                try
                {
                    if (raw > 1000000000000000L) return DateTimeOffset.FromUnixTimeMilliseconds(raw / 1000L).LocalDateTime;
                    if (raw > 100000000000L) return DateTimeOffset.FromUnixTimeMilliseconds(raw).LocalDateTime;
                    if (raw > 1000000000L) return DateTimeOffset.FromUnixTimeSeconds(raw).LocalDateTime;
                }
                catch { }
            }
            DateTimeOffset dto;
            if (DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out dto)
                || DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dto))
            {
                return dto.LocalDateTime;
            }
            return null;
        }

        private static string ExtractStatus(string text)
        {
            var known = new[]
            {
                "已付款", "待卖家发货", "等待买家付款", "未付款", "交易成功",
                "订单关闭", "退款中", "买家已下单", "订单创建成功", "新下单"
            };
            return known.FirstOrDefault(x =>
                (text ?? string.Empty).IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0) ?? string.Empty;
        }

        private static bool? ResolvePaidState(string text, string status)
        {
            var value = (text ?? string.Empty) + " " + (status ?? string.Empty);
            if (Regex.IsMatch(value, "未付款|等待买家付款|待付款|付款关闭|交易关闭", RegexOptions.IgnoreCase)) return false;
            if (Regex.IsMatch(value, "已付款|付款成功|支付成功|交易成功|已支付|待卖家发货|WAIT_SELLER_SEND_GOODS|TRADE_PAID", RegexOptions.IgnoreCase)) return true;
            return null;
        }

        private static OrderEventType ResolveEventType(string text, string status, bool? paid)
        {
            var value = (text ?? string.Empty) + " " + (status ?? string.Empty);
            if (Regex.IsMatch(value, "申请退款|退款中|退货退款|仅退款", RegexOptions.IgnoreCase)) return OrderEventType.RefundRequested;
            if (Regex.IsMatch(value, "订单关闭|交易关闭|已关闭|已取消", RegexOptions.IgnoreCase)) return OrderEventType.Closed;
            return paid == true ? OrderEventType.Paid : OrderEventType.Created;
        }

        private static void MergeLiveEvent(OrderSnapshot target, OrderSnapshot incoming)
        {
            if (target == null || incoming == null) return;
            if (string.IsNullOrWhiteSpace(target.SkuText)) target.SkuText = incoming.SkuText;
            if (target.Quantity <= 0) target.Quantity = incoming.Quantity;
            if (!target.TotalAmount.HasValue) target.TotalAmount = incoming.TotalAmount;
            if (!target.PaidAmount.HasValue) target.PaidAmount = incoming.PaidAmount;
            if (incoming.IsPaid == true)
            {
                target.IsPaid = true;
                target.PaidAt = incoming.PaidAt ?? (DateTime?)incoming.EventTime;
                target.TradeStatus = string.IsNullOrWhiteSpace(incoming.TradeStatus) ? "已付款" : incoming.TradeStatus;
            }
            if (incoming.EventTime > target.EventTime) target.EventTime = incoming.EventTime;
        }

        private static string BuildSafeEventText(OrderSnapshot snapshot)
        {
            var sb = new StringBuilder();
            sb.Append(snapshot.IsPaid == true || snapshot.EventType == OrderEventType.Paid
                ? "买家已付款 "
                : "买家已下单 ");
            sb.Append("订单号：").Append(snapshot.OrderId);
            if (!string.IsNullOrWhiteSpace(snapshot.ItemTitle)) sb.Append(" 商品：").Append(Safe(snapshot.ItemTitle, 220));
            if (!string.IsNullOrWhiteSpace(snapshot.SkuText)) sb.Append(" 规格：").Append(Safe(snapshot.SkuText, 180));
            if (snapshot.Quantity > 0) sb.Append(" 数量：").Append(snapshot.Quantity);
            if (snapshot.PaidAmount.HasValue) sb.Append(" 实付：").Append(snapshot.PaidAmount.Value.ToString("0.00", CultureInfo.InvariantCulture));
            else if (snapshot.TotalAmount.HasValue) sb.Append(" 金额：").Append(snapshot.TotalAmount.Value.ToString("0.00", CultureInfo.InvariantCulture));
            sb.Append(" 订单状态：").Append(string.IsNullOrWhiteSpace(snapshot.TradeStatus)
                ? (snapshot.IsPaid == true ? "已付款" : "新下单")
                : snapshot.TradeStatus);
            return sb.ToString();
        }

        private static bool LooksLikeJson(string value)
        {
            value = (value ?? string.Empty).Trim();
            return (value.StartsWith("{") && value.EndsWith("}"))
                || (value.StartsWith("[") && value.EndsWith("]"));
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

        private static string NormalizeKey(string value)
        {
            return Regex.Replace((value ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9]", string.Empty);
        }

        private static string NormalizeIdentity(string value)
        {
            value = Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", string.Empty);
            if (value.StartsWith("cntaobao", StringComparison.Ordinal)) value = value.Substring("cntaobao".Length);
            return value;
        }

        private static string BuildOrderKey(string seller, string buyer, string orderId)
        {
            return NormalizeIdentity(seller) + "#" + NormalizeIdentity(buyer) + "#" + (orderId ?? string.Empty).Trim();
        }

        private static string BuildBuyerKey(string seller, string buyer)
        {
            return NormalizeIdentity(seller) + "#" + NormalizeIdentity(buyer);
        }

        private static string Hash(string value)
        {
            try
            {
                using (var sha = SHA256.Create())
                {
                    return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)))
                        .Replace("-", string.Empty).ToLowerInvariant();
                }
            }
            catch { return string.Empty; }
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

        private static void CleanupPending()
        {
            var now = DateTime.Now;
            foreach (var pair in Pending)
            {
                if (pair.Value != null && pair.Value.Until >= now) continue;
                PendingOrder ignored;
                Pending.TryRemove(pair.Key, out ignored);
            }
        }
    }

    public partial class QN
    {
        /// <summary>
        /// 新桥接已在 OrderEventHub 中抢先登记事件；这里直接复用原有模板渲染、
        /// 人工操作保护、可靠发送、日志与 UI 状态更新流程。
        /// </summary>
        internal Task ProcessOrderTemplateTradeDetailPlanAsync(OrderPlacedReplyPlan plan)
        {
            return ProcessOrderPlacedReplyAsync(plan);
        }
    }
}
