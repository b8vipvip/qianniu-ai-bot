using BotLib;
using BotLib.Db.Sqlite;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal sealed class VisualKnowledgeObservationEntity
    {
        [PrimaryKey]
        public string EntityId { get; set; }
        public string Seller { get; set; }
        public string Buyer { get; set; }
        public string MessageKey { get; set; }
        public string VisualQuestion { get; set; }
        public string VisualSummary { get; set; }
        public string VisualTags { get; set; }
        public string GeneratedAnswer { get; set; }
        public string Status { get; set; }
        public string LearnedKnowledgeId { get; set; }
        public long ObservedAtTicks { get; set; }
        public long UpdatedAtTicks { get; set; }
    }

    internal sealed class VisualKnowledgeEntryEntity
    {
        [PrimaryKey]
        public string EntityId { get; set; }
        public string Seller { get; set; }
        public string VisualQuestion { get; set; }
        public string VisualSummary { get; set; }
        public string VisualTags { get; set; }
        public string Answer { get; set; }
        public string SourceType { get; set; }
        public int Confirmations { get; set; }
        public bool Enabled { get; set; }
        public long CreatedAtTicks { get; set; }
        public long UpdatedAtTicks { get; set; }
    }

    internal sealed class VisualKnowledgeMatch
    {
        public string KnowledgeId { get; set; }
        public string Answer { get; set; }
        public string VisualQuestion { get; set; }
        public string VisualSummary { get; set; }
        public string VisualTags { get; set; }
        public double Score { get; set; }
    }

    internal static class VisualKnowledgeLearningService
    {
        public const int BuyerQuietMinutes = 5;
        public const int SellerQuietSeconds = 30;
        public const int ObservationRetentionDays = 30;
        public const int MaxObservations = 2000;
        public const int MaxKnowledgeEntriesPerSeller = 500;
        public const double MatchThreshold = 0.74;

        private static readonly object DbSync = new object();
        private static readonly ConcurrentDictionary<string, Timer> Timers =
            new ConcurrentDictionary<string, Timer>(StringComparer.Ordinal);
        private static int _initialized;
        private static int _schemaReady;

        public static void RecordVisionAnalysis(
            string seller,
            string buyer,
            QNChatMessage message,
            string messageKey,
            string visualQuestion,
            string visualSummary,
            string visualTags,
            string generatedAnswer)
        {
            seller = Clean(seller, 160);
            buyer = Clean(buyer, 160);
            visualQuestion = Clean(visualQuestion, 500);
            visualSummary = Clean(visualSummary, 1600);
            visualTags = NormalizeTags(visualTags);
            generatedAnswer = Clean(generatedAnswer, 1200);
            if (seller.Length == 0 || buyer.Length == 0 || visualSummary.Length == 0) return;

            EnsureInitialized();
            var observedAt = GetMessageTime(message);
            if (observedAt == DateTime.MinValue || observedAt > DateTime.Now.AddMinutes(2)) observedAt = DateTime.Now;
            var entity = new VisualKnowledgeObservationEntity
            {
                EntityId = Guid.NewGuid().ToString("N"),
                Seller = seller,
                Buyer = buyer,
                MessageKey = Clean(messageKey, 500),
                VisualQuestion = visualQuestion,
                VisualSummary = visualSummary,
                VisualTags = visualTags,
                GeneratedAnswer = generatedAnswer,
                Status = "等待人工接待结束",
                LearnedKnowledgeId = string.Empty,
                ObservedAtTicks = observedAt.Ticks,
                UpdatedAtTicks = DateTime.Now.Ticks
            };
            lock (DbSync)
            {
                var duplicate = (DbHelper.Db.Select(
                    typeof(VisualKnowledgeObservationEntity),
                    "where Seller = ? and Buyer = ? and MessageKey = ? order by UpdatedAtTicks desc limit 1",
                    seller, buyer, entity.MessageKey) ?? new List<object>())
                    .OfType<VisualKnowledgeObservationEntity>()
                    .FirstOrDefault();
                if (duplicate != null && !string.IsNullOrWhiteSpace(entity.MessageKey))
                {
                    duplicate.VisualQuestion = entity.VisualQuestion;
                    duplicate.VisualSummary = entity.VisualSummary;
                    duplicate.VisualTags = entity.VisualTags;
                    duplicate.GeneratedAnswer = entity.GeneratedAnswer;
                    duplicate.UpdatedAtTicks = DateTime.Now.Ticks;
                    entity = duplicate;
                }
                DbHelper.Db.SaveRecordsInTransaction(new List<object> { entity });
            }
            Schedule(entity.EntityId, TimeSpan.FromMinutes(BuyerQuietMinutes));
            Cleanup();
            Log.Info("已记录视觉知识学习候选: seller=" + seller + ", buyer=" + buyer
                + ", summary=" + Short(visualSummary, 120));
        }

        public static bool TryFindMatch(
            string seller,
            string visualQuestion,
            string visualSummary,
            string visualTags,
            out VisualKnowledgeMatch match)
        {
            match = null;
            seller = Clean(seller, 160);
            visualQuestion = Clean(visualQuestion, 500);
            visualSummary = Clean(visualSummary, 1600);
            visualTags = NormalizeTags(visualTags);
            if (seller.Length == 0 || visualSummary.Length == 0) return false;

            EnsureInitialized();
            List<VisualKnowledgeEntryEntity> entries;
            lock (DbSync)
            {
                entries = (DbHelper.Db.Select(
                    typeof(VisualKnowledgeEntryEntity),
                    "where Seller = ? and Enabled = 1 order by UpdatedAtTicks desc limit " + MaxKnowledgeEntriesPerSeller,
                    seller) ?? new List<object>())
                    .OfType<VisualKnowledgeEntryEntity>()
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Answer))
                    .ToList();
            }

            VisualKnowledgeEntryEntity best = null;
            var bestScore = 0.0;
            foreach (var entry in entries)
            {
                var score = Similarity(
                    visualQuestion,
                    visualSummary,
                    visualTags,
                    entry.VisualQuestion,
                    entry.VisualSummary,
                    entry.VisualTags);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = entry;
                }
            }
            if (best == null || bestScore < MatchThreshold) return false;

            match = new VisualKnowledgeMatch
            {
                KnowledgeId = best.EntityId,
                Answer = best.Answer,
                VisualQuestion = best.VisualQuestion,
                VisualSummary = best.VisualSummary,
                VisualTags = best.VisualTags,
                Score = bestScore
            };
            Log.Info("命中视觉人工知识: seller=" + seller + ", knowledgeId=" + best.EntityId
                + ", score=" + bestScore.ToString("0.00", CultureInfo.InvariantCulture));
            return true;
        }

        private static void EnsureInitialized()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            EnsureSchema();
            ResumePending();
        }

        private static void Schedule(string entityId, TimeSpan due)
        {
            if (string.IsNullOrWhiteSpace(entityId)) return;
            Timer old;
            if (Timers.TryRemove(entityId, out old))
            {
                try { old.Dispose(); } catch { }
            }
            var ms = (long)Math.Max(1000, due.TotalMilliseconds);
            Timer timer = null;
            timer = new Timer(_ =>
            {
                Timer removed;
                if (Timers.TryRemove(entityId, out removed))
                {
                    try { removed.Dispose(); } catch { }
                }
                Task.Run(() => FinalizeObservation(entityId));
            }, null, (int)Math.Min(int.MaxValue, ms), Timeout.Infinite);
            Timers[entityId] = timer;
        }

        private static void FinalizeObservation(string entityId)
        {
            var observation = LoadObservation(entityId);
            if (observation == null || !string.Equals(observation.Status, "等待人工接待结束", StringComparison.Ordinal)) return;

            var observedAt = FromTicks(observation.ObservedAtTicks);
            var turns = ConversationContextStore.GetRecentTurns(observation.Seller, observation.Buyer, string.Empty, 24)
                .Where(x => x != null && x.Timestamp != DateTime.MinValue)
                .OrderBy(x => x.Timestamp)
                .ToList();

            var latestBuyer = turns
                .Where(x => x.Role == "user" && x.Timestamp >= observedAt.AddSeconds(-3))
                .OrderByDescending(x => x.Timestamp)
                .FirstOrDefault();
            if (latestBuyer != null)
            {
                var remaining = TimeSpan.FromMinutes(BuyerQuietMinutes) - (DateTime.Now - latestBuyer.Timestamp);
                if (remaining > TimeSpan.Zero)
                {
                    Schedule(entityId, remaining);
                    return;
                }
            }

            var latestSeller = turns
                .Where(x => x.Role == "assistant" && x.Timestamp >= observedAt.AddSeconds(-3))
                .OrderByDescending(x => x.Timestamp)
                .FirstOrDefault();
            if (latestSeller != null)
            {
                var sellerRemaining = TimeSpan.FromSeconds(SellerQuietSeconds) - (DateTime.Now - latestSeller.Timestamp);
                if (sellerRemaining > TimeSpan.Zero)
                {
                    Schedule(entityId, sellerRemaining);
                    return;
                }
            }

            var humanReply = turns
                .Where(x => x.Role == "assistant" && !x.Withdrawn)
                .Where(x => x.Timestamp >= observedAt.AddSeconds(-5))
                .Where(x => !IsBotReply(x.Text))
                .Where(x => !string.IsNullOrWhiteSpace(x.Text))
                .OrderByDescending(x => x.Timestamp)
                .FirstOrDefault();

            if (humanReply == null)
            {
                observation.Status = "未发现人工确认回复";
                observation.UpdatedAtTicks = DateTime.Now.Ticks;
                SaveObservation(observation);
                return;
            }

            var answer = StripAi(Clean(humanReply.Text, 1200));
            if (answer.Length == 0 || ContainsHighRisk(answer))
            {
                observation.Status = "人工回复不适合自动学习";
                observation.UpdatedAtTicks = DateTime.Now.Ticks;
                SaveObservation(observation);
                return;
            }

            var knowledge = UpsertKnowledge(observation, answer);
            observation.Status = knowledge == null ? "视觉知识写入失败" : "已学习";
            observation.LearnedKnowledgeId = knowledge == null ? string.Empty : knowledge.EntityId;
            observation.UpdatedAtTicks = DateTime.Now.Ticks;
            SaveObservation(observation);
            if (knowledge != null)
            {
                Log.Info("视觉人工知识学习完成: seller=" + observation.Seller + ", buyer=" + observation.Buyer
                    + ", knowledgeId=" + knowledge.EntityId + ", answer=" + Short(answer, 120));
            }
        }

        private static VisualKnowledgeEntryEntity UpsertKnowledge(
            VisualKnowledgeObservationEntity observation,
            string answer)
        {
            try
            {
                lock (DbSync)
                {
                    var entries = (DbHelper.Db.Select(
                        typeof(VisualKnowledgeEntryEntity),
                        "where Seller = ? and Enabled = 1 order by UpdatedAtTicks desc limit " + MaxKnowledgeEntriesPerSeller,
                        observation.Seller) ?? new List<object>())
                        .OfType<VisualKnowledgeEntryEntity>()
                        .ToList();
                    VisualKnowledgeEntryEntity best = null;
                    var bestScore = 0.0;
                    foreach (var entry in entries)
                    {
                        var score = Similarity(
                            observation.VisualQuestion,
                            observation.VisualSummary,
                            observation.VisualTags,
                            entry.VisualQuestion,
                            entry.VisualSummary,
                            entry.VisualTags);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = entry;
                        }
                    }

                    var now = DateTime.Now.Ticks;
                    if (best != null && bestScore >= 0.70)
                    {
                        best.VisualQuestion = PreferLonger(best.VisualQuestion, observation.VisualQuestion);
                        best.VisualSummary = PreferLonger(best.VisualSummary, observation.VisualSummary);
                        best.VisualTags = MergeTags(best.VisualTags, observation.VisualTags);
                        best.Answer = answer;
                        best.SourceType = "视觉人工学习";
                        best.Confirmations = Math.Max(1, best.Confirmations) + 1;
                        best.Enabled = true;
                        best.UpdatedAtTicks = now;
                        DbHelper.Db.SaveRecordsInTransaction(new List<object> { best });
                        return best;
                    }

                    var created = new VisualKnowledgeEntryEntity
                    {
                        EntityId = Guid.NewGuid().ToString("N"),
                        Seller = observation.Seller,
                        VisualQuestion = observation.VisualQuestion,
                        VisualSummary = observation.VisualSummary,
                        VisualTags = observation.VisualTags,
                        Answer = answer,
                        SourceType = "视觉人工学习",
                        Confirmations = 1,
                        Enabled = true,
                        CreatedAtTicks = now,
                        UpdatedAtTicks = now
                    };
                    DbHelper.Db.SaveRecordsInTransaction(new List<object> { created });
                    DbHelper.Db.Execute(
                        "delete from VisualKnowledgeEntryEntity where Seller = ? and EntityId not in (select EntityId from VisualKnowledgeEntryEntity where Seller = ? order by UpdatedAtTicks desc limit "
                        + MaxKnowledgeEntriesPerSeller + ")",
                        observation.Seller, observation.Seller);
                    return created;
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("写入视觉人工知识失败：" + ex.Message, 20);
                return null;
            }
        }

        private static void ResumePending()
        {
            try
            {
                List<VisualKnowledgeObservationEntity> pending;
                lock (DbSync)
                {
                    pending = (DbHelper.Db.Select(
                        typeof(VisualKnowledgeObservationEntity),
                        "where Status = ? and ObservedAtTicks >= ? order by UpdatedAtTicks desc limit 200",
                        "等待人工接待结束", DateTime.Now.AddDays(-1).Ticks) ?? new List<object>())
                        .OfType<VisualKnowledgeObservationEntity>()
                        .ToList();
                }
                foreach (var item in pending)
                {
                    var observedAt = FromTicks(item.ObservedAtTicks);
                    var due = TimeSpan.FromMinutes(BuyerQuietMinutes) - (DateTime.Now - observedAt);
                    Schedule(item.EntityId, due > TimeSpan.Zero ? due : TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("恢复视觉知识学习任务失败：" + ex.Message, 10);
            }
        }

        private static VisualKnowledgeObservationEntity LoadObservation(string entityId)
        {
            EnsureSchema();
            lock (DbSync)
            {
                return (DbHelper.Db.Select(
                    typeof(VisualKnowledgeObservationEntity),
                    "where EntityId = ? limit 1", entityId) ?? new List<object>())
                    .OfType<VisualKnowledgeObservationEntity>()
                    .FirstOrDefault();
            }
        }

        private static void SaveObservation(VisualKnowledgeObservationEntity entity)
        {
            if (entity == null) return;
            EnsureSchema();
            lock (DbSync)
            {
                DbHelper.Db.SaveRecordsInTransaction(new List<object> { entity });
            }
        }

        private static void EnsureSchema()
        {
            if (Volatile.Read(ref _schemaReady) != 0) return;
            lock (DbSync)
            {
                if (_schemaReady != 0) return;
                DbHelper.Db.Execute("create table if not exists VisualKnowledgeObservationEntity (EntityId text primary key not null,Seller text,Buyer text,MessageKey text,VisualQuestion text,VisualSummary text,VisualTags text,GeneratedAnswer text,Status text,LearnedKnowledgeId text,ObservedAtTicks integer not null default 0,UpdatedAtTicks integer not null default 0)");
                DbHelper.Db.Execute("create index if not exists IX_VisualKnowledgeObservation_SellerBuyer on VisualKnowledgeObservationEntity(Seller,Buyer,ObservedAtTicks)");
                DbHelper.Db.Execute("create table if not exists VisualKnowledgeEntryEntity (EntityId text primary key not null,Seller text,VisualQuestion text,VisualSummary text,VisualTags text,Answer text,SourceType text,Confirmations integer not null default 0,Enabled integer not null default 1,CreatedAtTicks integer not null default 0,UpdatedAtTicks integer not null default 0)");
                DbHelper.Db.Execute("create index if not exists IX_VisualKnowledgeEntry_Seller on VisualKnowledgeEntryEntity(Seller,Enabled,UpdatedAtTicks)");
                Volatile.Write(ref _schemaReady, 1);
            }
        }

        private static void Cleanup()
        {
            try
            {
                lock (DbSync)
                {
                    DbHelper.Db.Execute(
                        "delete from VisualKnowledgeObservationEntity where UpdatedAtTicks < ?",
                        DateTime.Now.AddDays(-ObservationRetentionDays).Ticks);
                    DbHelper.Db.Execute(
                        "delete from VisualKnowledgeObservationEntity where EntityId not in (select EntityId from VisualKnowledgeObservationEntity order by UpdatedAtTicks desc limit "
                        + MaxObservations + ")");
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("清理视觉知识学习记录失败：" + ex.Message, 10);
            }
        }

        private static double Similarity(
            string questionA,
            string summaryA,
            string tagsA,
            string questionB,
            string summaryB,
            string tagsB)
        {
            var tagSetA = SplitTags(tagsA);
            var tagSetB = SplitTags(tagsB);
            var intersection = tagSetA.Intersect(tagSetB).Count();
            var union = tagSetA.Union(tagSetB).Count();
            var tagScore = union <= 0 ? 0 : (double)intersection / union;
            var summaryScore = BigramSimilarity(Normalize(summaryA), Normalize(summaryB));
            var questionScore = BigramSimilarity(Normalize(questionA), Normalize(questionB));
            var weighted = Math.Max(
                summaryScore * 0.70 + tagScore * 0.30,
                questionScore * 0.75 + tagScore * 0.25);
            if (tagSetA.Count > 0 && tagSetB.Count > 0 && intersection == 0 && weighted < 0.88)
            {
                weighted *= 0.72;
            }
            return Math.Max(0, Math.Min(1, weighted));
        }

        private static HashSet<string> SplitTags(string value)
        {
            return new HashSet<string>(
                (value ?? string.Empty)
                    .Split(new[] { ',', '，', ';', '；', '|', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => Normalize(x))
                    .Where(x => x.Length >= 2),
                StringComparer.Ordinal);
        }

        private static double BigramSimilarity(string a, string b)
        {
            if (a.Length == 0 || b.Length == 0) return 0;
            if (a == b) return 1;
            var aa = Bigrams(a);
            var bb = Bigrams(b);
            if (aa.Count == 0 || bb.Count == 0) return 0;
            var common = aa.Intersect(bb).Count();
            return (2.0 * common) / (aa.Count + bb.Count);
        }

        private static HashSet<string> Bigrams(string value)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i + 1 < value.Length; i++) result.Add(value.Substring(i, 2));
            return result;
        }

        private static string MergeTags(string left, string right)
        {
            return string.Join(",", SplitTags(left).Union(SplitTags(right)).Take(20));
        }

        private static string NormalizeTags(string value)
        {
            return string.Join(",", SplitTags(value).Take(20));
        }

        private static string PreferLonger(string left, string right)
        {
            left = left ?? string.Empty;
            right = right ?? string.Empty;
            return right.Length > left.Length ? right : left;
        }

        private static bool IsBotReply(string value)
        {
            value = (value ?? string.Empty).Trim();
            return value.EndsWith("[AI]", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsHighRisk(string value)
        {
            var terms = new[] { "退款", "退货", "赔偿", "投诉", "差评", "举报", "仲裁", "身份证", "银行卡", "验证码", "密码", "订单隐私", "订单号", "手机号", "账号安全", "封号", "解封", "法律", "报警" };
            return terms.Any(x => (value ?? string.Empty).IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static DateTime GetMessageTime(QNChatMessage message)
        {
            if (message == null) return DateTime.MinValue;
            DateTime result;
            if (TryParseTime(message.sendTime, out result)) return result;
            if (TryParseTime(message.sortTimeMicrosecond, out result)) return result;
            return DateTime.MinValue;
        }

        private static bool TryParseTime(string value, out DateTime result)
        {
            result = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(value)) return false;
            long raw;
            if (long.TryParse(value.Trim(), out raw))
            {
                try
                {
                    if (raw > 1000000000000000L) result = DateTimeOffset.FromUnixTimeMilliseconds(raw / 1000L).LocalDateTime;
                    else if (raw > 100000000000L) result = DateTimeOffset.FromUnixTimeMilliseconds(raw).LocalDateTime;
                    else if (raw > 1000000000L) result = DateTimeOffset.FromUnixTimeSeconds(raw).LocalDateTime;
                    if (result != DateTime.MinValue) return true;
                }
                catch { }
            }
            DateTimeOffset dto;
            if (DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out dto)
                || DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dto))
            {
                result = dto.LocalDateTime;
                return true;
            }
            return false;
        }

        private static DateTime FromTicks(long ticks)
        {
            try { return ticks > 0 ? new DateTime(ticks) : DateTime.MinValue; }
            catch { return DateTime.MinValue; }
        }

        private static string StripAi(string value)
        {
            value = (value ?? string.Empty).Trim();
            while (value.EndsWith("[AI]", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(0, value.Length - 4).TrimEnd();
            }
            return value;
        }

        private static string Normalize(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"[\s\p{P}\p{S}]+", string.Empty);
        }

        private static string Clean(string value, int max)
        {
            value = (value ?? string.Empty).Trim();
            if (max > 0 && value.Length > max) value = value.Substring(0, max).Trim();
            return value;
        }

        private static string Short(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length > max ? value.Substring(0, max) + "..." : value;
        }
    }
}
