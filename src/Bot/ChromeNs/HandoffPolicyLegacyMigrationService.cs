using BotLib;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    /// <summary>
    /// One-time compatibility migration for installations that previously kept
    /// AI handoff rules in the Enterprise WeCom control plane. It runs only
    /// until a successful authenticated import creates the local marker. There
    /// is no polling and message processing never depends on this network call.
    /// </summary>
    internal static class HandoffPolicyLegacyMigrationService
    {
        private const string Scope = "ai-control-plane";
        private const string UrlKey = "ControlPlaneUrl";
        private const string TokenKey = "ControlPlaneClientToken";
        private static int _started;

        public static void StartOnce()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            if (File.Exists(GetMarkerPath())) return;
            Task.Run((Func<Task>)TryMigrateAsync);
        }

        private static async Task TryMigrateAsync()
        {
            try
            {
                string serverUrl;
                string token;
                ReadConnection(out serverUrl, out token);
                if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(token))
                {
                    Log.Info("旧服务端转人工策略暂未迁移：控制面地址或客户端令牌尚未配置；下次启动继续尝试。");
                    return;
                }

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
                    http.Timeout = TimeSpan.FromSeconds(10);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    request.Headers.TryAddWithoutValidation("Accept", "application/json");
                    request.Headers.TryAddWithoutValidation("User-Agent", "qianniu-bot-handoff-policy-migration/1.0");

                    using (var response = await http.SendAsync(request).ConfigureAwait(false))
                    {
                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                        {
                            throw new Exception("HTTP " + (int)response.StatusCode + " " + Safe(body));
                        }

                        var root = JObject.Parse(body);
                        var array = root["rules"] as JArray;
                        if (array == null)
                            throw new Exception("服务端迁移响应缺少 rules 数组");

                        var rules = array.ToObject<List<RemoteHandoffRule>>()
                            ?? new List<RemoteHandoffRule>();
                        HandoffRuleRemoteConfigService.SaveRules(rules);
                        WriteMarker(
                            Convert.ToString(root["revision"]),
                            rules.Count,
                            serverUrl);
                        Log.Info("旧服务端AI转人工策略已一次性迁移到本机：rules=" + rules.Count
                            + "。后续只读取本机 handoff-policy.json，不再轮询服务端。");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount(
                    "旧服务端AI转人工策略迁移失败，下次启动继续尝试：" + Safe(ex.Message),
                    10);
            }
        }

        private static void ReadConnection(out string serverUrl, out string token)
        {
            serverUrl = BotLib.Db.Sqlite.PersistentParams.GetParam2Key(UrlKey, Scope, string.Empty);
            token = BotLib.Db.Sqlite.PersistentParams.GetParam2Key(TokenKey, Scope, string.Empty);
            serverUrl = (serverUrl ?? string.Empty).Trim().TrimEnd('/');
            if (serverUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                serverUrl = serverUrl.Substring(0, serverUrl.Length - 3).TrimEnd('/');
            token = (token ?? string.Empty).Trim();
        }

        private static void WriteMarker(string revision, int count, string serverUrl)
        {
            var path = GetMarkerPath();
            var directory = Path.GetDirectoryName(path);
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            var payload = new JObject
            {
                ["schema"] = "qianniu-ai-bot.handoff-policy-migration",
                ["migratedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["revision"] = revision ?? string.Empty,
                ["ruleCount"] = count,
                ["source"] = serverUrl ?? string.Empty
            }.ToString();
            var temp = path + ".tmp";
            File.WriteAllText(temp, payload, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        private static string GetMarkerPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QianniuAiBot",
                "data",
                "handoff-policy-migration.json");
        }

        private static string Safe(string value)
        {
            value = Regex.Replace(
                (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim(),
                @"\s+",
                " ");
            return value.Length <= 300 ? value : value.Substring(0, 300) + "...";
        }
    }
}
