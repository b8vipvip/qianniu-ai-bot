using Bot.ChromeNs;
using Bot.ShopScope;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Bot.Knowledge
{
    internal static partial class KnowledgeEngineV2Service
    {
        private sealed class Snapshot
        {
            public string ShopKey;
            public DateTime BuiltAt;
            public List<KnowledgeV2Record> Records;
            public Dictionary<string, HashSet<int>> Exact;
            public Dictionary<string, HashSet<int>> Intent;
            public Dictionary<string, HashSet<int>> Predicate;
            public Dictionary<string, HashSet<int>> Entity;
            public Dictionary<string, HashSet<int>> Ngram;
        }

        private sealed class RuntimeSettings
        {
            public bool Enabled;
            public string Mode;
            public double DirectThreshold;
            public double MinConfidence;
            public DateTime ExpiresAt;
        }

        private static readonly ConcurrentDictionary<string, Snapshot> Snapshots =
            new ConcurrentDictionary<string, Snapshot>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, KnowledgeV2WorkingMemory> Working =
            new ConcurrentDictionary<string, KnowledgeV2WorkingMemory>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, object> BuildLocks =
            new ConcurrentDictionary<string, object>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, RuntimeSettings> SettingsCache =
            new ConcurrentDictionary<string, RuntimeSettings>(StringComparer.Ordinal);
        private static readonly IShopScopedPathProvider Paths = new ShopScopedPathProvider();

        public static bool IsEnabled(string seller)
        {
            return GetSettings(seller).Enabled;
        }

        public static string GetMode(string seller)
        {
            return GetSettings(seller).Mode;
        }

        public static KnowledgeV2Settings GetSettingsView(string seller)
        {
            var settings = GetSettings(seller);
            return new KnowledgeV2Settings
            {
                Enabled = settings.Enabled,
                Mode = settings.Mode,
                DirectThreshold = settings.DirectThreshold,
                MinConfidence = settings.MinConfidence
            };
        }

        public static void SetSettings(string seller, bool enabled, string mode, double threshold, double minConfidence)
        {
            var shop = ResolveShopRequired(seller);
            var store = new ShopScopedSettingsStore(shop, Paths);
            store.SetString(KnowledgeEngineV2Constants.SettingsEnabled, enabled ? "1" : "0");
            store.SetString(KnowledgeEngineV2Constants.SettingsMode,
                string.Equals(mode, KnowledgeEngineV2Constants.ModeShadow, StringComparison.OrdinalIgnoreCase)
                    ? KnowledgeEngineV2Constants.ModeShadow : KnowledgeEngineV2Constants.ModeProduction);
            store.SetString(KnowledgeEngineV2Constants.SettingsDirectThreshold,
                Math.Max(0.70, Math.Min(0.96, threshold)).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
            store.SetString(KnowledgeEngineV2Constants.SettingsMinConfidence,
                Math.Max(0.50, Math.Min(0.95, minConfidence)).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
            SettingsCache[shop.ShopKey] = new RuntimeSettings
            {
                Enabled = enabled,
                Mode = string.Equals(mode, KnowledgeEngineV2Constants.ModeShadow, StringComparison.OrdinalIgnoreCase)
                    ? KnowledgeEngineV2Constants.ModeShadow : KnowledgeEngineV2Constants.ModeProduction,
                DirectThreshold = Math.Max(0.70, Math.Min(0.96, threshold)),
                MinConfidence = Math.Max(0.50, Math.Min(0.95, minConfidence)),
                ExpiresAt = DateTime.Now.AddMinutes(5)
            };
        }

        public static KnowledgeV2Decision Resolve(string seller, string buyer, string message)
        {
            var total = Stopwatch.StartNew();
            var settings = GetSettings(seller);
            var decision = new KnowledgeV2Decision { Enabled = settings.Enabled, Mode = settings.Mode };
            if (!decision.Enabled)
            {
                decision.Reason = "Knowledge Engine V2已关闭";
                decision.TotalMs = total.ElapsedMilliseconds;
                return decision;
            }
            message = (message ?? string.Empty).Trim();
            if (message.Length == 0 || IsMediaPlaceholder(message))
            {
                decision.Reason = "空消息或媒体消息不走文本知识引擎";
                decision.TotalMs = total.ElapsedMilliseconds;
                return decision;
            }

            var parseSw = Stopwatch.StartNew();
            var memory = GetWorkingMemory(seller, buyer);
            var query = KnowledgeEngineV2Semantics.Parse(message, memory);
            decision.Query = query;
            UpdateWorkingMemory(seller, buyer, query);
            decision.ParseMs = parseSw.ElapsedMilliseconds;

            var snapshot = GetSnapshot(seller);
            var recallSw = Stopwatch.StartNew();
            var candidates = Recall(snapshot, query);
            decision.CandidateCount = candidates.Count;
            decision.RecallMs = recallSw.ElapsedMilliseconds;

            var rankSw = Stopwatch.StartNew();
            var matches = candidates
                .Select(i => Score(snapshot.Records[i], query))
                .Where(x => x != null && x.Score >= 0.30)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.ConfidenceScore)
                .Take(5)
                .ToList();
            decision.Matches = matches;
            decision.RankMs = rankSw.ElapsedMilliseconds;

            var decideSw = Stopwatch.StartNew();
            var best = matches.FirstOrDefault();
            if (best == null)
            {
                decision.Reason = "结构化索引没有找到足够相关的候选知识";
                Finish(decision, total, decideSw);
                return decision;
            }
            var second = matches.Count > 1 ? matches[1] : null;
            decision.HasConflict = HasConflict(best, second);
            var margin = second == null ? best.Score : best.Score - second.Score;
            var threshold = settings.DirectThreshold;
            var minConfidence = settings.MinConfidence;
            var highRisk = KnowledgeEngineV2Semantics.IsHighRisk(message)
                || KnowledgeEngineV2Semantics.IsHighRisk(best.Record.Answer)
                || string.Equals(best.Record.RiskLevel, "high", StringComparison.OrdinalIgnoreCase);
            var unapprovedLearning = string.Equals(best.Record.Status, "candidate", StringComparison.OrdinalIgnoreCase)
                || string.Equals(best.Record.Type, "learning_candidate", StringComparison.OrdinalIgnoreCase);
            var sameFactSecond = second != null
                && string.Equals(KnowledgeEngineV2Semantics.FactKey(best.Record),
                    KnowledgeEngineV2Semantics.FactKey(second.Record), StringComparison.Ordinal);
            var effectiveMargin = sameFactSecond && AnswersEquivalent(best.Record.Answer, second.Record.Answer)
                ? Math.Max(margin, 0.12) : margin;

            decision.CanDirectReply = ReplyModeService.IsLocalFirst(seller)
                && decision.Mode == KnowledgeEngineV2Constants.ModeProduction
                && !decision.HasConflict
                && !highRisk
                && !unapprovedLearning
                && best.Record.Enabled
                && best.Score >= threshold
                && best.ConfidenceScore >= minConfidence
                && (best.AliasScore >= 0.94 || best.PredicateScore >= 0.99)
                && (effectiveMargin >= 0.08 || best.AliasScore >= 0.98);
            decision.Answer = decision.CanDirectReply ? (best.Record.Answer ?? string.Empty).Trim() : string.Empty;
            decision.Reason = decision.CanDirectReply
                ? "V2结构化知识高置信直答：score=" + best.Score.ToString("0.00")
                    + ", predicate=" + query.Predicate + ", candidates=" + candidates.Count
                : (unapprovedLearning
                    ? "学习候选尚未人工批准，禁止本地直答"
                    : BuildRejectReason(best, decision, threshold, minConfidence, effectiveMargin, highRisk));
            Finish(decision, total, decideSw);
            return decision;
        }

        public static List<KnowledgeV2Record> GetRecords(string seller)
        {
            return GetSnapshot(seller).Records.Select(Clone).ToList();
        }

        public static List<KnowledgeV2Conflict> GetConflicts(string seller)
        {
            return GetSnapshot(seller).Records
                .Where(x => x.Enabled
                    && !string.Equals(x.Status, "candidate", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(x.Type, "learning_candidate", StringComparison.OrdinalIgnoreCase))
                .GroupBy(KnowledgeEngineV2Semantics.FactKey)
                .Where(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() > 1)
                .Select(g => new KnowledgeV2Conflict
                {
                    FactKey = g.Key,
                    Subject = g.First().Subject,
                    Predicate = g.First().Predicate,
                    Records = g.ToList()
                })
                .Where(x => HasAnswerDisagreement(x.Records))
                .ToList();
        }

        public static KnowledgeV2Stats GetStats(string seller)
        {
            var snapshot = GetSnapshot(seller);
            return new KnowledgeV2Stats
            {
                Total = snapshot.Records.Count,
                BusinessFacts = snapshot.Records.Count(x => x.Type == "business_fact" || x.Type == "presale"),
                Procedures = snapshot.Records.Count(x => x.Type == "procedure"),
                SafetyRules = snapshot.Records.Count(x => x.Type == "safety_rule"),
                LearningCandidates = snapshot.Records.Count(x => x.Status == "candidate" || x.Type == "learning_candidate"),
                ProductBound = snapshot.Records.Count(x => x.ProductIds != null && x.ProductIds.Count > 0),
                Conflicts = GetConflicts(seller).Count,
                SnapshotBuiltAt = snapshot.BuiltAt,
                DatabasePath = KnowledgeEngineV2Repository.GetDatabasePath(seller)
            };
        }

        public static void Invalidate(string seller)
        {
            var shop = ResolveShop(seller);
            if (shop == null) return;
            Snapshot ignored;
            Snapshots.TryRemove(shop.ShopKey, out ignored);
        }

        public static void Warm(string seller)
        {
            GetSnapshot(seller);
        }

        public static void RebuildFromLegacy(string seller)
        {
            KnowledgeEngineV2Repository.ResetFromLegacy(seller);
            Invalidate(seller);
            Warm(seller);
        }

        public static void PromoteCandidate(string seller, string id)
        {
            var record = GetRecords(seller).FirstOrDefault(x => x.Id == id);
            if (record == null) return;
            record.Status = "active";
            if (record.Type == "learning_candidate") record.Type = "business_fact";
            record.Confidence = Math.Max(record.Confidence, 0.82);
            KnowledgeEngineV2Repository.Save(seller, record);
        }
    }
}
