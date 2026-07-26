using Bot.Options;
using BotLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal sealed class RemoteHandoffRule
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("enabled")]
        public bool Enabled { get; set; }

        [JsonProperty("rule_type")]
        public string RuleType { get; set; }

        [JsonProperty("keyword")]
        public string Keyword { get; set; }

        [JsonProperty("match_mode")]
        public string MatchMode { get; set; }

        [JsonProperty("risk_terms")]
        public string RiskTerms { get; set; }

        [JsonProperty("exceptions")]
        public string Exceptions { get; set; }

        [JsonProperty("safe_reply")]
        public string SafeReply { get; set; }

        [JsonProperty("note")]
        public string Note { get; set; }

        [JsonProperty("sort_order")]
        public int SortOrder { get; set; }
    }

    /// <summary>
    /// 从统一 API 服务读取企业微信页面维护的转人工规则，并同步到本机规则关键词。
    /// 消息处理本身只读内存缓存，不在买家等待期间访问网络。
    /// </summary>
    internal static class HandoffRuleRemoteConfigService
    {
        private const string Scope = "ai-control-plane";
        private const string UrlKey = "ControlPlaneUrl";
        private const string TokenKey = "ControlPlaneClientToken";
        private const string CacheKey = "HandoffRemoteRulesJson";
        private const string DefaultAccountSafeReply =
            "可以的，月卡可以给朋友或其他账号充值，您再拍对应月卡即可；下单后按页面提示提供需要充值的账号。";
        private const string BuiltInAccountRiskTerms =
            "密码|验证码|登录|登陆|找回|被盗|冻结|封禁|绑定|解绑|实名|身份证|泄露|安全|申诉|修改账号|换绑";
        private const string BuiltInAccountPurchaseExceptions =
            "另一个账号|其他账号|别的账号|朋友账号|好友账号|给朋友|给别人|帮朋友|帮别人|再拍|再买|购买|充值|充到|月卡";

        private static readonly object Sync = new object();
        private static List<RemoteHandoffRule> _rules = new List<RemoteHandoffRule>();
        private static string _revision = string.Empty;
        private static bool _hasAuthoritativeSnapshot;
        private static int _initialized;

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            LoadCachedRules();
            Task.Run(PollLoopAsync);
            Log.Info("服务端转人工规则同步已启动：本机消息判断只使用内存缓存，后台定时刷新。" );
        }

        /// <summary>
        /// 调用发生在旧本地规则已经命中之后。若服务端规则把该命中定义为安全例外，
        /// 将决策改成固定安全答复并阻止创建企业微信工单。
        /// </summary>
        public static bool TryApplySafeAutoReply(
            string question,
            AutoReplyRuleDecision decision,
            out string detail)
        {
            detail = string.Empty;
            if (decision == null || !decision.Matched || string.IsNullOrWhiteSpace(decision.HitKeyword))
            {
                return false;
            }

            List<RemoteHandoffRule> snapshot;
            bool authoritative;
            lock (Sync)
            {
                snapshot = _rules.Select(Clone).ToList();
                authoritative = _hasAuthoritativeSnapshot;
            }

            var rule = snapshot
                .Where(x => x != null && x.Enabled)
                .OrderBy(x => x.SortOrder)
                .ThenBy(x => x.Id)
                .FirstOrDefault(x => Same(x.Keyword, decision.HitKeyword));

            if (rule != null)
            {
                string riskHit;
                string exceptionHit;
                var hasRisk = ContainsAny(question, rule.RiskTerms, out riskHit);
                var hasException = ContainsAny(question, rule.Exceptions, out exceptionHit);
                var contextual = string.Equals(rule.MatchMode, "sensitive_context", StringComparison.OrdinalIgnoreCase);

                // 明确的密码、验证码、登录安全等风险语境始终优先，不允许被“给朋友”等词绕过。
                if (contextual && hasRisk)
                {
                    UpdateReason(decision, rule);
                    detail = "服务端敏感语境成立：" + riskHit;
                    return false;
                }

                if (hasException)
                {
                    ApplySafeReply(decision, rule.SafeReply, rule.Keyword, exceptionHit);
                    detail = "服务端规则例外：" + exceptionHit;
                    return true;
                }

                // sensitive_context 在没有风险词时仍保持保守：只有配置了明确例外才自动回答。
                UpdateReason(decision, rule);
                detail = contextual
                    ? "服务端敏感语境未充分确认，保持人工确认"
                    : "服务端包含规则命中";
                return false;
            }

            // 服务端尚未连通时仍保护已确认的常见业务场景，防止“另一个账号充值”被单词“账号”误伤。
            if (!authoritative && Same(decision.HitKeyword, "账号"))
            {
                string riskHit;
                string exceptionHit;
                var hasRisk = ContainsAny(question, BuiltInAccountRiskTerms, out riskHit);
                var hasPurchaseException = ContainsAny(question, BuiltInAccountPurchaseExceptions, out exceptionHit);
                if (!hasRisk && hasPurchaseException)
                {
                    ApplySafeReply(decision, DefaultAccountSafeReply, "账号", exceptionHit);
                    detail = "内置账号购买例外：" + exceptionHit;
                    return true;
                }
            }

            return false;
        }

        private static async Task PollLoopAsync()
        {
            while (true)
            {
                try
                {
                    string serverUrl;
                    string token;
                    ReadConnection(out serverUrl, out token);
                    if (!string.IsNullOrWhiteSpace(serverUrl) && !string.IsNullOrWhiteSpace(token))
                    {
                        await PollOnceAsync(serverUrl, token);
                        await Task.Delay(TimeSpan.FromMinutes(1));
                    }
                    else
                    {
                        await Task.Delay(TimeSpan.FromSeconds(15));
                    }
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("刷新服务端转人工规则失败，继续使用最近缓存：" + Safe(ex.Message), 20);
                    await Task.Delay(TimeSpan.FromSeconds(30));
                }
            }
        }

        private static async Task PollOnceAsync(string serverUrl, string token)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var handler = new HttpClientHandler
            {
                UseProxy = true,
                Proxy = WebRequest.DefaultWebProxy
            })
            using (var http = new HttpClient(handler))
            using (var request = new HttpRequestMessage(
                HttpMethod.Get,
                serverUrl.TrimEnd('/') + "/api/runtime/v1/handoff/rules"))
            {
                http.Timeout = TimeSpan.FromSeconds(20);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                request.Headers.TryAddWithoutValidation("User-Agent", "qianniu-bot-handoff-rules/1.0");
                using (var response = await http.SendAsync(request))
                {
                    var body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new Exception("HTTP " + (int)response.StatusCode + " " + Safe(body));
                    }
                    var root = JObject.Parse(body);
                    var rules = root["rules"] == null
                        ? new List<RemoteHandoffRule>()
                        : root["rules"].ToObject<List<RemoteHandoffRule>>();
                    SetRules(
                        rules ?? new List<RemoteHandoffRule>(),
                        Convert.ToString(root["revision"]),
                        body,
                        "服务端");
                }
            }
        }

        private static void LoadCachedRules()
        {
            try
            {
                var json = BotLib.Db.Sqlite.PersistentParams.GetParam2Key(CacheKey, Scope, string.Empty);
                if (string.IsNullOrWhiteSpace(json)) return;
                var root = JObject.Parse(json);
                var rules = root["rules"] == null
                    ? new List<RemoteHandoffRule>()
                    : root["rules"].ToObject<List<RemoteHandoffRule>>();
                SetRules(
                    rules ?? new List<RemoteHandoffRule>(),
                    Convert.ToString(root["revision"]),
                    json,
                    "本地缓存");
            }
            catch (Exception ex)
            {
                Log.Info("读取服务端转人工规则缓存失败，等待联网刷新：" + Safe(ex.Message));
            }
        }

        private static void SetRules(
            List<RemoteHandoffRule> rules,
            string revision,
            string rawJson,
            string source)
        {
            rules = (rules ?? new List<RemoteHandoffRule>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Keyword))
                .OrderBy(x => x.SortOrder)
                .ThenBy(x => x.Id)
                .Take(300)
                .Select(Clone)
                .ToList();

            var changed = false;
            lock (Sync)
            {
                changed = !string.Equals(_revision, revision ?? string.Empty, StringComparison.Ordinal)
                    || JsonConvert.SerializeObject(_rules) != JsonConvert.SerializeObject(rules);
                _rules = rules;
                _revision = (revision ?? string.Empty).Trim();
                _hasAuthoritativeSnapshot = true;
            }

            if (!string.IsNullOrWhiteSpace(rawJson))
            {
                BotLib.Db.Sqlite.PersistentParams.TrySaveParam2Key(CacheKey, Scope, rawJson);
            }
            SyncKeywordsToLocalConfig(rules);
            if (changed)
            {
                Log.Info("服务端转人工规则已应用: source=" + source
                    + ", revision=" + _revision
                    + ", enabled=" + rules.Count(x => x.Enabled));
            }
        }

        private static void SyncKeywordsToLocalConfig(IEnumerable<RemoteHandoffRule> rules)
        {
            try
            {
                var enabled = (rules ?? new List<RemoteHandoffRule>())
                    .Where(x => x != null && x.Enabled && !string.IsNullOrWhiteSpace(x.Keyword))
                    .ToList();
                var manual = string.Join(",", enabled
                    .Where(x => string.Equals(x.RuleType, "manual", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x.SortOrder)
                    .Select(x => x.Keyword.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase));
                var confirm = string.Join(",", enabled
                    .Where(x => string.Equals(x.RuleType, "confirm", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x.SortOrder)
                    .Select(x => x.Keyword.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase));

                var cfg = BotFeatureStore.GetAutoReplyRules();
                if (cfg == null) return;
                if (string.Equals(cfg.ManualKeywords ?? string.Empty, manual, StringComparison.Ordinal)
                    && string.Equals(cfg.NoAutoReplyKeywords ?? string.Empty, confirm, StringComparison.Ordinal))
                {
                    return;
                }
                cfg.ManualKeywords = manual;
                cfg.NoAutoReplyKeywords = confirm;
                BotFeatureStore.SaveAutoReplyRules(cfg);
                Log.Info("服务端转人工关键词已同步到本机规则：强制=" + CountWords(manual)
                    + "，仅人工确认=" + CountWords(confirm));
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("同步服务端转人工关键词到本机失败：" + Safe(ex.Message), 10);
            }
        }

        private static void ApplySafeReply(
            AutoReplyRuleDecision decision,
            string reply,
            string keyword,
            string exceptionHit)
        {
            decision.AllowAutoReply = true;
            decision.UseAiReply = false;
            decision.IsOffHours = false;
            decision.ReplyText = string.IsNullOrWhiteSpace(reply)
                ? DefaultAccountSafeReply
                : reply.Trim();
            decision.Reason = "命中可自动回答的转人工规则例外：" + keyword
                + (string.IsNullOrWhiteSpace(exceptionHit) ? string.Empty : "（" + exceptionHit + "）");
        }

        private static void UpdateReason(AutoReplyRuleDecision decision, RemoteHandoffRule rule)
        {
            decision.HitKeyword = rule.Keyword == null ? decision.HitKeyword : rule.Keyword.Trim();
            decision.Reason = string.Equals(rule.RuleType, "manual", StringComparison.OrdinalIgnoreCase)
                ? "命中强制转人工关键词：" + decision.HitKeyword
                : "命中仅人工确认关键词：" + decision.HitKeyword;
        }

        private static bool ContainsAny(string text, string values, out string hit)
        {
            text = text ?? string.Empty;
            foreach (var value in Split(values))
            {
                if (text.IndexOf(value, StringComparison.OrdinalIgnoreCase) < 0) continue;
                hit = value;
                return true;
            }
            hit = string.Empty;
            return false;
        }

        private static IEnumerable<string> Split(string values)
        {
            return (values ?? string.Empty)
                .Split(new[] { '|', ',', '，', ';', '；', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static int CountWords(string values)
        {
            return Split(values).Count();
        }

        private static bool Same(string left, string right)
        {
            return string.Equals(
                (left ?? string.Empty).Trim(),
                (right ?? string.Empty).Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        private static RemoteHandoffRule Clone(RemoteHandoffRule rule)
        {
            if (rule == null) return null;
            return new RemoteHandoffRule
            {
                Id = rule.Id,
                Enabled = rule.Enabled,
                RuleType = rule.RuleType ?? string.Empty,
                Keyword = rule.Keyword ?? string.Empty,
                MatchMode = rule.MatchMode ?? string.Empty,
                RiskTerms = rule.RiskTerms ?? string.Empty,
                Exceptions = rule.Exceptions ?? string.Empty,
                SafeReply = rule.SafeReply ?? string.Empty,
                Note = rule.Note ?? string.Empty,
                SortOrder = rule.SortOrder
            };
        }

        private static void ReadConnection(out string serverUrl, out string token)
        {
            serverUrl = BotLib.Db.Sqlite.PersistentParams.GetParam2Key(UrlKey, Scope, string.Empty);
            token = BotLib.Db.Sqlite.PersistentParams.GetParam2Key(TokenKey, Scope, string.Empty);
            serverUrl = (serverUrl ?? string.Empty).Trim().TrimEnd('/');
            if (serverUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                serverUrl = serverUrl.Substring(0, serverUrl.Length - 3).TrimEnd('/');
            }
            token = (token ?? string.Empty).Trim();
        }

        private static string Safe(string value)
        {
            value = Regex.Replace((value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim(), @"\s+", " ");
            return value.Length <= 300 ? value : value.Substring(0, 300) + "...";
        }
    }
}
