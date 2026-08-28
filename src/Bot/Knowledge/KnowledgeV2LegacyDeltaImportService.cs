using Bot.ChromeNs;
using Bot.ShopScope;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Bot.Knowledge
{
    /// <summary>
    /// Incrementally imports only legacy entries produced by the restored history-chat scanner.
    /// It never clears/rebuilds V2, so V2-only fields and later revisions are preserved.
    /// </summary>
    internal static class KnowledgeV2LegacyDeltaImportService
    {
        public static int ImportMissingHistoryKnowledge(string seller)
        {
            seller = (seller ?? string.Empty).Trim();
            if (seller.Length == 0) return 0;
            var shop = ShopContextLocator.ResolveBySellerNick(seller);
            if (shop == null) throw new InvalidOperationException("无法确定当前店铺身份。");

            List<KnowledgeBaseEntry> legacy;
            using (ShopSettingsScope.Enter(shop)) legacy = BotFeatureStore.GetKnowledgeBase() ?? new List<KnowledgeBaseEntry>();
            var existing = new HashSet<string>(
                KnowledgeEngineV2Repository.LoadAll(seller).Where(x => x != null).Select(x => x.Id ?? string.Empty),
                StringComparer.Ordinal);
            var added = 0;
            foreach (var entry in legacy.Where(x => x != null
                && string.Equals((x.SourceType ?? string.Empty).Trim(), "历史聊天扫描", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(x.Answer)))
            {
                if (!string.IsNullOrWhiteSpace(entry.Id) && existing.Contains(entry.Id)) continue;
                var record = KnowledgeEngineV2Semantics.FromLegacy(entry, null);
                if (record == null) continue;
                record.SourceType = "历史聊天扫描";
                record.SourceId = string.IsNullOrWhiteSpace(entry.Id) ? "history:" + Guid.NewGuid().ToString("N") : entry.Id;
                record.Confidence = Math.Max(record.Confidence, 0.82);
                KnowledgeEngineV2Repository.Save(seller, record);
                existing.Add(record.Id ?? string.Empty);
                added++;
            }
            if (added > 0) KnowledgeEngineV2Service.Warm(seller);
            return added;
        }
    }
}
