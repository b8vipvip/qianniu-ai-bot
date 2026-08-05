using BotLib.Extensions;
using System;
using System.IO;

namespace Bot.ShopScope
{
    internal sealed class ShopScopedPathProvider : IShopScopedPathProvider
    {
        public ShopScopedPathProvider()
            : this(PathEx.UserDataRoot, PathEx.GlobalDataDir)
        {
        }

        internal ShopScopedPathProvider(string userDataRoot, string legacyDataRoot)
        {
            UserDataRoot = NormalizeRoot(userDataRoot, nameof(userDataRoot));
            LegacyDataRoot = NormalizeRoot(legacyDataRoot, nameof(legacyDataRoot));
            GlobalRoot = EnsureDirectory(Path.Combine(UserDataRoot, "global"));
            ShopsRoot = EnsureDirectory(Path.Combine(UserDataRoot, "shops"));
            RegistryPath = Path.Combine(GlobalRoot, "shops.json");
        }

        public string UserDataRoot { get; private set; }
        public string GlobalRoot { get; private set; }
        public string ShopsRoot { get; private set; }
        public string LegacyDataRoot { get; private set; }
        public string RegistryPath { get; private set; }

        public string GetShopRoot(ShopContext shop)
        {
            RequireShop(shop);
            ValidateShopKey(shop.ShopKey);
            return EnsureDirectory(Path.Combine(ShopsRoot, shop.ShopKey));
        }

        public string GetProfilePath(ShopContext shop)
        {
            return Path.Combine(GetShopRoot(shop), "profile.json");
        }

        public string GetConfigRoot(ShopContext shop)
        {
            return GetShopDirectory(shop, "config");
        }

        public string GetConfigPath(ShopContext shop, string fileName)
        {
            fileName = RequireLeafFileName(fileName);
            return Path.Combine(GetConfigRoot(shop), fileName);
        }

        public string GetKnowledgeRoot(ShopContext shop)
        {
            return GetShopDirectory(shop, "knowledge");
        }

        public string GetRulesRoot(ShopContext shop)
        {
            return GetShopDirectory(shop, "rules");
        }

        public string GetStateRoot(ShopContext shop)
        {
            return GetShopDirectory(shop, "state");
        }

        public string GetCacheRoot(ShopContext shop)
        {
            return GetShopDirectory(shop, "cache");
        }

        public string GetLogRoot(ShopContext shop)
        {
            return GetShopDirectory(shop, "logs");
        }

        public string GetBackupRoot(ShopContext shop)
        {
            return GetShopDirectory(shop, "backup");
        }

        public string GetCompatibilityDataRoot(ShopContext shop)
        {
            return EnsureDirectory(Path.Combine(GetStateRoot(shop), "data"));
        }

        private string GetShopDirectory(ShopContext shop, string name)
        {
            return EnsureDirectory(Path.Combine(GetShopRoot(shop), name));
        }

        private static string RequireLeafFileName(string fileName)
        {
            fileName = (fileName ?? string.Empty).Trim();
            if (fileName.Length == 0
                || Path.IsPathRooted(fileName)
                || !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal)
                || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || fileName == "."
                || fileName == "..")
            {
                throw new ArgumentException("A safe leaf file name is required.", nameof(fileName));
            }
            return fileName;
        }

        private static void RequireShop(ShopContext shop)
        {
            if (shop == null) throw new ArgumentNullException(nameof(shop));
        }

        private static void ValidateShopKey(string shopKey)
        {
            if (string.IsNullOrWhiteSpace(shopKey) || shopKey.Length > 64)
                throw new ArgumentException("ShopKey is missing or too long.", nameof(shopKey));
            foreach (var ch in shopKey)
            {
                if (!(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-'))
                    throw new ArgumentException("ShopKey contains unsafe path characters.", nameof(shopKey));
            }
        }

        private static string NormalizeRoot(string path, string name)
        {
            path = (path ?? string.Empty).Trim();
            if (path.Length == 0) throw new ArgumentException("Path is required.", name);
            return EnsureDirectory(Path.GetFullPath(path));
        }

        private static string EnsureDirectory(string path)
        {
            Directory.CreateDirectory(path);
            return path;
        }
    }
}