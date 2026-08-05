using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Bot.ShopScope
{
    internal sealed class ShopScopedSettingsStore
    {
        private const string Schema = "qianniu-ai-bot.shop-settings";
        private const int SchemaVersion = 1;
        private static readonly ConcurrentDictionary<string, object> Locks =
            new ConcurrentDictionary<string, object>(StringComparer.Ordinal);

        private readonly ShopContext _shop;
        private readonly IShopScopedPathProvider _paths;
        private readonly string _path;

        public ShopScopedSettingsStore(ShopContext shop, IShopScopedPathProvider paths)
        {
            if (shop == null) throw new ArgumentNullException(nameof(shop));
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            _shop = shop;
            _paths = paths;
            _path = _paths.GetConfigPath(_shop, "settings.json");
        }

        public bool TryGetString(string key, out string value)
        {
            key = RequireKey(key);
            lock (GetLock())
            {
                var document = Load();
                return document.Values.TryGetValue(key, out value);
            }
        }

        public void SetString(string key, string value)
        {
            key = RequireKey(key);
            lock (GetLock())
            {
                var document = Load();
                document.Values[key] = value ?? string.Empty;
                document.UpdatedAtUtc = DateTime.UtcNow;
                Save(document);
            }
        }

        public bool Remove(string key)
        {
            key = RequireKey(key);
            lock (GetLock())
            {
                var document = Load();
                if (!document.Values.Remove(key)) return false;
                document.UpdatedAtUtc = DateTime.UtcNow;
                Save(document);
                return true;
            }
        }

        public string SettingsPath
        {
            get { return _path; }
        }

        private object GetLock()
        {
            return Locks.GetOrAdd(_shop.ShopKey, _ => new object());
        }

        private SettingsDocument Load()
        {
            if (!File.Exists(_path)) return NewDocument();
            try
            {
                var json = File.ReadAllText(_path, Encoding.UTF8);
                var document = JsonConvert.DeserializeObject<SettingsDocument>(json);
                if (document == null
                    || !string.Equals(document.Schema, Schema, StringComparison.Ordinal)
                    || document.SchemaVersion != SchemaVersion
                    || !string.Equals(document.ShopKey, _shop.ShopKey, StringComparison.Ordinal)
                    || document.Values == null)
                {
                    throw new InvalidDataException("Shop settings file has an invalid schema or ShopKey.");
                }
                return document;
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("Cannot read shop settings: " + ex.Message, ex);
            }
        }

        private SettingsDocument NewDocument()
        {
            return new SettingsDocument
            {
                Schema = Schema,
                SchemaVersion = SchemaVersion,
                ShopKey = _shop.ShopKey,
                UpdatedAtUtc = DateTime.UtcNow,
                Values = new Dictionary<string, string>(StringComparer.Ordinal)
            };
        }

        private void Save(SettingsDocument document)
        {
            AtomicWrite(_path, JsonConvert.SerializeObject(document, Formatting.Indented));
        }

        private static void AtomicWrite(string path, string content)
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("Target directory is missing.");
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

        private static string RequireKey(string key)
        {
            key = (key ?? string.Empty).Trim();
            if (key.Length == 0 || key.Length > 160)
                throw new ArgumentException("A valid settings key is required.", nameof(key));
            return key;
        }

        private sealed class SettingsDocument
        {
            [JsonProperty("schema")]
            public string Schema { get; set; }

            [JsonProperty("schema_version")]
            public int SchemaVersion { get; set; }

            [JsonProperty("shop_key")]
            public string ShopKey { get; set; }

            [JsonProperty("updated_at_utc")]
            public DateTime UpdatedAtUtc { get; set; }

            [JsonProperty("values")]
            public Dictionary<string, string> Values { get; set; }
        }
    }
}
