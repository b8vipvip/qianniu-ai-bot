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
    /// 直接下单/付款可能以千牛系统卡片或 messageCenterNotify 到达，而不是普通 buyer -> seller 消息。
    /// 本桥接器订阅 QN 暴露的原始事件，在原有 IsBuyerMessage 过滤之前处理订单。
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
        private static readonly ConcurrentDictionary<string, DateTime> RawReservations =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private static Timer _timer;
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
                _timer = new Timer(_ => Attach(), null, 0, 750);
            }
        }

        private static void Attach()
        {
            QN[] instances;
            try
            {
                instances = QN.QNSet == null ? new QN[0] : QN.QNSet.Where(x => x != null).ToArray();
            }
            catch
            {
                return;
            }

            foreach (var qn in instances)
            {
                lock (Sync)
                {
                    if (Attached.Contains(qn)) continue;
                    Attached.Add(qn);
                }
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
            if (qn == null || raw.Length == 0 || !LooksPotential(raw)) return;

            try
            {
                var chat = JsonConvert.DeserializeObject<ChatResponse>(raw);
                var messages = chat == null || chat.result == null
                    ? new List<QNChatMessage>()
                    : chat.result.Where(x => x != null).ToList();
                foreach (var message in messages)
                {
                    if (!MessageLooksPotential(message)) continue;
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
            if (qn == null || raw.Length == 0 || !LooksPotential(raw)) return;

            var hash = Hash(raw);
            CleanupReservations();
            DateTime until;
            if (RawReservations.TryGetValue(hash, out until) && until >= DateTime.Now) return;
            RawReservations[hash] = DateTime.Now.AddMinutes(2);

            try
            {
                JToken token;
                try { token = JToken.Parse(raw); }
                catch { token = new JValue(raw); }
                var envelope = BuildEnvelope(qn, token, raw);
                if (envelope == null || string.IsNullOrWhiteSpace(envelope.OrderId)) return;
                if (string.IsNullOrWhiteSpace(envelope.Buyer))
                {
                    Log.Info("消息中心检测到订单通知但缺少买家昵称，等待详细卡片补偿: orderId="
                        + envelope.OrderId + ", payloadHash=" + hash);
                    return;
                }

                var synthetic = new QNChatMessage { summary = envelope.Text };
                await qn.ProcessDirectOrderMessageAsync(
                    synthetic,
                    envelope.Seller,
                    envelope.Buyer,
                    "messageCenterNotify订单通知");
            }
            catch (Exception ex)
            {
                Log.Info("消息中心订单通知解析失败: length=" + raw.Length
                    + ", payloadHash=" + hash + ", error=" + ex.Message);
            }
        }

        private static void OnShopRobotNewMessage(object sender, ShopRobotReceriveNewMessageEventArgs e)
        {
            if (e == null || e.Seller == null || e.Buyer == null) return;
            Log.Info("直接下单桥接观察到后台会话通知: seller=" + (e.Seller.Nick ?? string.Empty)
                + ", buyer=" + (e.Buyer.Nick ?? string.Empty));
        }

        private static bool MessageLooksPotential(QNChatMessage message)
        {
            if (message == null) return false;
            try { return LooksPotential(JObject.FromObject(message).ToString(Formatting.None)); }
            catch
            {
                var text = (message.summary ?? string.Empty)
                    + " " + (message.originalData == null ? string.Empty : message.originalData.text ?? string.Empty);
                return LooksPotential(text);
            }
        }

        private static bool LooksPotential(string raw)
        {
            raw = raw ?? string.Empty;
            if (raw.Length < 8) return false;
            var lower = raw.ToLowerInvariant();
            var cue = raw.Contains("订单") || raw.Contains("下单") || raw.Contains("已付款")
                || raw.Contains("支付成功") || raw.Contains("待发货")
                || lower.Contains("orderid") || lower.Contains("bizorderid")
                || lower.Contains("tradeid") || lower.Contains("wait_seller_send_goods");
            if (!cue) return false;
            return OrderIdTextRegex.IsMatch(raw)
                || Regex.IsMatch(lower,
                    "(?:orderid|bizorderid|mainorderid|suborderid|tradeid|\\\"tid\\\")\\s*[\\\"']?\\s*[:=]\\s*[\\\"']?\\d{8,}");
        }

        private static NotificationEnvelope BuildEnvelope(QN qn, JToken token, string raw)
        {
            var flat = Flatten(token);
            var text = string.Join(" ", flat
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Value) && x.Value.Length <= 500)
                .Select(x => x.Value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(180));
            if (string.IsNullOrWhiteSpace(text)) text = Short(raw, 1800);

            var orderId = DigitsOnly(FindValue(flat, OrderIdKeys), 8, 40);
            if (string.IsNullOrWhiteSpace(orderId))
            {
                var match = OrderIdTextRegex.Match(text);
                if (match.Success) orderId = match.Groups[1].Value;
            }
            if (string.IsNullOrWhiteSpace(orderId)) return null;

            var seller = FindValue(flat, SellerKeys);
            if (string.IsNullOrWhiteSpace(seller) && qn.Seller != null) seller = qn.Seller.Nick;
            var buyer = FindIdentity(flat, seller);
            var item = FindValue(flat, ItemTitleKeys);
            var sku = FindValue(flat, SkuTextKeys);
            var amount = FindValue(flat, AmountKeys);
            var paid = IsPaid(text);

            var summary = new StringBuilder();
            summary.Append(paid ? "买家已付款 " : "买家已下单 ");
            summary.Append("订单号：").Append(orderId);
            if (!string.IsNullOrWhiteSpace(item)) summary.Append(" 商品：").Append(Short(item, 240));
            if (!string.IsNullOrWhiteSpace(sku)) summary.Append(" 规格：").Append(Short(sku, 180));
            if (!string.IsNullOrWhiteSpace(amount)) summary.Append(paid ? " 实付：" : " 金额：").Append(Short(amount, 60));
            summary.Append(" 订单状态：").Append(paid ? "已付款" : "新下单");

            return new NotificationEnvelope
            {
                Seller = (seller ?? string.Empty).Trim(),
                Buyer = (buyer ?? string.Empty).Trim(),
                OrderId = orderId,
                Text = summary.ToString()
            };
        }

        private static List<FlatValue> Flatten(JToken root)
        {
            var result = new List<FlatValue>();
            Walk(root, string.Empty, result, 0);
            return result;
        }

        private static void Walk(JToken token, string path, List<FlatValue> output, int depth)
        {
            if (token == null || depth > 14 || output.Count >= 900) return;
            if (token.Type == JTokenType.Object)
            {
                foreach (var p in ((JObject)token).Properties())
                {
                    Walk(p.Value, path.Length == 0 ? p.Name : path + "." + p.Name, output, depth + 1);
                    if (output.Count >= 900) break;
                }
                return;
            }
            if (token.Type == JTokenType.Array)
            {
                var i = 0;
                foreach (var child in (JArray)token)
                {
                    Walk(child, path + "[" + i + "]", output, depth + 1);
                    if (++i >= 100 || output.Count >= 900) break;
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

        private static string FindIdentity(IList<FlatValue> flat, string seller)
        {
            var direct = FindValue(flat, BuyerKeys);
            if (UsableIdentity(direct, seller)) return direct.Trim();
            foreach (var item in flat ?? new List<FlatValue>())
            {
                if (item == null || !UsableIdentity(item.Value, seller)) continue;
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
                if (item != null && !string.IsNullOrWhiteSpace(item.Value)
                    && set.Contains(NormalizeKey(item.Key))) return item.Value.Trim();
            }
            foreach (var item in flat ?? new List<FlatValue>())
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Value)) continue;
                var path = NormalizeKey(item.Path);
                if (set.Any(x => path.EndsWith(x, StringComparison.OrdinalIgnoreCase))) return item.Value.Trim();
            }
            return string.Empty;
        }

        private static bool IsPaid(string text)
        {
            text = (text ?? string.Empty).ToLowerInvariant();
            return text.Contains("已付款") || text.Contains("支付成功")
                || text.Contains("买家已付款") || text.Contains("wait_seller_send_goods")
                || text.Contains("paid");
        }

        private static bool UsableIdentity(string value, string seller)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length < 2 || value.Length > 160) return false;
            if (DirectOrderIdentityResolver.IdentityEquals(value, seller)) return false;
            if (value.Contains("订单") || value.Contains("付款") || value.Contains("商品")) return false;
            return !Regex.IsMatch(value, @"^\d{16,}$");
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
                    .Replace("-", string.Empty).ToLowerInvariant();
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
            foreach (var pair in RawReservations)
            {
                if (pair.Value >= now) continue;
                DateTime ignored;
                RawReservations.TryRemove(pair.Key, out ignored);
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
            if (Candidate(hint, seller)) return hint.Trim();
            if (message == null) return string.Empty;

            var from = message.fromid == null ? string.Empty : message.fromid.nick;
            var to = message.toid == null ? string.Empty : message.toid.nick;
            if (Candidate(from, seller)) return from.Trim();
            if (Candidate(to, seller)) return to.Trim();

            try
            {
                foreach (var token in JObject.FromObject(message).DescendantsAndSelf())
                {
                    var property = token.Parent as JProperty;
                    if (property == null || token.Type == JTokenType.Object || token.Type == JTokenType.Array) continue;
                    var key = Normalize(property.Name);
                    var path = Normalize(token.Path);
                    var allowed = key == "buyernick" || key == "buyername" || key == "contactnick"
                        || key == "conversationnick" || key == "usernick"
                        || ((path.Contains("buyer") || path.Contains("conversation") || path.Contains("contact"))
                            && (key == "nick" || key == "name"));
                    if (!allowed) continue;
                    var value = token.ToString();
                    if (Candidate(value, seller)) return value.Trim();
                }
            }
            catch { }
            return string.Empty;
        }

        public static bool IdentityEquals(string left, string right)
        {
            var a = NormalizeIdentity(left);
            var b = NormalizeIdentity(right);
            if (a.Length == 0 || b.Length == 0) return false;
            if (a == b) return true;
            var ah = a.Split(':')[0];
            var bh = b.Split(':')[0];
            return ah.Length >= 4 && ah == bh;
        }

        private static bool Candidate(string value, string seller)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length < 2 || value.Length > 160) return false;
            if (IdentityEquals(value, seller)) return false;
            if (value.Contains("订单") || value.Contains("付款") || value.Contains("商品")) return false;
            return !Regex.IsMatch(value, @"^\d{16,}$");
        }

        private static string Normalize(string value)
        {
            return Regex.Replace((value ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9]", string.Empty);
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
            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(buyer))
            {
                Log.Info("检测到疑似订单系统卡片但无法解析客服/买家身份，暂不自动处理: source="
                    + source + ", seller=" + seller + ", buyer=" + buyer);
                return;
            }

            OrderPlacedReplyPlan plan;
            if (!OrderPlacedAutoReplyService.TryCreatePlan(
                message,
                GetMessageText(message),
                seller,
                buyer,
                _messageSafetyStartedAt,
                out plan)) return;

            if (plan == null)
            {
                Log.Info("订单系统事件已由其他通道处理或已去重: source=" + source
                    + ", seller=" + seller + ", buyer=" + buyer);
                return;
            }

            Log.Info("优先识别到直接下单系统事件: source=" + source
                + ", seller=" + seller + ", buyer=" + buyer + ", orderId=" + plan.OrderId);
            // 展开诊断桥接能够解析嵌套 messageCenterNotify，但不得绕过统一字段补全。
            // 计划已包含准确 seller、buyer 和 orderId，交给 V2 查询交易详情后再发送。
            if (OrderTemplateRequiredFieldsV2.TryOwnExistingPlan(this, plan, source)) return;

            await ProcessOrderPlacedReplyAsync(plan);
        }
    }
}
