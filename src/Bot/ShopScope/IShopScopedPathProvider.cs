namespace Bot.ShopScope
{
    internal interface IShopScopedPathProvider
    {
        string UserDataRoot { get; }
        string GlobalRoot { get; }
        string ShopsRoot { get; }
        string LegacyDataRoot { get; }
        string RegistryPath { get; }

        string GetShopRoot(ShopContext shop);
        string GetProfilePath(ShopContext shop);
        string GetConfigRoot(ShopContext shop);
        string GetConfigPath(ShopContext shop, string fileName);
        string GetKnowledgeRoot(ShopContext shop);
        string GetRulesRoot(ShopContext shop);
        string GetStateRoot(ShopContext shop);
        string GetCacheRoot(ShopContext shop);
        string GetLogRoot(ShopContext shop);
        string GetBackupRoot(ShopContext shop);
        string GetCompatibilityDataRoot(ShopContext shop);
    }
}