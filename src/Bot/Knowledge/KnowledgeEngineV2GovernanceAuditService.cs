using Bot.ShopScope;
using BotLib;
using BotLib.Db.Sqlite;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Bot.Knowledge
{
    internal sealed class KnowledgeV2GovernanceSettings
    {
        public int NormalVerificationDays { get; set; }
        public int HighRiskVerificationDays { get; set; }
        public int UnusedStaleDays { get; set; }
    }

    internal sealed class KnowledgeV2GovernanceAuditRow
    {
        [PrimaryKey]
        public string Id { get; set; }
        public string Seller { get; set; }
        public string ActionType { get; set; }
        public string TargetType { get; set; }
        public string KnowledgeId { get; set; }
        public string CandidateId { get; set; }
        public string TargetTitle { get; set; }
        public string BeforeState { get; set; }
        public string AfterState { get; set; }
        public string Summary { get; set; }
        public string Result { get; set; }
        public long CreatedAtTicks { get; set; }
    }

    internal sealed class KnowledgeV2GovernanceAuditEntry
    {
        public string Id { get; set; }
        public string Seller { get; set; }
        public string ActionType { get; set; }
        public string TargetType { get; set; }
        public string KnowledgeId { get; set; }
        public string CandidateId { get; set; }
        public string TargetTitle { get; set; }
        public string BeforeState { get; set; }
        public string AfterState { get; set; }
        public string Summary { get; set; }
        public string Result { get; set; }
        public DateTime CreatedAt { get; set; }

        public string CreatedAtText
        {
            get { return CreatedAt == DateTime.MinValue ? "-" : CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"); }
        }

        public string ActionText
        {
            get
            {
                switch ((ActionType ?? string.Empty).Trim().ToLowerInvariant())
                {
                    case "mark_verified": return "确认仍有效";
                    case "disable_knowledge": return "停用知识";
                    case "rollback_revision": return "回滚修订";
                    case "apply_revision": return "应用修订";
                    case "reject_revision": return "驳回修订";
                    case "generate_revision_candidates": return "生成修订候选";
                    case "update_settings": return "更新治理设置";
                    default: return ActionType ?? string.Empty;
                }
            }
        }

        public string ResultText
        {
            get { return string.Equals(Result, "success", StringComparison.OrdinalIgnoreCase) ? "成功" : Result ?? string.Empty; }
        }
    }

    internal static class KnowledgeEngineV2GovernanceAuditService
    {
        internal const string NormalVerificationDaysKey = "knowledge.engine_v2.governance.normal_verification_days";
        internal const string HighRiskVerificationDaysKey = "knowledge.engine_v2.governance.high_risk_verification_days";
        internal const string UnusedStaleDaysKey = "knowledge.engine_v2.governance.unused_stale_days";

        internal const int DefaultNormalVerificationDays = 180;
        internal const int DefaultHighRiskVerificationDays = 60;
        internal const int DefaultUnusedStaleDays = 120;
        internal const int MinNormalVerificationDays = 30;
        internal const int MaxNormalVerificationDays = 730;
        internal const int MinHighRiskVerificationDays = 7;
        internal const int MaxHighRiskVerificationDays = 365;
        internal const int MinUnusedStaleDays = 30;
        internal const int MaxUnusedStaleDays = 730;

        private sealed class StoreState
        {
            public readonly object Sync = new object();
            public SQLiteHelper Db;
            public string Path;
        }

        private static readonly IShopScopedPathProvider Paths = new ShopScopedPathProvider();
        private static readonly ConcurrentDictionary<string, StoreState> Stores =
            new ConcurrentDictionary<string, StoreState>(StringComparer.Ordinal);

        public static KnowledgeV2GovernanceSettings GetSettings(string seller)
        {
            var shop = ResolveShopRequired(seller);
            var values = new ShopScopedSettingsStore(shop, Paths).ExportValues();
            var normal = ReadInt(values, NormalVerificationDaysKey, DefaultNormalVerificationDays,
                MinNormalVerificationDays, MaxNormalVerificationDays);
            var high = ReadInt(values, HighRiskVerificationDaysKey, DefaultHighRiskVerificationDays,
                MinHighRiskVerificationDays, Math.Min(MaxHighRiskVerificationDays, normal));
            var unused = ReadInt(values, UnusedStaleDaysKey, DefaultUnusedStaleDays,
                MinUnusedStaleDays, MaxUnusedStaleDays);
            return new KnowledgeV2GovernanceSettings
            {
                NormalVerificationDays = normal,
                HighRiskVerificationDays = high,
                UnusedStaleDays = unused
            };
        }

        public static KnowledgeV2GovernanceSettings SaveSettings(string seller,
            int normalVerificationDays, int highRiskVerificationDays, int unusedStaleDays)
        {
            seller = Clean(seller);
            var shop = ResolveShopRequired(seller);
            var before = GetSettings(seller);
            var after = new KnowledgeV2GovernanceSettings
            {
                NormalVerificationDays = Clamp(normalVerificationDays,
                    MinNormalVerificationDays, MaxNormalVerificationDays),
                UnusedStaleDays = Clamp(unusedStaleDays,
                    MinUnusedStaleDays, MaxUnusedStaleDays)
            };
            after.HighRiskVerificationDays = Clamp(highRiskVerificationDays,
                MinHighRiskVerificationDays, Math.Min(MaxHighRiskVerificationDays, after.NormalVerificationDays));

            var store = new ShopScopedSettingsStore(shop, Paths);
            store.MergeValues(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { NormalVerificationDaysKey, after.NormalVerificationDays.ToString(CultureInfo.InvariantCulture) },
                { HighRiskVerificationDaysKey, after.HighRiskVerificationDays.ToString(CultureInfo.InvariantCulture) },
                { UnusedStaleDaysKey, after.UnusedStaleDays.ToString(CultureInfo.InvariantCulture) }
            }, true);

            if (!SettingsEqual(before, after))
            {
                string ignored;
                TryAppendAction(seller, "update_settings", "governance_settings", string.Empty, string.Empty,
                    "Knowledge V2治理阈值", DescribeSettings(before), DescribeSettings(after),
                    "人工更新验证过期与长期未使用阈值；不修改任何生产知识。", "success", out ignored);
            }
            return after;
        }

        public static List<KnowledgeV2GovernanceAuditEntry> GetEntries(string seller, int maxCount)
        {
            var state = GetState(ResolveShopRequired(seller));
            List<KnowledgeV2GovernanceAuditRow> rows;
            lock (state.Sync) rows = state.Db.ReadRecords<KnowledgeV2GovernanceAuditRow>(null);
            return rows
                .Where(x => x != null)
                .OrderByDescending(x => x.CreatedAtTicks)
                .Take(Math.Max(1, Math.Min(1000, maxCount <= 0 ? 300 : maxCount)))
                .Select(ToEntry)
                .ToList();
        }

        public static bool TryAppendAction(string seller, string actionType, string targetType,
            string knowledgeId, string candidateId, string targetTitle, string beforeState,
            string afterState, string summary, string result, out string error)
        {
            error = string.Empty;
            try
            {
                seller = Clean(seller);
                if (seller.Length == 0) throw new InvalidOperationException("无法识别当前店铺客服账号。");
                var state = GetState(ResolveShopRequired(seller));
                var row = new KnowledgeV2GovernanceAuditRow
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Seller = seller,
                    ActionType = Truncate(actionType, 80).ToLowerInvariant(),
                    TargetType = Truncate(targetType, 80).ToLowerInvariant(),
                    KnowledgeId = Truncate(knowledgeId, 160),
                    CandidateId = Truncate(candidateId, 160),
                    TargetTitle = Truncate(targetTitle, 500),
                    BeforeState = Truncate(beforeState, 2000),
                    AfterState = Truncate(afterState, 2000),
                    Summary = Truncate(summary, 1600),
                    Result = Truncate(result, 80).ToLowerInvariant(),
                    CreatedAtTicks = DateTime.Now.Ticks
                };
                lock (state.Sync) state.Db.SaveOneRecord(row);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.ErrorWithMaxCount("写入Knowledge V2治理审计失败: seller=" + Clean(seller)
                    + ", action=" + Clean(actionType) + ", error=" + ex.Message, 10);
                return false;
            }
        }

        public static string DescribeRecord(KnowledgeV2Record record)
        {
            if (record == null) return "record=missing";
            return "enabled=" + (record.Enabled ? "1" : "0")
                + ";status=" + Clean(record.Status)
                + ";last_verified=" + FormatDate(record.LastVerifiedAt)
                + ";answer_sha256=" + Sha256(record.Answer ?? string.Empty);
        }

        public static string DescribeSettings(KnowledgeV2GovernanceSettings settings)
        {
            settings = settings ?? new KnowledgeV2GovernanceSettings
            {
                NormalVerificationDays = DefaultNormalVerificationDays,
                HighRiskVerificationDays = DefaultHighRiskVerificationDays,
                UnusedStaleDays = DefaultUnusedStaleDays
            };
            return "normal_verification_days=" + settings.NormalVerificationDays
                + ";high_risk_verification_days=" + settings.HighRiskVerificationDays
                + ";unused_stale_days=" + settings.UnusedStaleDays;
        }

        public static string GetDatabasePath(string seller)
        {
            return GetState(ResolveShopRequired(seller)).Path;
        }

        private static StoreState GetState(ShopContext shop)
        {
            return Stores.GetOrAdd(shop.ShopKey, _ =>
            {
                var root = Paths.GetKnowledgeRoot(shop);
                if (!Directory.Exists(root)) Directory.CreateDirectory(root);
                var path = Path.Combine(root, "knowledge-governance-v2.db");
                return new StoreState
                {
                    Path = path,
                    Db = new SQLiteHelper(path, new List<Type> { typeof(KnowledgeV2GovernanceAuditRow) })
                };
            });
        }

        private static ShopContext ResolveShopRequired(string seller)
        {
            var current = ShopSettingsScope.Current;
            if (current != null) return current;
            var shop = ShopContextLocator.ResolveBySellerNick(Clean(seller));
            if (shop == null) throw new InvalidOperationException("Knowledge V2治理审计无法确定当前店铺身份。");
            return shop;
        }

        private static KnowledgeV2GovernanceAuditEntry ToEntry(KnowledgeV2GovernanceAuditRow row)
        {
            return new KnowledgeV2GovernanceAuditEntry
            {
                Id = row.Id,
                Seller = row.Seller,
                ActionType = row.ActionType,
                TargetType = row.TargetType,
                KnowledgeId = row.KnowledgeId,
                CandidateId = row.CandidateId,
                TargetTitle = row.TargetTitle,
                BeforeState = row.BeforeState,
                AfterState = row.AfterState,
                Summary = row.Summary,
                Result = row.Result,
                CreatedAt = SafeDate(row.CreatedAtTicks)
            };
        }

        private static int ReadInt(IDictionary<string, string> values, string key, int fallback, int min, int max)
        {
            string raw;
            int parsed;
            return values != null && values.TryGetValue(key, out raw)
                && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                ? Clamp(parsed, min, max) : Clamp(fallback, min, max);
        }

        private static bool SettingsEqual(KnowledgeV2GovernanceSettings left, KnowledgeV2GovernanceSettings right)
        {
            return left != null && right != null
                && left.NormalVerificationDays == right.NormalVerificationDays
                && left.HighRiskVerificationDays == right.HighRiskVerificationDays
                && left.UnusedStaleDays == right.UnusedStaleDays;
        }

        private static string Sha256(string value)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
                try { return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant(); }
                finally { Array.Clear(bytes, 0, bytes.Length); }
            }
        }

        private static string FormatDate(DateTime value)
        {
            return value == DateTime.MinValue ? "-" : value.ToString("o", CultureInfo.InvariantCulture);
        }

        private static DateTime SafeDate(long ticks)
        {
            try { return ticks <= 0 ? DateTime.MinValue : new DateTime(ticks); }
            catch { return DateTime.MinValue; }
        }

        private static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        private static string Truncate(string value, int max)
        {
            value = Clean(value);
            return value.Length <= max ? value : value.Substring(0, max);
        }

        private static string Clean(string value)
        {
            return (value ?? string.Empty).Trim();
        }
    }
}
