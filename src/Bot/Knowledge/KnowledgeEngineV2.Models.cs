using Bot.ChromeNs;
using Bot.ShopScope;
using BotLib;
using BotLib.Db.Sqlite;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Bot.Knowledge
{
    internal static class KnowledgeEngineV2Constants
    {
        public const int SchemaVersion = 2;
        public const string ModeProduction = "production";
        public const string ModeShadow = "shadow";
        public const string SettingsEnabled = "knowledge.engine_v2.enabled";
        public const string SettingsMode = "knowledge.engine_v2.mode";
        public const string SettingsDirectThreshold = "knowledge.engine_v2.direct_threshold";
        public const string SettingsMinConfidence = "knowledge.engine_v2.min_confidence";
        public const string SettingsMigrationVersion = "knowledge.engine_v2.migration_version";
        public const double DefaultDirectThreshold = 0.82;
        public const double DefaultMinConfidence = 0.68;
    }

    internal sealed class KnowledgeV2RecordRow
    {
        [PrimaryKey]
        public string Id { get; set; }
        public string Type { get; set; }
        public string Title { get; set; }
        public string Intent { get; set; }
        public string Subject { get; set; }
        public string Predicate { get; set; }
        public string EntitiesJson { get; set; }
        public string AliasesJson { get; set; }
        public string Answer { get; set; }
        public string ShortAnswer { get; set; }
        public string ConditionsJson { get; set; }
        public string ExclusionsJson { get; set; }
        public string RequiredContextJson { get; set; }
        public string ProductIdsJson { get; set; }
        public string RiskLevel { get; set; }
        public string SourceType { get; set; }
        public string SourceId { get; set; }
        public double Authority { get; set; }
        public double Confidence { get; set; }
        public int UseCount { get; set; }
        public int AcceptedCount { get; set; }
        public int CorrectionCount { get; set; }
        public int WithdrawCount { get; set; }
        public bool Enabled { get; set; }
        public string Status { get; set; }
        public long CreatedAtTicks { get; set; }
        public long UpdatedAtTicks { get; set; }
        public long LastVerifiedAtTicks { get; set; }
    }

    internal sealed class KnowledgeV2MetaRow
    {
        [PrimaryKey]
        public string Key { get; set; }
        public string Value { get; set; }
    }

    internal sealed class KnowledgeV2Record
    {
        public string Id { get; set; }
        public string Type { get; set; }
        public string Title { get; set; }
        public string Intent { get; set; }
        public string Subject { get; set; }
        public string Predicate { get; set; }
        public List<string> Entities { get; set; }
        public List<string> Aliases { get; set; }
        public string Answer { get; set; }
        public string ShortAnswer { get; set; }
        public List<string> Conditions { get; set; }
        public List<string> Exclusions { get; set; }
        public List<string> RequiredContext { get; set; }
        public List<string> ProductIds { get; set; }
        public string RiskLevel { get; set; }
        public string SourceType { get; set; }
        public string SourceId { get; set; }
        public double Authority { get; set; }
        public double Confidence { get; set; }
        public int UseCount { get; set; }
        public int AcceptedCount { get; set; }
        public int CorrectionCount { get; set; }
        public int WithdrawCount { get; set; }
        public bool Enabled { get; set; }
        public string Status { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime LastVerifiedAt { get; set; }

        public KnowledgeV2Record()
        {
            Entities = new List<string>();
            Aliases = new List<string>();
            Conditions = new List<string>();
            Exclusions = new List<string>();
            RequiredContext = new List<string>();
            ProductIds = new List<string>();
        }

        public string ConfidenceText
        {
            get { return (Confidence * 100).ToString("0") + "%"; }
        }

        public string UpdatedAtText
        {
            get { return UpdatedAt == DateTime.MinValue ? "-" : UpdatedAt.ToString("yyyy-MM-dd HH:mm"); }
        }
    }

    internal sealed class KnowledgeV2Query
    {
        public string Original { get; set; }
        public string Normalized { get; set; }
        public string Intent { get; set; }
        public string Subject { get; set; }
        public string Predicate { get; set; }
        public List<string> Entities { get; set; }
        public bool ContextDependent { get; set; }
        public string WorkingMemoryReason { get; set; }

        public KnowledgeV2Query()
        {
            Entities = new List<string>();
        }
    }

    internal sealed class KnowledgeV2Match
    {
        public KnowledgeV2Record Record { get; set; }
        public double Score { get; set; }
        public double AliasScore { get; set; }
        public double EntityScore { get; set; }
        public double PredicateScore { get; set; }
        public double IntentScore { get; set; }
        public double ConfidenceScore { get; set; }
        public string Reason { get; set; }
    }

    internal sealed class KnowledgeV2Decision
    {
        public bool Enabled { get; set; }
        public string Mode { get; set; }
        public bool CanDirectReply { get; set; }
        public bool HasConflict { get; set; }
        public string Reason { get; set; }
        public string Answer { get; set; }
        public KnowledgeV2Query Query { get; set; }
        public List<KnowledgeV2Match> Matches { get; set; }
        public int CandidateCount { get; set; }
        public long ParseMs { get; set; }
        public long RecallMs { get; set; }
        public long RankMs { get; set; }
        public long DecisionMs { get; set; }
        public long TotalMs { get; set; }

        public KnowledgeV2Decision()
        {
            Matches = new List<KnowledgeV2Match>();
            Reason = string.Empty;
            Answer = string.Empty;
        }
    }

    internal sealed class KnowledgeV2Conflict
    {
        public string FactKey { get; set; }
        public string Subject { get; set; }
        public string Predicate { get; set; }
        public List<KnowledgeV2Record> Records { get; set; }

        public KnowledgeV2Conflict()
        {
            Records = new List<KnowledgeV2Record>();
        }
    }

    internal sealed class KnowledgeV2Stats
    {
        public int Total { get; set; }
        public int BusinessFacts { get; set; }
        public int Procedures { get; set; }
        public int SafetyRules { get; set; }
        public int LearningCandidates { get; set; }
        public int ProductBound { get; set; }
        public int Conflicts { get; set; }
        public DateTime SnapshotBuiltAt { get; set; }
        public string DatabasePath { get; set; }
    }

    internal sealed class KnowledgeV2Settings
    {
        public bool Enabled { get; set; }
        public string Mode { get; set; }
        public double DirectThreshold { get; set; }
        public double MinConfidence { get; set; }
    }

    internal sealed class KnowledgeV2WorkingMemory
    {
        public string Seller { get; set; }
        public string Buyer { get; set; }
        public string Subject { get; set; }
        public string Predicate { get; set; }
        public string Intent { get; set; }
        public List<string> Entities { get; set; }
        public DateTime UpdatedAt { get; set; }

        public KnowledgeV2WorkingMemory()
        {
            Entities = new List<string>();
        }
    }
}
