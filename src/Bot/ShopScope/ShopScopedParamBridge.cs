using BotLib.Db.Sqlite;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace Bot
{
    public partial class App
    {
        private readonly object _shopScopedParamBridgeBootstrap =
            ShopScope.ShopScopedParamBridge.InitializeForApp();
    }
}

namespace Bot.ShopScope
{
    internal static class ShopScopedParamBridge
    {
        private const string AiSubKey = "ai";
        private const string AiEndpointListKey = "AiEndpointListJson";

        private static readonly HashSet<string> AllowedSubKeys =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "ai",
                "feature",
                "shop-cloud",
                "shop-runtime"
            };

        private static readonly ShopScopedPathProvider Paths = new ShopScopedPathProvider();
        private static int _initialized;

        public static object InitializeForApp()
        {
            if (System.Threading.Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                ScopedParamRouter.Configure(TryRead, TryWrite);
            }
            return new object();
        }

        private static bool TryRead(string masterKey, string subKey, out string value)
        {
            value = null;
            var shop = ShopSettingsScope.Current;
            if (shop == null || !AllowedSubKeys.Contains(subKey ?? string.Empty)) return false;

            var store = new ShopScopedSettingsStore(shop, Paths);
            var hasScopedValue = store.TryGetString(masterKey, out value);
            if (hasScopedValue && !string.IsNullOrWhiteSpace(value)) return true;

            // The modern product keeps upstream providers/models/keys in the API Control Plane.
            // Older reply code still reads AiEndpointStore. When this shop has no explicit local
            // endpoint list, expose an in-memory OpenAI-compatible endpoint that points to the
            // ShopKey-bound Control Plane proxy. The Bot token is never persisted into the AI
            // settings file; it is materialized only for this read.
            if (string.Equals(subKey, AiSubKey, StringComparison.Ordinal)
                && string.Equals(masterKey, AiEndpointListKey, StringComparison.Ordinal))
            {
                string generated;
                if (TryBuildControlPlaneEndpointJson(shop, out generated))
                {
                    value = generated;
                    return true;
                }
            }

            return hasScopedValue;
        }

        private static bool TryBuildControlPlaneEndpointJson(ShopContext shop, out string json)
        {
            json = string.Empty;
            try
            {
                var connection = new ShopControlPlaneConnectionStore(shop, Paths);
                var serverUrl = connection.GetServerUrl();
                string token;
                string tokenError;
                if (string.IsNullOrWhiteSpace(serverUrl)
                    || !connection.TryGetToken(out token, out tokenError)
                    || string.IsNullOrWhiteSpace(token))
                {
                    return false;
                }

                var endpoint = new
                {
                    Id = "control-plane-" + shop.ShopKey,
                    Name = "Bot服务端统一AI",
                    Type = "ControlPlane",
                    BaseUrl = serverUrl.TrimEnd('/')
                        + "/api/runtime/v1/ai-proxy/"
                        + Uri.EscapeDataString(shop.ShopKey),
                    ApiKey = token.Trim(),
                    Model = "text-default",
                    TextModel = "text-default",
                    VisionModel = string.Empty,
                    SupportsVision = false,
                    MaxImageSizeMb = 5,
                    VisionTimeoutSeconds = 45,
                    SystemPrompt = string.Empty,
                    Enabled = true,
                    Priority = 1,
                    Weight = 1,
                    TimeoutSeconds = 70,
                    RetryCount = 0,
                    LastStatus = "由Bot服务端统一路由",
                    LastLatencyMs = 0
                };
                json = JsonConvert.SerializeObject(new[] { endpoint }, Formatting.None);
                return true;
            }
            catch (Exception ex)
            {
                BotLib.Log.ErrorWithMaxCount(
                    "构造本店Control Plane AI路由失败：" + Safe(ex.Message, 220),
                    10);
                return false;
            }
        }

        private static bool TryWrite(string masterKey, string subKey, string value)
        {
            var shop = ShopSettingsScope.Current;
            if (shop == null || !AllowedSubKeys.Contains(subKey ?? string.Empty)) return false;
            new ShopScopedSettingsStore(shop, Paths).SetString(masterKey, value ?? string.Empty);
            return true;
        }

        private static string Safe(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }
}
