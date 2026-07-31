using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;

namespace DbEntity
{
    public class ZnkfTradeQueryResponse
    {
        public string api { get; set; }
        public ZnkfTradeQueryData data { get; set; }

        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }
    }

    public class ZnkfTradeQueryData
    {
        public List<ZnkfTrade> orders { get; set; }

        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }
    }

    public class ZnkfTrade
    {
        public string adjustFee { get; set; }
        public string afterSaleText { get; set; }
        public string bizOrderId { get; set; }
        public int buyAmount { get; set; }
        public string cardTypeText { get; set; }
        public string category { get; set; }
        public bool collapse { get; set; }
        public DateTime? consignTime { get; set; }
        public DateTime createTime { get; set; }
        public DateTime? endTime { get; set; }
        public string expressCompany { get; set; }
        public string expressOrderNumber { get; set; }
        public string orderPrice { get; set; }
        public DateTime? payTime { get; set; }
        public string postFee { get; set; }
        public string promotionTotalFee { get; set; }
        public string receiverAddress { get; set; }
        public string receiverMobilePhone { get; set; }
        public string receiverName { get; set; }
        public string refundFee { get; set; }
        public bool riskOrder { get; set; }
        public int sellerFlag { get; set; }
        public string sellerMemo { get; set; }
        public bool underInquiry { get; set; }
        public List<ZnkfTradeItem> itemList { get; set; }
        public List<ZnkfTradePromotion> promotionDetails { get; set; }

        // 千牛接口会随版本新增字段。保留未知字段，避免右侧订单面板能显示、
        // 而强类型模型静默丢弃 SKU/金额/状态等数据。
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }
    }

    public class ZnkfTradeItem
    {
        public string adjustFee { get; set; }
        public string auctionId { get; set; }
        public string auctionPrice { get; set; }
        public string auctionTitle { get; set; }
        public string auctionUrl { get; set; }
        public string bizOrderId { get; set; }
        public int buyAmount { get; set; }
        public int buyerAmount { get; set; }
        public int buyerRateStatus { get; set; }
        public string cardType { get; set; }
        public string cardTypeText { get; set; }
        public DateTime createTime { get; set; }
        public DateTime? endTime { get; set; }
        public int logisticsStatus { get; set; }
        public string oldPrice { get; set; }
        public string outerId { get; set; }
        public int payStatus { get; set; }
        public DateTime? payTime { get; set; }
        public string picUrl { get; set; }
        public string price { get; set; }
        public string refundFee { get; set; }
        public int refundStatus { get; set; }
        public string refundType { get; set; }
        public string sku { get; set; }
        public string snapshotUrl { get; set; }
        public string subOrderId { get; set; }
        public bool supportPriceProtect { get; set; }
        public bool underInquiry { get; set; }

        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }

        [OnDeserialized]
        internal void RecoverVisibleSkuAfterDeserialization(StreamingContext context)
        {
            if (!string.IsNullOrWhiteSpace(sku))
            {
                sku = QianniuTradeSkuRecovery.NormalizeCandidate(sku);
                return;
            }

            string strategy;
            sku = QianniuTradeSkuRecovery.Resolve(ExtensionData, out strategy);
        }
    }

    public class ZnkfTradePromotion
    {
        public string discountFee { get; set; }
        public string promotionDesc { get; set; }
        public string promotionId { get; set; }
        public string promotionName { get; set; }

        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }
    }

    /// <summary>
    /// 恢复千牛交易接口中被版本差异隐藏的可见 SKU。
    ///
    /// 千牛右侧订单面板常从 skuText、skuPropertiesName，或
    /// pName/vName、propertyName/propertyValue 等嵌套属性组合规格；旧模型只声明
    /// item.sku，未知字段在反序列化时被直接丢弃，最终模板只剩数量和实付。
    /// </summary>
    internal static class QianniuTradeSkuRecovery
    {
        private sealed class FlatValue
        {
            public string Path;
            public string Key;
            public string Value;
        }

        private static readonly string[] DirectKeys =
        {
            "skutext", "skuname", "skutitle", "skuinfo", "skudesc", "skudescription",
            "skupropertiesname", "skupropertyname", "propertiesname", "propertynamevalue",
            "spec", "specification", "specinfo", "salesproperties", "auctionprops",
            "outername", "skucontent", "skudisplay", "skudisplaytext", "skuproperties",
            "propertydesc", "propertytext", "sku"
        };

        private static readonly string[] NameKeys =
        {
            "pname", "propname", "propertyname", "specname", "attributename",
            "optiongroupname", "dimensionname", "label", "key"
        };

        private static readonly string[] ValueKeys =
        {
            "vname", "propvalue", "propertyvalue", "specvalue", "attributevalue",
            "optionname", "selectedvalue", "displayvalue", "value", "text"
        };

        public static string Resolve(IDictionary<string, JToken> extensionData, out string strategy)
        {
            strategy = string.Empty;
            if (extensionData == null || extensionData.Count == 0) return string.Empty;

            var root = new JObject();
            foreach (var pair in extensionData)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null) continue;
                root[pair.Key] = pair.Value.DeepClone();
            }

            var flat = new List<FlatValue>();
            Walk(root, string.Empty, flat, 0);

            var direct = ResolveDirect(flat);
            if (!string.IsNullOrWhiteSpace(direct))
            {
                strategy = "交易接口完整SKU字段";
                return direct;
            }

            var pairs = ResolvePairs(flat);
            if (!string.IsNullOrWhiteSpace(pairs))
            {
                strategy = "交易接口SKU属性对";
                return pairs;
            }

            return string.Empty;
        }

        private static string ResolveDirect(IList<FlatValue> flat)
        {
            var aliases = new HashSet<string>(DirectKeys, StringComparer.OrdinalIgnoreCase);
            var best = string.Empty;
            var bestScore = 0;

            foreach (var item in flat ?? new List<FlatValue>())
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Value)) continue;
                var key = NormalizeKey(item.Key);
                if (!aliases.Contains(key)) continue;

                var candidate = NormalizeCandidate(item.Value);
                if (candidate.Length == 0) continue;

                var path = NormalizeKey(item.Path);
                var score = 40;
                if (key == "skutext" || key == "skupropertiesname" || key == "propertiesname") score += 100;
                if (path.Contains("sku") || path.Contains("spec") || path.Contains("propert")) score += 55;
                if (candidate.Contains(":")) score += 35;
                if (candidate.Any(ch => ch >= 0x3400 && ch <= 0x9fff)) score += 15;
                score += Math.Min(25, candidate.Length / 4);

                if (score <= bestScore) continue;
                bestScore = score;
                best = candidate;
            }

            return best;
        }

        private static string ResolvePairs(IList<FlatValue> flat)
        {
            var output = new List<string>();
            foreach (var group in (flat ?? new List<FlatValue>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Value))
                .GroupBy(x => ParentPath(x.Path), StringComparer.OrdinalIgnoreCase))
            {
                var parent = group.Key ?? string.Empty;
                if (!IsSkuPath(parent) && !group.Any(x => IsSkuPath(x.Path))) continue;

                var name = Find(group, NameKeys);
                var value = Find(group, ValueKeys);
                if (string.IsNullOrWhiteSpace(name) && IsSkuPath(parent)) name = Find(group, new[] { "name", "title" });
                if (string.IsNullOrWhiteSpace(value) && IsSkuPath(parent)) value = Find(group, new[] { "selected", "content", "desc" });

                name = NormalizePart(name);
                value = NormalizePart(value);
                if (name.Length == 0 || value.Length == 0) continue;
                if (string.Equals(name, value, StringComparison.OrdinalIgnoreCase)) continue;
                if (IdentifierOnly(name) || IdentifierOnly(value)) continue;

                var candidate = NormalizeCandidate(name + ":" + value);
                if (candidate.Length == 0 || output.Contains(candidate, StringComparer.OrdinalIgnoreCase)) continue;
                output.Add(candidate);
            }

            return output.Count == 0 ? string.Empty : string.Join("; ", output.Take(8));
        }

        public static string NormalizeCandidate(string value)
        {
            value = NormalizePart(value);
            if (value.Length == 0 || value.Length > 600) return string.Empty;
            if (LooksLikeJson(value) || Regex.IsMatch(value, @"^https?://", RegexOptions.IgnoreCase)) return string.Empty;
            if (IdentifierOnly(value)) return string.Empty;

            value = Regex.Replace(
                value,
                @"^(?:SKU|规格名称|规格|销售属性|套餐|属性)\s*[:：]\s*",
                string.Empty,
                RegexOptions.IgnoreCase);
            value = value.Replace('：', ':');
            value = Regex.Replace(value, @"\s*:\s*", ":");
            value = Regex.Replace(value, @"\s*[;；]\s*", "; ");

            if (!value.Contains(":"))
            {
                var known = Regex.Match(
                    value,
                    @"^(专辑名称|套餐名称|套餐|期限|时长|会员类型|充值类型|账号类型|商品规格|版本)\s*(.+)$",
                    RegexOptions.IgnoreCase);
                if (known.Success && known.Groups[2].Value.Trim().Length > 0)
                {
                    value = known.Groups[1].Value.Trim() + ":" + known.Groups[2].Value.Trim();
                }
            }

            if (value.Length < 2
                || string.Equals(value, "SKU", StringComparison.OrdinalIgnoreCase)
                || value == "规格" || value == "属性" || value == "套餐")
            {
                return string.Empty;
            }

            return value.Length <= 240 ? value : value.Substring(0, 240);
        }

        private static void Walk(JToken token, string path, ICollection<FlatValue> output, int depth)
        {
            if (token == null || depth > 18 || output.Count >= 1800) return;
            if (token.Type == JTokenType.Object)
            {
                foreach (var property in ((JObject)token).Properties())
                {
                    Walk(property.Value, path.Length == 0 ? property.Name : path + "." + property.Name, output, depth + 1);
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

            var text = token.ToString().Trim();
            if (text.Length == 0 || text.Length > 12000) return;
            var key = path;
            var dot = key.LastIndexOf('.');
            if (dot >= 0) key = key.Substring(dot + 1);
            var bracket = key.IndexOf('[');
            if (bracket >= 0) key = key.Substring(0, bracket);
            output.Add(new FlatValue { Path = path, Key = key, Value = text.Length > 3000 ? text.Substring(0, 3000) : text });

            // 某些版本把 SKU 属性再次编码成 JSON 字符串。
            if (token.Type == JTokenType.String && depth < 16 && LooksLikeJson(text))
            {
                try { Walk(JToken.Parse(text), path + ".json", output, depth + 1); }
                catch { }
            }
        }

        private static string Find(IEnumerable<FlatValue> values, IEnumerable<string> aliases)
        {
            var set = new HashSet<string>((aliases ?? new string[0]).Select(NormalizeKey), StringComparer.OrdinalIgnoreCase);
            foreach (var item in values ?? new FlatValue[0])
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Value)) continue;
                if (set.Contains(NormalizeKey(item.Key))) return item.Value.Trim();
            }
            return string.Empty;
        }

        private static string ParentPath(string path)
        {
            path = path ?? string.Empty;
            var dot = path.LastIndexOf('.');
            return dot < 0 ? string.Empty : path.Substring(0, dot);
        }

        private static bool IsSkuPath(string value)
        {
            value = NormalizeKey(value);
            return value.Contains("sku") || value.Contains("spec") || value.Contains("propert")
                || value.Contains("salesattribute") || value.Contains("saleattribute")
                || value.Contains("attribute") || value.Contains("option");
        }

        private static string NormalizePart(string value)
        {
            value = (value ?? string.Empty).Trim();
            value = value.Trim('"', '\'', ',', ';', '，', '；', '{', '}', '[', ']');
            value = value.Replace("\\\"", "\"");
            return Regex.Replace(value.Replace("\r", " ").Replace("\n", " "), @"\s+", " ").Trim();
        }

        private static bool IdentifierOnly(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (Regex.IsMatch(value, @"^\d{5,}$")) return true;
            return Regex.IsMatch(value, @"^[a-f0-9\-]{16,}$", RegexOptions.IgnoreCase);
        }

        private static bool LooksLikeJson(string value)
        {
            value = (value ?? string.Empty).Trim();
            return (value.StartsWith("{") && value.EndsWith("}"))
                || (value.StartsWith("[") && value.EndsWith("]"));
        }

        private static string NormalizeKey(string value)
        {
            return Regex.Replace((value ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9]", string.Empty);
        }
    }
}
