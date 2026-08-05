using BotLib.Db.Sqlite;
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
        private static readonly HashSet<string> AllowedSubKeys =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "ai",
                "feature"
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
            return store.TryGetString(masterKey, out value);
        }

        private static bool TryWrite(string masterKey, string subKey, string value)
        {
            var shop = ShopSettingsScope.Current;
            if (shop == null || !AllowedSubKeys.Contains(subKey ?? string.Empty)) return false;

            var store = new ShopScopedSettingsStore(shop, Paths);
            store.SetString(masterKey, value ?? string.Empty);
            return true;
        }
    }
}
