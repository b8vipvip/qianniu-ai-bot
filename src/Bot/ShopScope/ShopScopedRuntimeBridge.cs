using BotLib;
using BotLib.Extensions;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace Bot
{
    public partial class App
    {
        private readonly object _shopScopedRuntimeBridgeBootstrap =
            ShopScope.ShopScopedRuntimeBridge.InitializeForApp();
    }
}

namespace Bot.ShopScope
{
    internal static class ShopScopedRuntimeBridge
    {
        private static readonly ShopScopedPathProvider Paths = new ShopScopedPathProvider();
        private static readonly ConcurrentDictionary<string, LogWriter> Writers =
            new ConcurrentDictionary<string, LogWriter>(StringComparer.Ordinal);
        private static int _initialized;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                ScopedDataPathRouter.Configure(TryResolveDataRoot);
                ScopedLogRouter.Configure(WriteShopLog);
            }
            return new object();
        }

        private static bool TryResolveDataRoot(out string dataRoot)
        {
            dataRoot = string.Empty;
            var shop = ShopSettingsScope.Current;
            if (shop == null) return false;
            dataRoot = Paths.GetCompatibilityDataRoot(shop);
            return true;
        }

        private static void WriteShopLog(string tag, string text)
        {
            var shop = ShopSettingsScope.Current;
            if (shop == null) return;
            var writer = Writers.GetOrAdd(shop.ShopKey, key =>
            {
                var path = Path.Combine(Paths.GetLogRoot(shop), "runtime.txt");
                return new LogWriter(path, true, 8 * 1024 * 1024)
                {
                    LimitSameStringWriteCount = false
                };
            });
            writer.Write("[shop=" + shop.ShopKey + "] " + (text ?? string.Empty), tag ?? "Info");
        }
    }
}
