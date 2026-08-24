using Bot.Knowledge;
using Bot.ShopScope;
using BotLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal static class KnowledgeEngineV2LearningBridge
    {
        private static int _initialized;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                KnowledgeLearningService.KnowledgeBaseChanged += OnLegacyKnowledgeChanged;
                Log.Info("Knowledge Engine V2学习桥已启动：旧学习事件只作为V2学习候选来源，不自动获得本地直答资格。");
            }
            return new object();
        }

        private static void OnLegacyKnowledgeChanged(object sender, EventArgs e)
        {
            QN[] qns;
            try { qns = QN.QNSet == null ? new QN[0] : QN.QNSet.ToArray(); }
            catch { return; }
            foreach (var seller in qns
                .Where(x => x != null && x.Seller != null && !string.IsNullOrWhiteSpace(x.Seller.Nick))
                .Select(x => x.Seller.Nick.Trim())
                .Distinct(StringComparer.Ordinal))
            {
                var captured = seller;
                Task.Run(() => ImportLearningChanges(captured));
            }
        }

        private static void ImportLearningChanges(string seller)
        {
            try
            {
                var shop = ShopContextLocator.ResolveBySellerNick(seller);
                if (shop == null) return;
                List<KnowledgeBaseEntry> legacy;
                using (ShopSettingsScope.Enter(shop))
                    legacy = BotFeatureStore.GetKnowledgeBase() ?? new List<KnowledgeBaseEntry>();

                var current = KnowledgeEngineV2Repository.LoadAll(seller);
                var byId = current.Where(x => x != null && !string.IsNullOrWhiteSpace(x.Id))
                    .ToDictionary(x => x.Id, x => x, StringComparer.Ordinal);
                var added = 0;
                foreach (var entry in legacy.Where(IsLearningEntry))
                {
                    KnowledgeV2Record existing;
                    if (!string.IsNullOrWhiteSpace(entry.Id)
                        && byId.TryGetValue(entry.Id, out existing)
                        && Same(existing.Answer, entry.Answer))
                        continue;

                    KnowledgePolicyProfile profile = null;
                    try
                    {
                        using (ShopSettingsScope.Enter(shop))
                            profile = KnowledgePolicyProfileService.GetProfile(entry);
                    }
                    catch { }
                    var record = KnowledgeEngineV2Semantics.FromLegacy(entry, profile);
                    if (record == null) continue;
                    record.Type = "learning_candidate";
                    record.Status = "candidate";
                    record.Enabled = true;
                    record.SourceType = string.IsNullOrWhiteSpace(entry.SourceType) ? "legacy_learning" : entry.SourceType;
                    record.SourceId = entry.Id;
                    record.Confidence = Math.Min(record.Confidence, 0.82);
                    if (existing != null && !Same(existing.Answer, entry.Answer))
                    {
                        record.Id = "candidate-" + KnowledgeAiService.ContentHash(
                            entry.Id ?? string.Empty, entry.Answer ?? string.Empty);
                        record.Title = "候选修正：" + (entry.Title ?? string.Empty);
                    }
                    KnowledgeEngineV2Repository.Save(seller, record);
                    added++;
                }
                if (added > 0)
                {
                    KnowledgeEngineV2Service.Warm(seller);
                    Log.Info("Knowledge Engine V2已接收学习候选: seller=" + seller + ", added=" + added);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("同步V2学习候选失败: seller=" + seller + ", error=" + ex.Message, 20);
            }
        }

        private static bool IsLearningEntry(KnowledgeBaseEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Answer)) return false;
            var value = (entry.SourceType ?? string.Empty) + " " + (entry.Category ?? string.Empty);
            return value.IndexOf("学习", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("人工回复", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("manual", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("session", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool Same(string left, string right)
        {
            return KnowledgeEngineV2Semantics.TextSimilarity(left, right) >= 0.96;
        }
    }
}

namespace Bot
{
    public partial class App
    {
        private readonly object _knowledgeEngineV2LearningBootstrap = ChromeNs.KnowledgeEngineV2LearningBridge.InitializeForApp();
    }
}
