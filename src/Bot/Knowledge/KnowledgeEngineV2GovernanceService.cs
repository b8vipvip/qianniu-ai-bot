using BotLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Bot.Knowledge
{
    internal sealed class KnowledgeV2GovernanceIssue
    {
        public string IssueId { get; set; }
        public string KnowledgeId { get; set; }
        public string KnowledgeTitle { get; set; }
        public string KnowledgeType { get; set; }
        public string RiskLevel { get; set; }
        public string IssueType { get; set; }
        public string Severity { get; set; }
        public string Evidence { get; set; }
        public string Recommendation { get; set; }
        public int UseCount { get; set; }
        public double QualityScore { get; set; }
        public int PendingRevisionCount { get; set; }
        public DateTime LastVerifiedAt { get; set; }

        public string SeverityText
        {
            get
            {
                switch ((Severity ?? string.Empty).Trim().ToLowerInvariant())
                {
                    case "critical": return "紧急";
                    case "high": return "高";
                    case "medium": return "中";
                    default: return "低";
                }
            }
        }

        public string IssueTypeText
        {
            get
            {
                switch ((IssueType ?? string.Empty).Trim().ToLowerInvariant())
                {
                    case "rollback_recommended": return "修订效果退化";
                    case "conflict": return "知识冲突";
                    case "low_quality": return "低质量";
                    case "quality_watch": return "质量观察";
                    case "multiple_pending_revision": return "多修订候选";
                    case "pending_revision": return "待复核修订";
                    case "verification_due": return "验证已过期";
                    case "unused_stale": return "长期未使用";
                    case "stale_revision": return "过期修订记录";
                    default: return IssueType ?? string.Empty;
                }
            }
        }

        public string QualityText { get { return QualityScore <= 0 ? "-" : (QualityScore * 100).ToString("0") + "%"; } }
        public string LastVerifiedAtText { get { return LastVerifiedAt == DateTime.MinValue ? "-" : LastVerifiedAt.ToString("yyyy-MM-dd"); } }
    }

    internal sealed class KnowledgeV2RevisionImpactItem
    {
        public string CandidateId { get; set; }
        public string KnowledgeId { get; set; }
        public string KnowledgeTitle { get; set; }
        public string RiskLevel { get; set; }
        public string OriginalAnswer { get; set; }
        public string ProposedAnswer { get; set; }
        public string CurrentAnswer { get; set; }
        public DateTime AppliedAt { get; set; }
        public int BeforeSent { get; set; }
        public int BeforeAccepted { get; set; }
        public int BeforeNegative { get; set; }
        public int AfterSent { get; set; }
        public int AfterAccepted { get; set; }
        public int AfterNegative { get; set; }
        public double BeforeNegativeRate { get; set; }
        public double AfterNegativeRate { get; set; }
        public bool RollbackRecommended { get; set; }
        public bool CanRollback { get; set; }
        public string Status { get; set; }
        public string Recommendation { get; set; }

        public string AppliedAtText { get { return AppliedAt == DateTime.MinValue ? "-" : AppliedAt.ToString("MM-dd HH:mm"); } }
        public string BeforeNegativeRateText { get { return (BeforeNegativeRate * 100).ToString("0.0") + "%"; } }
        public string AfterNegativeRateText { get { return (AfterNegativeRate * 100).ToString("0.0") + "%"; } }
        public string BeforeSampleText { get { return BeforeSent + "次 / 负向" + BeforeNegative; } }
        public string AfterSampleText { get { return AfterSent + "次 / 负向" + AfterNegative; } }
        public string RollbackText { get { return RollbackRecommended ? "建议回滚" : "-"; } }
    }

    internal static class KnowledgeEngineV2GovernanceService
    {
        private const int NormalVerificationDays = 180;
        private const int HighRiskVerificationDays = 60;
        private const int UnusedStaleDays = 120;
        private const int ImpactWindowDays = 30;

        public static List<KnowledgeV2GovernanceIssue> Scan(string seller)
        {
            seller = Clean(seller);
            if (seller.Length == 0) throw new InvalidOperationException("无法识别当前店铺客服账号。");

            var records = KnowledgeEngineV2Repository.LoadAll(seller)
                .Where(x => x != null && !string.Equals(x.Status, "deleted", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var quality = KnowledgeEngineV2FeedbackService.GetQualityItems(seller)
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.KnowledgeId))
                .ToDictionary(x => x.KnowledgeId, x => x, StringComparer.Ordinal);
            var conflicts = KnowledgeEngineV2Service.GetConflicts(seller) ?? new List<KnowledgeV2Conflict>();
            var conflictIds = new HashSet<string>(
                conflicts.SelectMany(x => x.Records ?? new List<KnowledgeV2Record>())
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Id))
                    .Select(x => x.Id),
                StringComparer.Ordinal);
            var revisions = KnowledgeEngineV2RevisionService.GetCandidates(seller, "all", 500)
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.KnowledgeId))
                .ToList();
            var pendingByKnowledge = revisions
                .Where(x => string.Equals(x.Status, "pending", StringComparison.OrdinalIgnoreCase))
                .GroupBy(x => x.KnowledgeId, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
            var staleByKnowledge = revisions
                .Where(x => string.Equals(x.Status, "stale", StringComparison.OrdinalIgnoreCase))
                .GroupBy(x => x.KnowledgeId, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
            var rollbackByKnowledge = GetRevisionImpacts(seller)
                .Where(x => x.RollbackRecommended)
                .GroupBy(x => x.KnowledgeId, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.OrderByDescending(y => y.AppliedAt).First(), StringComparer.Ordinal);

            var issues = new List<KnowledgeV2GovernanceIssue>();
            foreach (var record in records)
            {
                var id = record.Id ?? string.Empty;
                KnowledgeV2QualityItem q;
                quality.TryGetValue(id, out q);
                q = q ?? new KnowledgeV2QualityItem { KnowledgeId = id, QualityScore = record.Confidence };
                int pendingCount;
                pendingByKnowledge.TryGetValue(id, out pendingCount);

                KnowledgeV2RevisionImpactItem impact;
                if (rollbackByKnowledge.TryGetValue(id, out impact) && impact != null)
                {
                    issues.Add(BuildIssue(record, q, pendingCount,
                        "rollback_recommended", "critical",
                        "修订后30天窗口负向率由 " + impact.BeforeNegativeRateText + " 上升到 " + impact.AfterNegativeRateText
                            + "；修订后负向证据 " + impact.AfterNegative + " 次。",
                        "优先人工核对修订前后答案；确认退化时可在“修订效果”页一键安全回滚。"));
                }

                if (conflictIds.Contains(id))
                {
                    issues.Add(BuildIssue(record, q, pendingCount,
                        "conflict", "high",
                        "同一 Subject + Predicate 存在多个不一致的生产答案。",
                        "进入知识冲突页保留唯一正确答案；冲突未解决前不要依赖本地直答。"));
                }

                if (record.Enabled && !IsCandidate(record))
                {
                    if (string.Equals(q.HealthStatus, "低质量", StringComparison.Ordinal))
                    {
                        issues.Add(BuildIssue(record, q, pendingCount,
                            "low_quality", "high",
                            "质量 " + q.QualityText + "，命中 " + q.UseCount + "，纠正 " + q.CorrectionCount
                                + "，撤回 " + q.WithdrawCount + "。",
                            pendingCount > 0
                                ? "已有人工纠正聚类生成的修订候选，优先进入修订页复核。"
                                : "先生成修订候选；证据不足时由人工核对知识边界和适用条件。"));
                    }
                    else if (string.Equals(q.HealthStatus, "观察", StringComparison.Ordinal))
                    {
                        issues.Add(BuildIssue(record, q, pendingCount,
                            "quality_watch", "medium",
                            "质量 " + q.QualityText + "，当前已有纠正或撤回信号。",
                            "继续观察真实反馈；若同类人工纠正重复出现，再进入修订流程。"));
                    }

                    if (pendingCount >= 2)
                    {
                        issues.Add(BuildIssue(record, q, pendingCount,
                            "multiple_pending_revision", "high",
                            "同一知识存在 " + pendingCount + " 个待复核修订候选。",
                            "人工比较不同候选及其跨买家证据，只应用一个明确正确的版本。"));
                    }
                    else if (pendingCount == 1)
                    {
                        issues.Add(BuildIssue(record, q, pendingCount,
                            "pending_revision", "medium",
                            "已有1个修订候选等待人工复核。",
                            "进入修订页查看原答案、建议答案和真实人工纠正证据。"));
                    }

                    var verificationDays = IsHighRisk(record) ? HighRiskVerificationDays : NormalVerificationDays;
                    var verifiedAt = record.LastVerifiedAt == DateTime.MinValue ? record.UpdatedAt : record.LastVerifiedAt;
                    if (verifiedAt == DateTime.MinValue || verifiedAt < DateTime.Now.AddDays(-verificationDays))
                    {
                        var age = verifiedAt == DateTime.MinValue ? -1 : Math.Max(0, (int)(DateTime.Now - verifiedAt).TotalDays);
                        issues.Add(BuildIssue(record, q, pendingCount,
                            "verification_due", IsHighRisk(record) ? "high" : "medium",
                            verifiedAt == DateTime.MinValue
                                ? "没有有效的最近人工验证时间。"
                                : "距离最近验证约 " + age + " 天；当前阈值 " + verificationDays + " 天。",
                            "人工确认当前业务事实仍有效后点击“确认仍有效”；若规则已变化则直接编辑或停用。"));
                    }

                    var freshness = MaxDate(record.LastVerifiedAt, record.UpdatedAt, record.CreatedAt);
                    if (q.UseCount == 0 && freshness != DateTime.MinValue && freshness < DateTime.Now.AddDays(-UnusedStaleDays))
                    {
                        issues.Add(BuildIssue(record, q, pendingCount,
                            "unused_stale", "low",
                            "至少 " + Math.Max(0, (int)(DateTime.Now - freshness).TotalDays) + " 天未形成有效使用记录。",
                            "核对是否仍有业务价值；确认废弃后可停用，避免无效知识继续参与召回。"));
                    }
                }

                int staleCount;
                if (staleByKnowledge.TryGetValue(id, out staleCount) && staleCount > 0)
                {
                    issues.Add(BuildIssue(record, q, pendingCount,
                        "stale_revision", "low",
                        "存在 " + staleCount + " 个因原知识变化而过期的修订候选。",
                        "保留为审计记录即可；新的真实纠正达到门槛后会生成新候选。"));
                }
            }

            return issues
                .OrderBy(x => SeverityRank(x.Severity))
                .ThenBy(x => IssueRank(x.IssueType))
                .ThenBy(x => x.QualityScore)
                .ThenBy(x => x.KnowledgeTitle ?? string.Empty)
                .ToList();
        }

        public static List<KnowledgeV2RevisionImpactItem> GetRevisionImpacts(string seller)
        {
            seller = Clean(seller);
            if (seller.Length == 0) return new List<KnowledgeV2RevisionImpactItem>();
            var records = KnowledgeEngineV2Repository.LoadAll(seller)
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Id))
                .ToDictionary(x => x.Id, x => x, StringComparer.Ordinal);
            var applied = KnowledgeEngineV2RevisionService.GetCandidates(seller, "applied", 300)
                .Where(x => x != null && x.AppliedAt != DateTime.MinValue)
                .OrderByDescending(x => x.AppliedAt)
                .ToList();
            var result = new List<KnowledgeV2RevisionImpactItem>();

            foreach (var candidate in applied)
            {
                KnowledgeV2Record record;
                records.TryGetValue(candidate.KnowledgeId ?? string.Empty, out record);
                var events = KnowledgeEngineV2FeedbackService.GetRecentEvents(seller, candidate.KnowledgeId, 100)
                    .Where(x => x != null)
                    .ToList();
                var beforeStart = candidate.AppliedAt.AddDays(-ImpactWindowDays);
                var afterEnd = candidate.AppliedAt.AddDays(ImpactWindowDays);
                if (afterEnd > DateTime.Now) afterEnd = DateTime.Now;
                var before = events.Where(x =>
                {
                    var at = SafeDate(x.CreatedAtTicks);
                    return at >= beforeStart && at < candidate.AppliedAt;
                }).ToList();
                var after = events.Where(x =>
                {
                    var at = SafeDate(x.CreatedAtTicks);
                    return at >= candidate.AppliedAt && at <= afterEnd;
                }).ToList();

                var item = new KnowledgeV2RevisionImpactItem
                {
                    CandidateId = candidate.Id,
                    KnowledgeId = candidate.KnowledgeId,
                    KnowledgeTitle = candidate.KnowledgeTitle,
                    RiskLevel = candidate.RiskLevel,
                    OriginalAnswer = candidate.OriginalAnswer,
                    ProposedAnswer = candidate.ProposedAnswer,
                    CurrentAnswer = record == null ? string.Empty : record.Answer ?? string.Empty,
                    AppliedAt = candidate.AppliedAt,
                    BeforeSent = CountType(before, "sent"),
                    BeforeAccepted = CountType(before, "accepted"),
                    BeforeNegative = CountNegative(before),
                    AfterSent = CountType(after, "sent"),
                    AfterAccepted = CountType(after, "accepted"),
                    AfterNegative = CountNegative(after)
                };
                item.BeforeNegativeRate = item.BeforeNegative / (double)Math.Max(1, item.BeforeSent);
                item.AfterNegativeRate = item.AfterNegative / (double)Math.Max(1, item.AfterSent);

                var currentMatchesProposal = record != null
                    && string.Equals(NormalizeComparable(record.Answer), NormalizeComparable(candidate.ProposedAnswer), StringComparison.Ordinal);
                var currentMatchesOriginal = record != null
                    && string.Equals(NormalizeComparable(record.Answer), NormalizeComparable(candidate.OriginalAnswer), StringComparison.Ordinal);
                item.CanRollback = currentMatchesProposal && !string.IsNullOrWhiteSpace(candidate.OriginalAnswer);

                if (record == null)
                {
                    item.Status = "原知识不存在";
                    item.Recommendation = "原知识已不存在，不执行自动回滚。";
                }
                else if (currentMatchesOriginal && !currentMatchesProposal)
                {
                    item.Status = "已恢复原答案";
                    item.Recommendation = "当前知识已与修订前答案一致，不需要再次回滚。";
                }
                else if (!currentMatchesProposal)
                {
                    item.Status = "后续已修改";
                    item.Recommendation = "修订后知识又被人工修改；为避免覆盖后续修改，不提供自动回滚。";
                }
                else if (item.AfterSent < 3)
                {
                    item.Status = "观察中";
                    item.Recommendation = "修订后有效发送样本不足3次，继续收集真实反馈后再判断。";
                }
                else if (item.AfterNegative >= 2
                    && item.AfterNegativeRate >= Math.Max(0.25, item.BeforeNegativeRate + 0.15))
                {
                    item.RollbackRecommended = true;
                    item.Status = "效果退化";
                    item.Recommendation = "修订后负向率明显上升且已有至少2次负向证据，建议人工核对并考虑回滚。";
                }
                else if (item.BeforeSent >= 3
                    && item.AfterNegativeRate + 0.08 < item.BeforeNegativeRate
                    && item.AfterAccepted >= 2)
                {
                    item.Status = "效果改善";
                    item.Recommendation = "修订后负向率下降且获得多个确认信号，当前修订效果较好。";
                }
                else
                {
                    item.Status = "稳定";
                    item.Recommendation = "当前没有达到回滚门槛，继续观察真实使用反馈。";
                }
                result.Add(item);
            }

            return result
                .OrderBy(x => x.RollbackRecommended ? 0 : (x.Status == "观察中" ? 2 : 1))
                .ThenByDescending(x => x.AppliedAt)
                .ToList();
        }

        public static bool MarkVerified(string seller, string knowledgeId, out string error)
        {
            error = string.Empty;
            var record = FindRecord(seller, knowledgeId);
            if (record == null)
            {
                error = "知识不存在。";
                return false;
            }
            record.LastVerifiedAt = DateTime.Now;
            KnowledgeEngineV2Repository.Save(seller, record);
            KnowledgeEngineV2Service.Warm(seller);
            Log.Info("Knowledge V2治理：人工确认知识仍有效: seller=" + seller + ", knowledgeId=" + knowledgeId);
            return true;
        }

        public static bool DisableKnowledge(string seller, string knowledgeId, out string error)
        {
            error = string.Empty;
            var record = FindRecord(seller, knowledgeId);
            if (record == null)
            {
                error = "知识不存在。";
                return false;
            }
            if (!record.Enabled)
            {
                error = "该知识已经停用。";
                return false;
            }
            record.Enabled = false;
            record.Status = "disabled";
            record.LastVerifiedAt = DateTime.Now;
            KnowledgeEngineV2Repository.Save(seller, record);
            KnowledgeEngineV2Service.Warm(seller);
            Log.Info("Knowledge V2治理：人工停用知识: seller=" + seller + ", knowledgeId=" + knowledgeId);
            return true;
        }

        public static bool RollbackRevision(string seller, string candidateId, out string error)
        {
            error = string.Empty;
            seller = Clean(seller);
            candidateId = Clean(candidateId);
            var candidate = KnowledgeEngineV2RevisionService.GetCandidates(seller, "applied", 500)
                .FirstOrDefault(x => x != null && string.Equals(x.Id, candidateId, StringComparison.Ordinal));
            if (candidate == null)
            {
                error = "没有找到已应用的修订记录。";
                return false;
            }
            var record = FindRecord(seller, candidate.KnowledgeId);
            if (record == null)
            {
                error = "原知识已不存在，不能回滚。";
                return false;
            }
            if (!string.Equals(NormalizeComparable(record.Answer), NormalizeComparable(candidate.ProposedAnswer), StringComparison.Ordinal))
            {
                error = "当前知识在修订后又发生了变化，为避免覆盖后续人工修改，已拒绝自动回滚。";
                return false;
            }
            if (string.IsNullOrWhiteSpace(candidate.OriginalAnswer))
            {
                error = "修订审计记录中没有原答案，不能自动回滚。";
                return false;
            }

            record.Answer = candidate.OriginalAnswer.Trim();
            record.LastVerifiedAt = DateTime.Now;
            KnowledgeEngineV2Repository.Save(seller, record);
            KnowledgeEngineV2Service.Warm(seller);
            Log.Info("Knowledge V2治理：人工回滚修订: seller=" + seller + ", knowledgeId=" + candidate.KnowledgeId
                + ", candidateId=" + candidate.Id + ", beforeNegative=" + candidate.OriginalAnswer.Length
                + ", proposedLength=" + (candidate.ProposedAnswer ?? string.Empty).Length);
            return true;
        }

        private static KnowledgeV2GovernanceIssue BuildIssue(KnowledgeV2Record record, KnowledgeV2QualityItem q,
            int pendingCount, string issueType, string severity, string evidence, string recommendation)
        {
            return new KnowledgeV2GovernanceIssue
            {
                IssueId = issueType + "|" + (record.Id ?? string.Empty),
                KnowledgeId = record.Id ?? string.Empty,
                KnowledgeTitle = string.IsNullOrWhiteSpace(record.Title) ? "(无标题知识)" : record.Title,
                KnowledgeType = record.Type ?? string.Empty,
                RiskLevel = record.RiskLevel ?? string.Empty,
                IssueType = issueType,
                Severity = severity,
                Evidence = evidence ?? string.Empty,
                Recommendation = recommendation ?? string.Empty,
                UseCount = q == null ? 0 : q.UseCount,
                QualityScore = q == null ? record.Confidence : q.QualityScore,
                PendingRevisionCount = pendingCount,
                LastVerifiedAt = record.LastVerifiedAt
            };
        }

        private static KnowledgeV2Record FindRecord(string seller, string knowledgeId)
        {
            knowledgeId = Clean(knowledgeId);
            if (knowledgeId.Length == 0) return null;
            return KnowledgeEngineV2Repository.LoadAll(seller)
                .FirstOrDefault(x => x != null && string.Equals(x.Id, knowledgeId, StringComparison.Ordinal));
        }

        private static bool IsCandidate(KnowledgeV2Record record)
        {
            return record != null && (string.Equals(record.Status, "candidate", StringComparison.OrdinalIgnoreCase)
                || string.Equals(record.Type, "learning_candidate", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsHighRisk(KnowledgeV2Record record)
        {
            if (record == null) return true;
            return string.Equals(record.RiskLevel, "high", StringComparison.OrdinalIgnoreCase)
                || string.Equals(record.RiskLevel, "critical", StringComparison.OrdinalIgnoreCase)
                || string.Equals(record.Type, "safety_rule", StringComparison.OrdinalIgnoreCase)
                || KnowledgeEngineV2Semantics.IsHighRisk((record.Title ?? string.Empty) + " " + (record.Answer ?? string.Empty));
        }

        private static int CountType(IEnumerable<KnowledgeV2FeedbackEventRow> events, string type)
        {
            return (events ?? Enumerable.Empty<KnowledgeV2FeedbackEventRow>())
                .Count(x => x != null && string.Equals(x.EventType, type, StringComparison.OrdinalIgnoreCase));
        }

        private static int CountNegative(IEnumerable<KnowledgeV2FeedbackEventRow> events)
        {
            return (events ?? Enumerable.Empty<KnowledgeV2FeedbackEventRow>())
                .Count(x => x != null && (string.Equals(x.EventType, "correction", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(x.EventType, "withdrawal", StringComparison.OrdinalIgnoreCase)));
        }

        private static DateTime SafeDate(long ticks)
        {
            try { return ticks <= 0 ? DateTime.MinValue : new DateTime(ticks); }
            catch { return DateTime.MinValue; }
        }

        private static DateTime MaxDate(params DateTime[] values)
        {
            var best = DateTime.MinValue;
            foreach (var value in values ?? new DateTime[0]) if (value > best) best = value;
            return best;
        }

        private static string NormalizeComparable(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ");
        }

        private static string Clean(string value)
        {
            return (value ?? string.Empty).Trim();
        }

        private static int SeverityRank(string severity)
        {
            switch ((severity ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "critical": return 0;
                case "high": return 1;
                case "medium": return 2;
                default: return 3;
            }
        }

        private static int IssueRank(string issueType)
        {
            switch ((issueType ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "rollback_recommended": return 0;
                case "conflict": return 1;
                case "low_quality": return 2;
                case "multiple_pending_revision": return 3;
                case "pending_revision": return 4;
                case "verification_due": return 5;
                case "quality_watch": return 6;
                case "unused_stale": return 7;
                default: return 8;
            }
        }
    }
}
