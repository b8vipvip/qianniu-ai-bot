using Bot.ChatRecord;
using BotLib;
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
    /// 千牛部分版本把 messageCenterNotify.response 直接作为 JSON 对象发送，
    /// 而旧 WSocketMessage.Response 是 string。该兼容转换器只在“目标属性是 string，
    /// 实际 token 却是 object/array”时把 token 保留为紧凑 JSON 文本，避免整条付款事件反序列化失败。
    /// </summary>
    internal static class QianniuWebSocketJsonCompatibility
    {
        private static int _initialized;

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            var previous = JsonConvert.DefaultSettings;
            JsonConvert.DefaultSettings = () =>
            {
                var settings = previous == null ? new JsonSerializerSettings() : previous();
                if (settings == null) settings = new JsonSerializerSettings();
                if (!settings.Converters.OfType<FlexibleJsonStringConverter>().Any())
                {
                    settings.Converters.Insert(0, new FlexibleJsonStringConverter());
                }
                return settings;
            };
            Log.Info("千牛WebSocket JSON对象响应兼容已启用");
        }
    }

    internal sealed class FlexibleJsonStringConverter : JsonConverter
    {
        public override bool CanWrite { get { return false; } }

        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(string);
        }

        public override object ReadJson(
            JsonReader reader,
            Type objectType,
            object existingValue,
            JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null || reader.TokenType == JsonToken.Undefined) return null;
            if (reader.TokenType == JsonToken.String) return Convert.ToString(reader.Value);
            if (reader.TokenType == JsonToken.Integer
                || reader.TokenType == JsonToken.Float
                || reader.TokenType == JsonToken.Boolean
                || reader.TokenType == JsonToken.Date)
            {
                return Convert.ToString(reader.Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            var token = JToken.Load(reader);
            return token.Type == JTokenType.String
                ? token.ToString()
                : token.ToString(Formatting.None);
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    /// 对 messageCenterNotify 再做一层兼容解析：
    /// - 展开被包在字符串里的嵌套 JSON；
    /// - 兼容更多 buyer/contact/customer/sender/opposite 字段；
    /// - 能直接确认订单号/买家时继续走结构化订单链路；
    /// - 不能直接确认时，不从通知内容猜订单，而把通知作为“订单面板可能已刷新”的唤醒信号，
    ///   对当时已验证的当前买家做最长 180 秒被动右侧订单面板补扫；
    /// - 面板补扫仍要求真实 16-24 位订单号、受支持状态和新鲜订单时间，且读取前后买家必须一致。
    /// </summary>
    internal static class OrderPaymentNotificationFallback
    {
        private sealed class FlatValue
        {
            public string Path;
            public string Key;
            public string Value;
        }

        private sealed class PanelProbeState
        {
            public DateTime StartedAt;
            public int Running;
        }

        private static readonly object Sync = new object();
        private static readonly HashSet<QN> Attached = new HashSet<QN>();
        private static readonly ConcurrentDictionary<string, DateTime> Reservations =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, PanelProbeState> PanelProbes =
            new ConcurrentDictionary<string, PanelProbeState>(StringComparer.Ordinal);
        private static readonly int[] PanelProbeDelaysMs =
            { 250, 900, 1800, 3200, 5200, 8000, 12000, 20000, 30000, 45000, 60000, 90000, 120000, 150000, 180000 };
        private static Timer _timer;
        private static int _initialized;

        private static readonly Regex EventCueRegex = new Regex(
            "买家已下单|下单成功|订单创建成功|买家已付款|付款成功|支付成功|待卖家发货|卖家待发货|WAIT_SELLER_SEND_GOODS|WAIT_BUYER_PAY|申请退款|退款中|交易关闭|订单关闭",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex LabeledOrderRegex = new Regex(
            "(?:订单号|订单编号|主订单号|子订单号|交易号)\\s*[:：#]?\\s*(\\d{8,})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly string[] StrongOrderKeys =
        {
            "orderid", "bizorderid", "mainorderid", "suborderid", "biztradeid", "tradeid", "tid"
        };

        private static readonly string[] BuyerAliases =
        {
            "buyernick", "buyernickname", "buyername", "buyerid", "buyeruid",
            "customernick", "customername", "customerid", "customeruid",
            "contactnick", "contactname", "contactid", "conversationnick", "conversationname",
            "oppositenick", "oppositename", "peernick", "peername",
            "sendernick", "sendername", "fromnick", "fromname",
            "targetnick", "targetname", "membernick", "membername",
            "usernick", "username", "usernickname"
        };

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            _timer = new Timer(_ => Attach(), null, 0, 750);
        }

        private static void Attach()
        {
            QN[] qns;
            try { qns = QN.QNSet == null ? new QN[0] : QN.QNSet.Where(x => x != null).ToArray(); }
            catch { return; }

            foreach (var qn in qns)
            {
                lock (Sync)
                {
                    if (Attached.Contains(qn)) continue;
                    Attached.Add(qn);
                }
                qn.EvMessageNotity += OnMessageNotify;
                Log.Info("付款通知兼容兜底已绑定: seller="
                    + (qn.Seller == null ? string.Empty : qn.Seller.Nick));
            }
        }

        private static async void OnMessageNotify(object sender, MessageNotifyEventArgs e)
        {
            var qn = sender as QN;
            var raw = e == null ? string.Empty : (e.NotifyContent ?? string.Empty).Trim();
            if (qn == null || raw.Length == 0) return;

            if (!LooksLikeOrderEvent(raw))
            {
                ScheduleVerifiedCurrentBuyerPanelRecovery(
                    qn,
                    "messageCenterNotify未携带可直接判定的订单号/状态");
                return;
            }

            var hash = Hash(raw);
            CleanupReservations();
            DateTime until;
            if (Reservations.TryGetValue(hash, out until) && until >= DateTime.Now) return;
            Reservations[hash] = DateTime.Now.AddMinutes(2);

            try
            {
                var root = ParseExpanded(raw);
                var flat = Flatten(root);
                var combined = raw + " " + string.Join(" ", flat.Select(x => x.Value).Where(x => !string.IsNullOrWhiteSpace(x)).Take(250));
                if (!EventCueRegex.IsMatch(combined))
                {
                    ScheduleVerifiedCurrentBuyerPanelRecovery(qn, "messageCenterNotify缺少明确订单状态");
                    return;
                }

                var orderId = ResolveOrderId(flat, combined);
                if (string.IsNullOrWhiteSpace(orderId))
                {
                    ScheduleVerifiedCurrentBuyerPanelRecovery(qn, "messageCenterNotify缺少可验证订单号");
                    return;
                }
                var seller = qn.Seller == null ? string.Empty : (qn.Seller.Nick ?? string.Empty).Trim();
                var buyer = ResolveBuyer(flat, seller, orderId);
                if (string.IsNullOrWhiteSpace(buyer))
                {
                    var diagnosticKeys = string.Join(",", flat
                        .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Key))
                        .Select(x => Normalize(x.Key))
                        .Where(x => x.Contains("buyer") || x.Contains("customer") || x.Contains("contact")
                            || x.Contains("sender") || x.Contains("from") || x.Contains("opposite")
                            || x.Contains("conversation") || x.Contains("user"))
                        .Distinct()
                        .Take(24));
                    Log.Info("付款通知已解析订单号但仍缺少买家身份，未猜测当前会话: orderId="
                        + orderId + ", payloadHash=" + hash + ", identityKeys=" + diagnosticKeys);
                    ScheduleVerifiedCurrentBuyerPanelRecovery(qn, "messageCenterNotify有订单号但缺少买家身份");
                    return;
                }

                var paid = Regex.IsMatch(combined, "已付款|付款成功|支付成功|待卖家发货|WAIT_SELLER_SEND_GOODS", RegexOptions.IgnoreCase);
                var summary = (paid ? "买家已付款 " : "买家已下单 ")
                    + "订单号：" + orderId
                    + " 订单状态：" + (paid ? "已付款" : "新下单");
                var synthetic = new QNChatMessage { summary = summary };
                Log.Info("付款通知兼容兜底解析成功: seller=" + seller
                    + ", buyer=" + buyer + ", orderId=" + orderId + ", paid=" + paid);
                await qn.ProcessDirectOrderMessageAsync(
                    synthetic,
                    seller,
                    buyer,
                    "messageCenterNotify嵌套JSON兼容兜底");
            }
            catch (Exception ex)
            {
                Log.Info("付款通知兼容兜底解析失败: payloadHash=" + hash + ", error=" + ex.Message);
                ScheduleVerifiedCurrentBuyerPanelRecovery(qn, "messageCenterNotify解析异常");
            }
        }

        private static void ScheduleVerifiedCurrentBuyerPanelRecovery(QN qn, string trigger)
        {
            if (qn == null) return;
            Task.Run(async () =>
            {
                var seller = qn.Seller == null ? string.Empty : (qn.Seller.Nick ?? string.Empty).Trim();
                if (seller.Length == 0) return;

                DbEntity.Conversation current = null;
                try
                {
                    var response = await qn.GetCurrentConversationID().ConfigureAwait(false);
                    current = response == null ? null : response.Result;
                }
                catch (Exception ex)
                {
                    Log.Info("messageCenterNotify订单面板验证无法读取当前会话: seller=" + seller
                        + ", error=" + ex.Message);
                    return;
                }
                if (current == null || string.IsNullOrWhiteSpace(current.Nick))
                {
                    Log.Info("messageCenterNotify订单面板验证已跳过：当前没有可验证买家会话。seller=" + seller);
                    return;
                }

                BuyerIdentityAliasService.Observe(seller, current.Nick, current.Display, current.TargetId);
                var buyer = BuyerIdentityAliasService.ResolveInternalNick(seller, current.Nick);
                if (string.IsNullOrWhiteSpace(buyer)) buyer = current.Nick.Trim();
                if (buyer.Length == 0) return;

                var key = seller.Trim().ToLowerInvariant() + "#" + buyer.Trim().ToLowerInvariant();
                var state = PanelProbes.GetOrAdd(key, _ => new PanelProbeState { StartedAt = DateTime.Now });
                if (Interlocked.Exchange(ref state.Running, 1) != 0) return;

                Log.Info("messageCenterNotify未形成可直接处理的订单事件，启动当前会话右侧订单面板验证: seller="
                    + seller + ", buyer=" + buyer + ", trigger=" + (trigger ?? string.Empty));
                var elapsed = 0;
                try
                {
                    foreach (var targetDelay in PanelProbeDelaysMs)
                    {
                        var wait = Math.Max(0, targetDelay - elapsed);
                        if (wait > 0) await Task.Delay(wait).ConfigureAwait(false);
                        elapsed = targetDelay;

                        if (await qn.TryRecoverVisibleOrderPanelForBackgroundProbeAsync(
                            seller,
                            buyer,
                            "messageCenterNotify被动验证补扫@" + targetDelay + "ms",
                            state.StartedAt,
                            false).ConfigureAwait(false))
                        {
                            return;
                        }
                    }
                    Log.Info("messageCenterNotify订单面板验证结束：180秒内未发现可确认的新订单。seller="
                        + seller + ", buyer=" + buyer);
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("messageCenterNotify订单面板验证异常: seller=" + seller
                        + ", buyer=" + buyer + ", error=" + ex.Message, 20);
                }
                finally
                {
                    PanelProbeState currentState;
                    if (PanelProbes.TryGetValue(key, out currentState) && ReferenceEquals(currentState, state))
                    {
                        PanelProbeState ignored;
                        PanelProbes.TryRemove(key, out ignored);
                    }
                }
            });
        }

        private static bool LooksLikeOrderEvent(string raw)
        {
            if (!EventCueRegex.IsMatch(raw)) return false;
            return LabeledOrderRegex.IsMatch(raw)
                || Regex.IsMatch(raw,
                    "[\\\"'](?:orderid|bizorderid|mainorderid|suborderid|biztradeid|tradeid|tid)[\\\"']\\s*:\\s*[\\\"']?\\d{8,}",
                    RegexOptions.IgnoreCase);
        }

        private static JToken ParseExpanded(string raw)
        {
            JToken token;
            try { token = JToken.Parse(raw); }
            catch { return new JValue(raw); }
            for (var i = 0; i < 4 && token.Type == JTokenType.String; i++)
            {
                var nested = token.ToString().Trim();
                if (!(nested.StartsWith("{") || nested.StartsWith("["))) break;
                try { token = JToken.Parse(nested); }
                catch { break; }
            }
            return token;
        }

        private static List<FlatValue> Flatten(JToken root)
        {
            var result = new List<FlatValue>();
            Walk(root, string.Empty, result, 0);
            return result;
        }

        private static void Walk(JToken token, string path, List<FlatValue> output, int depth)
        {
            if (token == null || depth > 16 || output.Count >= 1400) return;
            if (token.Type == JTokenType.Object)
            {
                foreach (var property in ((JObject)token).Properties())
                {
                    Walk(property.Value,
                        path.Length == 0 ? property.Name : path + "." + property.Name,
                        output,
                        depth + 1);
                    if (output.Count >= 1400) break;
                }
                return;
            }
            if (token.Type == JTokenType.Array)
            {
                var index = 0;
                foreach (var child in (JArray)token)
                {
                    Walk(child, path + "[" + index + "]", output, depth + 1);
                    if (++index >= 150 || output.Count >= 1400) break;
                }
                return;
            }
            if (token.Type == JTokenType.Null || token.Type == JTokenType.Undefined) return;

            var value = token.ToString().Trim();
            if (value.Length == 0 || value.Length > 12000) return;
            var key = path;
            var dot = key.LastIndexOf('.');
            if (dot >= 0) key = key.Substring(dot + 1);
            var bracket = key.IndexOf('[');
            if (bracket >= 0) key = key.Substring(0, bracket);
            output.Add(new FlatValue { Path = path, Key = key, Value = value.Length > 3000 ? value.Substring(0, 3000) : value });

            if (token.Type == JTokenType.String && depth < 14)
            {
                var trimmed = value.Trim();
                if ((trimmed.StartsWith("{") && trimmed.EndsWith("}"))
                    || (trimmed.StartsWith("[") && trimmed.EndsWith("]")))
                {
                    try { Walk(JToken.Parse(trimmed), path + ".json", output, depth + 1); }
                    catch { }
                }
            }
        }

        private static string ResolveOrderId(IList<FlatValue> flat, string combined)
        {
            foreach (var item in flat ?? new List<FlatValue>())
            {
                if (item == null || !StrongOrderKeys.Contains(Normalize(item.Key))) continue;
                var digits = Digits(item.Value);
                if (digits.Length >= 8 && digits.Length <= 40) return digits;
            }
            var match = LabeledOrderRegex.Match(combined ?? string.Empty);
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
                if (!UsableIdentity(value, seller, orderId)) continue;
                var key = Normalize(item.Key);
                var path = Normalize(item.Path);
                var score = 0;
                if (BuyerAliases.Contains(key)) score += 100;
                if (path.Contains("buyer")) score += 90;
                if (path.Contains("customer")) score += 85;
                if (path.Contains("contact")) score += 75;
                if (path.Contains("opposite") || path.Contains("peer")) score += 70;
                if (path.Contains("conversation")) score += 65;
                if (path.Contains("sender") || path.Contains("from")) score += 45;
                if (path.Contains("target") || path.Contains("member") || path.Contains("user")) score += 35;
                if (key.EndsWith("nick") || key.EndsWith("name")) score += 25;
                if (key.EndsWith("uid") || key.EndsWith("id")) score += 10;
                if (score <= bestScore) continue;
                bestScore = score;
                best = value;
            }
            return bestScore >= 55 ? best : string.Empty;
        }

        private static bool UsableIdentity(string value, string seller, string orderId)
        {
            if (value.Length < 2 || value.Length > 180) return false;
            if (DirectOrderIdentityResolver.IdentityEquals(value, seller)) return false;
            if (string.Equals(Digits(value), orderId, StringComparison.Ordinal)) return false;
            if (value.Contains("订单") || value.Contains("付款") || value.Contains("商品") || value.Contains("金额")) return false;
            if (value.StartsWith("{") || value.StartsWith("[")) return false;
            if (Regex.IsMatch(value, "^\\d{16,}$")) return false;
            return true;
        }

        private static string Normalize(string value)
        {
            return Regex.Replace((value ?? string.Empty).ToLowerInvariant(), "[^a-z0-9]", string.Empty);
        }

        private static string Digits(string value)
        {
            return Regex.Replace(value ?? string.Empty, "\\D", string.Empty);
        }

        private static string Hash(string value)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)))
                    .Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static void CleanupReservations()
        {
            var now = DateTime.Now;
            foreach (var pair in Reservations)
            {
                if (pair.Value >= now) continue;
                DateTime ignored;
                Reservations.TryRemove(pair.Key, out ignored);
            }
        }
    }
}
