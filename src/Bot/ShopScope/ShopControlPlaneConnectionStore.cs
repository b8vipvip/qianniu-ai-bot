using BotLib.Db.Sqlite;
using System;
using System.Configuration;

namespace Bot.ShopScope
{
    internal sealed class ShopControlPlaneConnectionStore
    {
        private const string LegacyScope = "ai-control-plane";
        private const string UrlKey = "ControlPlaneUrl";
        private const string LegacyTokenKey = "ControlPlaneClientToken";
        private const string DefaultUrlSettingKey = "BotControlPlaneDefaultUrl";
        private const string ServerUrlEnvironmentKey = "QIANNIU_BOT_SERVER_URL";
        private const string BuiltInDefaultServerUrl = "http://aboter.mv3.cn";
        private const string ObsoleteBuiltInHost = "botserver.mv3.cn";
        private const string CurrentBuiltInHost = "aboter.mv3.cn";

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
            // The API Control Plane is a program/deployment endpoint, not a shop secret.
            // Every shop on one Bot installation uses the same endpoint; only the token
            // remains ShopKey-scoped. Preserve existing installs by migrating the first
            // historical per-shop URL into the old global slot once.
            var global = GetProgramServerUrl();
            if (!string.IsNullOrWhiteSpace(global)) return global;

            string scoped;
            if (_settings.TryGetString(UrlKey, out scoped))
            {
                scoped = NormalizeUrl(scoped);
                if (!string.IsNullOrWhiteSpace(scoped))
                {
                    SaveProgramServerUrl(scoped);
                    return scoped;
                }
            }

            return GetBuiltInServerUrl();
        }

        // Compatibility entry point for older callers. New UI no longer exposes an
        // editable per-shop server URL; writes here intentionally become program-global.
        public void SetServerUrl(string serverUrl)
        {
            SaveProgramServerUrl(serverUrl);
        }

        public bool HasShopServerUrl
        {
            get { return !string.IsNullOrWhiteSpace(GetServerUrl()); }
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
            var configured = GetProgramServerUrl();
            return !string.IsNullOrWhiteSpace(configured)
                ? configured
                : GetBuiltInServerUrl();
        }

        public static string GetProgramServerUrl()
        {
            var environment = NormalizeUrl(Environment.GetEnvironmentVariable(ServerUrlEnvironmentKey));
            if (!string.IsNullOrWhiteSpace(environment)) return environment;

            return NormalizeUrl(PersistentParams.GetParam2Key(UrlKey, LegacyScope, string.Empty));
        }

        public static void SaveProgramServerUrl(string serverUrl)
        {
            var normalized = NormalizeUrl(serverUrl);
            if (string.IsNullOrWhiteSpace(normalized)) return;
            PersistentParams.TrySaveParam2Key(UrlKey, LegacyScope, normalized);
        }

        public static string GetBuiltInServerUrl()
        {
            try
            {
                var configured = NormalizeUrl(ConfigurationManager.AppSettings[DefaultUrlSettingKey]);
                if (!string.IsNullOrWhiteSpace(configured)) return configured;
            }
            catch { }
            return NormalizeUrl(BuiltInDefaultServerUrl);
        }

        public static string NormalizeUrl(string serverUrl)
        {
            serverUrl = (serverUrl ?? string.Empty).Trim().TrimEnd('/');
            if (serverUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                serverUrl = serverUrl.Substring(0, serverUrl.Length - 3).TrimEnd('/');

            Uri parsed;
            if (Uri.TryCreate(serverUrl, UriKind.Absolute, out parsed)
                && string.Equals(parsed.Host, ObsoleteBuiltInHost, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var builder = new UriBuilder(parsed) { Host = CurrentBuiltInHost };
                    serverUrl = builder.Uri.AbsoluteUri.TrimEnd('/');
                }
                catch { }
            }

            return serverUrl;
        }
    }
}
