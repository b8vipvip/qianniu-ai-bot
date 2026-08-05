using Bot.ShopScope;
using BotLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Bot
{
    public partial class App
    {
        private readonly object _botWebBotEnabledBootstrap =
            ChromeNs.BotWebBotEnabledSyncService.InitializeForApp();
    }
}

namespace Bot.ChromeNs
{
    internal static class BotWebBotEnabledSyncService
    {
        private static readonly ShopScopedPathProvider Paths = new ShopScopedPathProvider();
        private static readonly ShopProfileStore Profiles = new ShopProfileStore(Paths);
        private static readonly ConcurrentDictionary<string, byte> Syncing =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        private static Timer _timer;
        private static int _initialized;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return new object();
            _timer = new Timer(_ => QueueAll(), null, 1800, 2500);
            Log.Info("Web端 Bot 总开关同步已启动：每个 ShopKey 独立下发并回传 Windows 当前状态。" );
            return new object();
        }

        private static void QueueAll()
        {
            foreach (var shop in SnapshotActiveShops())
            {
                if (!Syncing.TryAdd(shop.ShopKey, 0)) continue;
                var captured = shop;
                Task.Run(async () =>
                {
                    try { await SyncOnceAsync(captured); }
                    catch (Exception ex)
                    {
                        using (ShopSettingsScope.Enter(captured))
                            Log.ErrorWithMaxCount("本店 Web Bot 总开关同步失败：" + Safe(ex.Message, 300), 20);
                    }
                    finally
                    {
                        byte ignored;
                        Syncing.TryRemove(captured.ShopKey, out ignored);
                    }
                });
            }
        }

        private static IList<ShopContext> SnapshotActiveShops()
        {
            var result = new Dictionary<string, ShopContext>(StringComparer.Ordinal);
            try
            {
                var qns = QN.QNSet == null ? new QN[0] : QN.QNSet.ToArray();
                foreach (var qn in qns)
                {
                    if (qn == null || qn.Seller == null) continue;
                    try
                    {
                        var shop = Profiles.GetOrCreate(ShopIdentityResolver.Resolve(qn.Seller)).ToContext();
                        result[shop.ShopKey] = shop;
                    }
                    catch { }
                }
            }
            catch { }
            return result.Values.ToList();
        }

        private static async Task SyncOnceAsync(ShopContext shop)
        {
            using (ShopSettingsScope.Enter(shop))
            {
                var connection = new ShopControlPlaneConnectionStore(shop, Paths);
                var serverUrl = connection.GetServerUrl();
                string token;
                string tokenError;
                if (!connection.TryGetToken(out token, out tokenError)
                    || string.IsNullOrWhiteSpace(serverUrl)
                    || string.IsNullOrWhiteSpace(token)) return;

                var currentEnabled = Params.Robot.CanUseRobot;
                var payload = new JObject
                {
                    ["current_enabled"] = currentEnabled
                };

                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                using (var handler = new HttpClientHandler { UseProxy = true, Proxy = WebRequest.DefaultWebProxy })
                using (var http = new HttpClient(handler))
                using (var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    serverUrl.TrimEnd('/') + "/api/runtime/v1/bot-web/bot-enabled-sync"))
                {
                    http.Timeout = TimeSpan.FromSeconds(20);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    request.Headers.TryAddWithoutValidation("Accept", "application/json");
                    request.Headers.TryAddWithoutValidation("User-Agent", "qianniu-bot-enabled-sync/1.0");
                    request.Headers.TryAddWithoutValidation("X-Shop-Key", shop.ShopKey);
                    request.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");

                    using (var response = await http.SendAsync(request))
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        if (!response.IsSuccessStatusCode)
                            throw new Exception("HTTP " + (int)response.StatusCode + " " + Safe(body, 300));
                        var root = JObject.Parse(body);
                        var desired = root.Value<bool?>("desired_enabled");
                        if (!desired.HasValue || desired.Value == currentEnabled) return;

                        Params.Robot.CanUseRobot = desired.Value;
                        Log.Info("本店 Web端 Bot 总开关已应用: shop=" + shop.ShopKey
                            + ", enabled=" + desired.Value);
                    }
                }
            }
        }

        private static string Safe(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }
}
