using Newtonsoft.Json;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Bot.ShopScope
{
    internal sealed class ShopTokenStore
    {
        private const string Schema = "qianniu-ai-bot.shop-token";
        private const int SchemaVersion = 1;
        private const string Algorithm = "DPAPI-CurrentUser";
        private static readonly object Sync = new object();

        private readonly ShopContext _shop;
        private readonly IShopScopedPathProvider _paths;
        private readonly string _path;

        public ShopTokenStore(ShopContext shop, IShopScopedPathProvider paths)
        {
            if (shop == null) throw new ArgumentNullException(nameof(shop));
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            _shop = shop;
            _paths = paths;
            _path = _paths.GetConfigPath(_shop, "control-plane-token.json");
        }

        public bool Exists
        {
            get { return File.Exists(_path); }
        }

        public string TokenPath
        {
            get { return _path; }
        }

        public void Save(string token)
        {
            token = (token ?? string.Empty).Trim();
            if (token.Length < 16) throw new ArgumentException("Bot 客户端令牌长度无效。", nameof(token));

            var plain = Encoding.UTF8.GetBytes(token);
            var protectedBytes = ProtectedData.Protect(
                plain,
                Entropy(),
                DataProtectionScope.CurrentUser);
            Array.Clear(plain, 0, plain.Length);

            var document = new TokenDocument
            {
                Schema = Schema,
                SchemaVersion = SchemaVersion,
                ShopKey = _shop.ShopKey,
                Algorithm = Algorithm,
                ProtectedToken = Convert.ToBase64String(protectedBytes),
                Fingerprint = Fingerprint(token),
                UpdatedAtUtc = DateTime.UtcNow
            };
            Array.Clear(protectedBytes, 0, protectedBytes.Length);

            lock (Sync)
            {
                AtomicWrite(_path, JsonConvert.SerializeObject(document, Formatting.Indented));
            }
        }

        public bool TryLoad(out string token, out string error)
        {
            token = string.Empty;
            error = string.Empty;
            if (!File.Exists(_path)) return false;

            try
            {
                TokenDocument document;
                lock (Sync)
                {
                    document = JsonConvert.DeserializeObject<TokenDocument>(
                        File.ReadAllText(_path, Encoding.UTF8));
                }
                Validate(document);

                var protectedBytes = Convert.FromBase64String(document.ProtectedToken);
                var plain = ProtectedData.Unprotect(
                    protectedBytes,
                    Entropy(),
                    DataProtectionScope.CurrentUser);
                try
                {
                    token = Encoding.UTF8.GetString(plain).Trim();
                }
                finally
                {
                    Array.Clear(protectedBytes, 0, protectedBytes.Length);
                    Array.Clear(plain, 0, plain.Length);
                }
                if (token.Length < 16)
                    throw new InvalidDataException("解密后的 Bot 客户端令牌无效。");
                if (!string.Equals(document.Fingerprint, Fingerprint(token), StringComparison.Ordinal))
                    throw new InvalidDataException("Bot 客户端令牌指纹校验失败。");
                return true;
            }
            catch (Exception ex)
            {
                token = string.Empty;
                error = "无法读取本店令牌：" + ex.Message;
                return false;
            }
        }

        public string GetFingerprint()
        {
            if (!File.Exists(_path)) return string.Empty;
            try
            {
                TokenDocument document;
                lock (Sync)
                {
                    document = JsonConvert.DeserializeObject<TokenDocument>(
                        File.ReadAllText(_path, Encoding.UTF8));
                }
                Validate(document);
                return document.Fingerprint ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        public void Clear()
        {
            lock (Sync)
            {
                if (File.Exists(_path)) File.Delete(_path);
            }
        }

        public static string Fingerprint(string token)
        {
            token = (token ?? string.Empty).Trim();
            if (token.Length == 0) return string.Empty;
            using (var sha = SHA256.Create())
            {
                var digest = sha.ComputeHash(Encoding.UTF8.GetBytes(token));
                return BitConverter.ToString(digest)
                    .Replace("-", string.Empty)
                    .ToLowerInvariant()
                    .Substring(0, 12);
            }
        }

        private byte[] Entropy()
        {
            return Encoding.UTF8.GetBytes(
                "qianniu-ai-bot|control-plane-token|" + _shop.ShopKey);
        }

        private void Validate(TokenDocument document)
        {
            if (document == null
                || !string.Equals(document.Schema, Schema, StringComparison.Ordinal)
                || document.SchemaVersion != SchemaVersion
                || !string.Equals(document.ShopKey, _shop.ShopKey, StringComparison.Ordinal)
                || !string.Equals(document.Algorithm, Algorithm, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(document.ProtectedToken)
                || string.IsNullOrWhiteSpace(document.Fingerprint))
            {
                throw new InvalidDataException("店铺令牌文件格式无效或 ShopKey 不匹配。");
            }
        }

        private static void AtomicWrite(string path, string content)
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("令牌目录不存在。");
            Directory.CreateDirectory(directory);

            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, content ?? string.Empty, new UTF8Encoding(false));
            try
            {
                if (File.Exists(path))
                {
                    var backup = path + ".bak";
                    try
                    {
                        File.Replace(temp, path, backup, true);
                        return;
                    }
                    catch (PlatformNotSupportedException)
                    {
                    }
                    catch (IOException)
                    {
                    }
                    File.Copy(temp, path, true);
                    File.Delete(temp);
                    return;
                }
                File.Move(temp, path);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        private sealed class TokenDocument
        {
            [JsonProperty("schema")]
            public string Schema { get; set; }

            [JsonProperty("schema_version")]
            public int SchemaVersion { get; set; }

            [JsonProperty("shop_key")]
            public string ShopKey { get; set; }

            [JsonProperty("algorithm")]
            public string Algorithm { get; set; }

            [JsonProperty("protected_token")]
            public string ProtectedToken { get; set; }

            [JsonProperty("fingerprint")]
            public string Fingerprint { get; set; }

            [JsonProperty("updated_at_utc")]
            public DateTime UpdatedAtUtc { get; set; }
        }
    }
}
