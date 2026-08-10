using BotLib.Db.Sqlite;
using System;

namespace Bot.ShopScope
{
    internal sealed class ShopControlPlaneConnectionStore
    {
        private const string LegacyScope = "ai-control-plane";
        private const string UrlKey = "ControlPlaneUrl";
        private const string LegacyTokenKey = "ControlPlaneClientToken";

        private readonly ShopContext _shop;
        private readonly ShopTokenStore _tokens;
        private readonly ShopScopedSettingsStore _settings;

        public ShopControlPlaneConnectionStore(ShopContext shop, IShopScopedPathProvider paths)
        {
            if (shop == null) throw new ArgumentNullException(nameof(shop));
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            _shop = shop;
            _tokens = new ShopTokenStore(shop, paths);
            _settings = new ShopScopedSettingsStore(shop, paths);
        }

        public string GetServerUrl()
        {
            string value;
            if (_settings.TryGetString(UrlKey, out value))
            {
                return NormalizeUrl(value);
            }

            // Backward compatibility only: the first time a shop is opened after upgrade,
            // prefill the historical global URL. Saving the page writes the value into this
            // shop's encrypted settings.json and all subsequent reads stay shop-scoped.
            return GetLegacyGlobalServerUrl();
        }

        public void SetServerUrl(string serverUrl)
        {
            _settings.SetString(UrlKey, NormalizeUrl(serverUrl));
        }

        public bool HasShopServerUrl
        {
            get
            {
                string ignored;
                return _settings.TryGetString(UrlKey, out ignored);
            }
        }

        public bool TryGetToken(out string token, out string error)
        {
            return _tokens.TryLoad(out token, out error);
        }

        public void SaveToken(string token)
        {
            _tokens.Save(token);
        }

        public void ClearToken()
        {
            _tokens.Clear();
        }

        public bool HasToken
        {
            get { return _tokens.Exists; }
        }

        public string TokenFingerprint
        {
            get { return _tokens.GetFingerprint(); }
        }

        public string TokenPath
        {
            get { return _tokens.TokenPath; }
        }

        public static string GetLegacyGlobalToken()
        {
            return (PersistentParams.GetParam2Key(LegacyTokenKey, LegacyScope, string.Empty) ?? string.Empty).Trim();
        }

        public static string GetLegacyGlobalServerUrl()
        {
            return NormalizeUrl(PersistentParams.GetParam2Key(UrlKey, LegacyScope, string.Empty));
        }

        public static string NormalizeUrl(string serverUrl)
        {
            serverUrl = (serverUrl ?? string.Empty).Trim().TrimEnd('/');
            if (serverUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                serverUrl = serverUrl.Substring(0, serverUrl.Length - 3).TrimEnd('/');
            return serverUrl;
        }
    }
}
