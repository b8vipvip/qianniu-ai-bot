using Bot.ChatRecord;
using BotLib;
using DbEntity;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    /// <summary>
    /// 千牛的“买家直接下单/付款”不一定是普通 buyer -> seller 聊天消息：
    /// 可能以系统订单卡片或 messageCenterNotify 到达。
    /// 旧链路先执行 IsBuyerMessage，导致这类事件在订单解析前被丢弃。
    /// 本桥接器直接订阅 QN 已公开的原始事件，在普通消息角色判断之前识别订单。
    /// </summary>
    internal static class DirectOrderEventBridge
    {
        private sealed class FlatValue
        {
            public string Path;
            public string Key;
            public string Value;
        }

        private sealed class NotificationEnvelope
        {
            public string Seller;
            public string Buyer;
            public string OrderId;
            public string Text;
        }

        private static readonly object Sync = new object();
        private static readonly HashSet<QN> Attached = new HashSet<QN>();
        private static readonly ConcurrentDictionary<string, DateTime> RawEventReservations =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private static Timer _attachTimer;
        private static bool _initialized;

        private static readonly Regex OrderIdTextRegex = new Regex(
            @"(?:订单号|订单编号|主订单号|子订单号|交易号|订单)\s*[:：#]?\s*(\d{8,})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly string[] OrderIdKeys =
        {
            "orderid", "bizorderid", "mainorderid", "suborderid", "tradeid", "tid", "biztradeid"
        };

        private static readonly string[] BuyerKeys =
        {
            "buyernick", "buyernickname", "buyername", "buyerid", "buyeruid",
            "contactnick", "contactname", "conversationnick", "usernick", "usernickname"
        };

        private static readonly string[] SellerKeys =
        {
            "sellernick", "sellernickname", "sellername", "loginid", "shopnick", "shopname"
        };

        private static readonly string[] ItemTitleKeys =
        {
            "itemtitle", "auctiontitle", "producttitle", "goodstitle", "itemname", "productname", "subject"
        };

        private static readonly string[] SkuTextKeys =
        {
            "skutext", "skuname", "skupropertiesname", "propertiesname", "spec", "specification", "skudesc"
        };

        private static readonly string[] AmountKeys =
        {
            "paidamount", "payment", "actualfee", "paidfee", "realpay", "actualamount",
            "totalamount", "totalfee", "orderamount", "amount", "totalprice"
        };

        public static void Initialize()
        {
            lock (Sync)
            {
                if (_initialized) return;
                _initialized = true;
                _attachTimer = new Timer(_ => AttachCurrentQnInstances(), null, 0, 750);
            }
        }

        private static void AttachCurrentQnInstances()
        {
            QN[] qns;
            try
            {
                qns = QN.QNSet == null ? new QN[0] : QN.QNSet.Where(x => x != null).ToArray();
            }
            catch
            {
                return;
            }

            foreach (var qn in qns)
            {
                var attach = false;
                lock (Sync)
                {
                    if (!Attached.Contains(qn))
                    {
                        Attached.Add(qn);
                        attach = true;
                    }
                }
                if (!attach) continue;

                qn.EvRecieveNewMessage += OnReceiveNewMessage;
                qn.EvMessageNotity += OnMessageCenterNotify;
                qn.EvShopRobotReceriveNewMessage += OnShopRobotNewMessage;
                Log.Info("直接下单系统事件桥接已绑定: seller="
                    + (qn.Seller == null ? string.Empty : qn.Seller.Nick));
            }
        }

        private static async void OnReceiveNewMessage(object sender, RecieveNewMessageEventArgs e)
        {
            var qn = sender as QN;
            var raw = e == null ? string.Empty : (e.Message ?? string.Empty);
            if (qn == null || raw.Length == 0 || !LooksLikePotentialOrderPayload(raw)) return;

            try
            {
                var chat = JsonConvert.DeserializeObject<ChatResponse>(raw);
                var messages = chat == null || chat.result == null
                    ? new List<QNChatMessage>()
                    : chat.result.Where(x => x != null).ToList();
                if (messages.Count < 1) return;

                foreach (var message in messages)
                {
                    if (!MessageLooksLikeOrder(message)) continue;
                    await qn.ProcessDirectOrderMessageAsync(
                        message,
                        DirectOrderIdentityResolver.ResolveSeller(qn, message, null),
                        DirectOrderIdentityResolver.ResolveBuyer(qn, message, null),
                        "receiveNewMsg系统订单卡片");
                }
            }
            catch (Exception ex)
            {
                Log.Info("直接下单 receiveNewMsg 桥接解析失败: length=" + raw.Length + ", error=" + ex.Message);
            }
        }

        private static async void OnMessageCenterNotify(object sender, MessageNotifyEventArgs e)
        {
            var qn = sender as QN;
            var raw = e == null ? string.Empty : (e.NotifyContent ?? string.Empty);
            if (qn == null || raw.Length == 0 || !LooksLikePotentialOrderPayload(raw)) return;

            var reservation = Hash(raw);
            CleanupReservations();
            DateTime until;
            if (RawEventReservations.TryGetValue(reservation, out until) && until >= DateTime.Now) return;
            RawEventReservations[reservation] = DateTime.Now.AddMinutes(2);

            try
            {
                JToken token;
                try { token = JToken.Parse(raw); }
                catch { token = new JValue(raw); }

                var envelope = BuildNotificationEnvelope(qn, token, raw);
                if (envelope == null || string.IsNullOrWhiteSpace(envelope.OrderId)) return;
                if (string.IsNullOrWhiteSpace(envelope.Buyer))
                {
                    Log.Info("消息中心检测到订单通知但缺少买家昵称，等待详细订单卡片补偿: orderId="
                        + envelope.OrderId + ", payloadHash=" + reservation);
                    return;
                }

                var synthetic = new QNChatMessage
                {
                    summary = envelope.Text
                };
                await qn.ProcessDirectOrderMessageAsync(
                    synthetic,
                    envelope.Seller,
                    envelope.Buyer,
                    "messageCenterNotify订单通知");
            }
            catch (Exception ex)
            {
                Log.Info("消息中心订单通知解析失败: length=" + raw.Length
                    + ", payloadHash=" + reservation + ", error=" + ex.Message);
            }
        }

        private static void OnShopRobotNewMessage(object sender, ShopRobotReceriveNewMessageEventArgs e)
        {
            // QN 原链路会在该事件中执行定向远端历史补抓。
            // 这里只补充诊断，便于区分“通知没到”和“远端历史未包含订单卡片”。
            if (e == null || e.Seller == null || e.Buyer == null) return;
            Log.Info("直接下单桥接观察到后台会话通知: seller=" + (e.Seller.Nick ?? string.Empty)
                + ", buyer=" + (e.Buyer.Nick ?? string.Empty));
        }

        private static bool MessageLooksLikeOrder(QNChatMessage message)
        {
            if (message == null) return false;
            try
            {
                var json = JObject.FromObject(message).ToString(Formatting.None);
                return LooksLikePotentialOrderPayload(json);
            }
            catch
            {
                var text = (message.summary ?? string.Empty)
                    + " " + (message.originalData == null ? string.Empty : message.originalData.text ?? string.Empty);
                return LooksLikePotentialOrderPayload(text);
            }
        }

        private static bool LooksLikePotentialOrderPayload(string raw)
        {
            raw = raw ?? string.Empty;
            if (raw.Length < 8) return false;
            var lower = raw.ToLowerInvariant();
            var hasOrderWord = raw.Contains("订单")
                || raw.Contains("下单")
                || raw.Contains("已付款")
                || raw.Contains("支付成功")
                || raw.Contains("待发货")
                || lower.Contains("orderid")
                || lower.Contains("bizorderid")
                || lower.Contains("tradeid")
                || lower.Contains("wait_seller_send_goods");
            if (!hasOrderWord) return false;
            return OrderIdTextRegex.IsMatch(raw)
                || Regex.IsMatch(lower, @"(?:orderid|bizorderid|mainorderid|suborderid|tradeid|\"tid\")\s*[\"']?\s*[:=]\s*[\"']?\d{8,}");
        }

        private static NotificationEnvelope BuildNotificationEnvelope(QN qn, JToken token, string raw)
        {
            var flat = Flatten(token);
            var allText = BuildFlatText(flat, raw);
            var orderId = ExtractOrderId(flat, allText);
            if (string.IsNullOrWhiteSpace(orderId)) return null;

            var seller = FindValue(flat, SellerKeys);
            if (string.IsNullOrWhiteSpace(seller) && qn.Seller != null) seller = qn.Seller.Nick;
            var buyer = FindIdentity(flat, BuyerKeys, seller);

            var item = FindValue(flat, ItemTitleKeys);
            var sku = FindValue(flat, SkuTextKeys);
            var amount = FindValue(flat, AmountKeys);
            var paid = IsPaidCue(allText);
            var sb = new StringBuilder();
            sb.Append(paid ? "买家已付款 " : "买家已下单 ");
            sb.Append("订单号：").Append(orderId);
            if (!string.IsNullOrWhiteSpace(item)) sb.Append(" 商品：").Append(Short(item, 240));
            if (!string.IsNullOrWhiteSpace(sku)) sb.Append(" 规格：").Append(Short(sku, 180));
            if (!string.IsNullOrWhiteSpace(amount)) sb.Append(paid ? " 实付：" : " 金额：").Append(Short(amount, 60));
            sb.Append(" 订单状态：").Append(paid ? "已付款" : "新下单");

            return new NotificationEnvelope
            {
                Seller = (seller ?? string.Empty).Trim(),
                Buyer = (buyer ?? string.Empty).Trim(),
                OrderId = orderId,
                Text = sb.ToString()
            };
        }

        private static List<FlatValue> Flatten(JToken root)
        {
            var values = new List<FlatValue>();
            Walk(root, string.Empty, values, 0);
            return values;
        }

        private static void Walk(JToken token, string path, List<FlatValue> output, int depth)
        {
            if (token == null || depth > 14 || output.Count >= 900) return;
            if (token.Type == JTokenType.Object)
            {
                foreach (var property in ((JObject)token).Properties())
                {
                    Walk(property.Value, path.Length == 0 ? property.Name : path + "." + property.Name, output, depth + 1);
                    if (output.Count >= 900) break;
                }
                return;
            }
            if (token.Type == JTokenType.Array)
            {
                var index = 0;
                foreach (var child in (JArray)token)
                {
                    Walk(child, path + "[" + index + "]", output, depth + 1);
                    if (++index >= 100 || output.Count >= 900) break;
                }
                return;
            }
            if (token.Type == JTokenType.Null || token.Type == JTokenType.Undefined) return;
            var value = token.ToString().Trim();
            if (value.Length == 0 || value.Length > 3000) return;
            var key = path;
            var dot = key.LastIndexOf('.');
            if (dot >= 0) key = key.Substring(dot + 1);
            var bracket = key.IndexOf('[');
            if (bracket >= 0) key = key.Substring(0, bracket);
            output.Add(new FlatValue { Path = path, Key = key, Value = value });
        }

        private static string BuildFlatText(IEnumerable<FlatValue> flat, string raw)
        {
            var parts = (flat ?? new FlatValue[0])
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Value) && x.Value.Length <= 500)
                .Select(x => x.Value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(180)
                .ToList();
            if (parts.Count < 1) parts.Add(Short(raw, 1800));
            return string.Join(" ", parts);
        }

        private static string ExtractOrderId(IList<FlatValue> flat, string text)
        {
            var value = FindValue(flat, OrderIdKeys);
            var digits = DigitsOnly(value, 8, 40);
            if (!string.IsNullOrWhiteSpace(digits)) return digits;
            var match = OrderIdTextRegex.Match(text ?? string.Empty);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        private static string FindIdentity(IList<FlatValue> flat, IEnumerable<string> aliases, string seller)
        {
            var direct = FindValue(flat, aliases);
            if (IsUsableIdentity(direct, seller)) return direct.Trim();

            foreach (var item in flat ?? new List<FlatValue>())
            {
                if (item == null || !IsUsableIdentity(item.Value, seller)) continue;
                var path = NormalizeKey(item.Path);
                if ((path.Contains("buyer") || path.Contains("contact") || path.Contains("conversation"))
                    && (path.EndsWith("nick") || path.EndsWith("name") || path.EndsWith("uid")))
                {
                    return item.Value.Trim();
                }
            }
            return string.Empty;
        }

        private static string FindValue(IList<FlatValue> flat, IEnumerable<string> aliases)
        {
            var set = new HashSet<string>((aliases ?? new string[0]).Select(NormalizeKey), StringComparer.OrdinalIgnoreCase);
            foreach (var item in flat ?? new List<FlatValue>())
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Value)) continue;
                if (set.Contains(NormalizeKey(item.Key))) return item.Value.Trim();
            }
            foreach (var item in flat ?? new List<FlatValue>())
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Value)) continue;
                var path = NormalizeKey(item.Path);
                if (set.Any(x => path.EndsWith(x, StringComparison.OrdinalIgnoreCase))) return item.Value.Trim();
            }
            return string.Empty;
        }

        private static bool IsPaidCue(string text)
        {
            text = (text ?? string.Empty).ToLowerInvariant();
            return text.Contains("已付款")
                || text.Contains("支付成功")
                || text.Contains("买家已付款")
                || text.Contains("wait_seller_send_goods")
                || text.Contains("paid");
        }

        private static bool IsUsableIdentity(string value, string seller)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length < 2 || value.Length > 160) return false;
            if (DirectOrderIdentityResolver.IdentityEquals(value, seller)) return false;
            if (value.Contains("订单") || value.Contains("付款") || value.Contains("商品")) return false;
            if (Regex.IsMatch(value, @"^\d{16,}$")) return false;
            return true;
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

        private static string Hash(string value)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
            }
        }

        private static string Short(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }

        private static void CleanupReservations()
        {
            var now = DateTime.Now;
            foreach (var pair in RawEventReservations)
            {
                if (pair.Value >= now) continue;
                DateTime ignored;
                RawEventReservations.TryRemove(pair.Key, out ignored);
            }
        }
    }

    internal static class DirectOrderIdentityResolver
    {
        public static string ResolveSeller(QN qn, QNChatMessage message, string hint)
        {
            if (!string.IsNullOrWhiteSpace(hint)) return hint.Trim();
            if (qn != null && qn.Seller != null && !string.IsNullOrWhiteSpace(qn.Seller.Nick)) return qn.Seller.Nick.Trim();
            if (message != null && message.loginid != null && !string.IsNullOrWhiteSpace(message.loginid.nick)) return message.loginid.nick.Trim();
            return string.Empty;
        }

        public static string ResolveBuyer(QN qn, QNChatMessage message, string hint)
        {
            var seller = ResolveSeller(qn, message, null);
            if (IsCandidate(hint, seller)) return hint.Trim();
            if (message != null)
            {
                var from = message.fromid == null ? string.Empty : message.fromid.nick;
                var to = message.toid == null ? string.Empty : message.toid.nick;
                if (IsCandidate(from, seller)) return from.Trim();
                if (IsCandidate(to, seller)) return to.Trim();

                try
                {
                    var token = JObject.FromObject(message);
                    var candidate = FindBuyerInToken(token, seller);
                    if (IsCandidate(candidate, seller)) return candidate.Trim();
                }
                catch { }
            }
            return string.Empty;
        }

        public static bool IdentityEquals(string left, string right)
        {
            var a = NormalizeIdentity(left);
            var b = NormalizeIdentity(right);
            if (a.Length == 0 || b.Length == 0) return false;
            if (a == b) return true;
            var aHead = a.Split(':')[0];
            var bHead = b.Split(':')[0];
            return aHead.Length >= 4 && aHead == bHead;
        }

        private static string FindBuyerInToken(JToken root, string seller)
        {
            if (root == null) return string.Empty;
            foreach (var token in root.DescendantsAndSelf())
            {
                var property = token.Parent as JProperty;
                if (property == null || token.Type == JTokenType.Object || token.Type == JTokenType.Array) continue;
                var key = Regex.Replace(property.Name.ToLowerInvariant(), @"[^a-z0-9]", string.Empty);
                var path = Regex.Replace(token.Path.ToLowerInvariant(), @"[^a-z0-9]", string.Empty);
                if (!(key == "buyernick" || key == "buyername" || key == "contactnick"
                    || key == "conversationnick" || key == "usernick"
                    || ((path.Contains("buyer") || path.Contains("conversation") || path.Contains("contact"))
                        && (key == "nick" || key == "name")))) continue;
                var value = token.ToString();
                if (IsCandidate(value, seller)) return value;
            }
            return string.Empty;
        }

        private static bool IsCandidate(string value, string seller)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length < 2 || value.Length > 160) return false;
            if (IdentityEquals(value, seller)) return false;
            if (value.Contains("订单") || value.Contains("付款") || value.Contains("商品")) return false;
            if (Regex.IsMatch(value, @"^\d{16,}$")) return false;
            return true;
        }

        private static string NormalizeIdentity(string value)
        {
            value = Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", string.Empty);
            if (value.StartsWith("cntaobao", StringComparison.Ordinal)) value = value.Substring("cntaobao".Length);
            return value;
        }
    }

    public partial class QN
    {
        internal async Task ProcessDirectOrderMessageAsync(
            QNChatMessage message,
            string sellerHint,
            string buyerHint,
            string source)
        {
            if (message == null) return;
            var seller = DirectOrderIdentityResolver.ResolveSeller(this, message, sellerHint);
            var buyer = DirectOrderIdentityResolver.ResolveBuyer(this, message, buyerHint);
            var text = GetMessageText(message);

            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(buyer))
            {
                Log.Info("检测到疑似订单系统卡片但无法解析客服/买家身份，暂不自动处理: source="
                    + source + ", seller=" + seller + ", buyer=" + buyer);
                return;
            }

            OrderPlacedReplyPlan plan;
            if (!OrderPlacedAutoReplyService.TryCreatePlan(
                message,
                text,
                seller,
                buyer,
                _messageSafetyStartedAt,
                out plan))
            {
                return;
            }

            if (plan == null)
            {
                Log.Info("订单系统事件已由其他通道处理或已去重: source=" + source
                    + ", seller=" + seller + ", buyer=" + buyer);
                return;
            }

            Log.Info("优先识别到直接下单系统事件: source=" + source
                + ", seller=" + seller + ", buyer=" + buyer + ", orderId=" + plan.OrderId);
            await ProcessOrderPlacedReplyAsync(plan);
        }
    }
}
