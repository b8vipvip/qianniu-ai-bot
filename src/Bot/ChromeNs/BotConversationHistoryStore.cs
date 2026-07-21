using Bot.Common;
using BotLib;
using BotLib.Db.Sqlite;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Bot.ChromeNs
{
    internal sealed class BotConversationHistoryEntity
    {
        [PrimaryKey]
        public string EntityId { get; set; }
        public string Seller { get; set; }
        public string Buyer { get; set; }
        public string Question { get; set; }
        public string Answer { get; set; }
        public string AnswerSource { get; set; }
        public string StatusText { get; set; }
        public string StatusKind { get; set; }
        public string StatusDetail { get; set; }
        public bool CanResend { get; set; }
        public long QuestionDetectedAtTicks { get; set; }
        public long AnswerReadyAtTicks { get; set; }
        public long CreatedAtTicks { get; set; }
        public long UpdatedAtTicks { get; set; }
    }

    internal static class BotConversationHistoryStore
    {
        public const int RetentionDays = 90;
        public const int MaxTotalRecords = 10000;
        public const int MaxRecordsPerConversation = 200;
        public const int DefaultLoadCount = 100;

        private static readonly object DbSync = new object();
        private static readonly ConcurrentDictionary<string, BotConversationHistoryEntity> Pending =
            new ConcurrentDictionary<string, BotConversationHistoryEntity>(StringComparer.Ordinal);

        private static int _initialized;
        private static int _schemaReady;
        private static int _flushScheduled;
        private static int _cleanupScheduled;
        private static int _savedSinceCleanup;

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;

            try
            {
                if (Application.Current != null)
                {
                    Application.Current.Exit += (s, e) => FlushNow();
                }
            }
            catch
            {
            }

            Task.Run(async () =>
            {
                await Task.Delay(1500);
                CleanupNow();
            });

            Log.Info("Bot问答历史持久化已启用：保留" + RetentionDays
                + "天，全局最多" + MaxTotalRecords
                + "条，单买家最多" + MaxRecordsPerConversation + "条。");
        }

        public static void QueueSave(BotConversationHistoryEntity entity)
        {
            if (entity == null) return;
            Initialize();

            var copy = Clone(entity);
            copy.EntityId = (copy.EntityId ?? string.Empty).Trim();
            copy.Seller = (copy.Seller ?? string.Empty).Trim();
            copy.Buyer = (copy.Buyer ?? string.Empty).Trim();
            if (copy.EntityId.Length == 0 || copy.Seller.Length == 0 || copy.Buyer.Length == 0) return;

            var nowTicks = DateTime.Now.Ticks;
            if (copy.CreatedAtTicks <= 0)
            {
                copy.CreatedAtTicks = copy.QuestionDetectedAtTicks > 0
                    ? copy.QuestionDetectedAtTicks
                    : nowTicks;
            }
            copy.UpdatedAtTicks = nowTicks;
            Pending[copy.EntityId] = copy;
            ScheduleFlush();
        }

        public static List<BotConversationHistoryEntity> LoadRecent(string seller, string buyer, int maxCount = DefaultLoadCount)
        {
            Initialize();
            FlushNow();
            EnsureSchema();

            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();
            if (seller.Length == 0 || buyer.Length == 0) return new List<BotConversationHistoryEntity>();

            var take = Math.Max(1, Math.Min(MaxRecordsPerConversation, maxCount <= 0 ? DefaultLoadCount : maxCount));
            var cutoffTicks = DateTime.Now.AddDays(-RetentionDays).Ticks;
            try
            {
                lock (DbSync)
                {
                    var rows = DbHelper.Db.Select(
                        typeof(BotConversationHistoryEntity),
                        "where Seller = ? and Buyer = ? and UpdatedAtTicks >= ? order by CreatedAtTicks desc limit " + take,
                        seller,
                        buyer,
                        cutoffTicks);
                    return (rows ?? new List<object>())
                        .OfType<BotConversationHistoryEntity>()
                        .Where(x => x != null && !string.IsNullOrWhiteSpace(x.EntityId))
                        .OrderBy(x => x.CreatedAtTicks)
                        .ThenBy(x => x.UpdatedAtTicks)
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("读取Bot问答历史失败：" + ex.Message, 10);
                return new List<BotConversationHistoryEntity>();
            }
        }

        public static void FlushNow()
        {
            Initialize();
            FlushPendingCore();
        }

        public static void CleanupNow()
        {
            if (Interlocked.Exchange(ref _cleanupScheduled, 1) != 0) return;
            try
            {
                EnsureSchema();
                FlushPendingCore();
                var cutoffTicks = DateTime.Now.AddDays(-RetentionDays).Ticks;
                int expired;
                int overflow;
                lock (DbSync)
                {
                    expired = DbHelper.Db.Execute(
                        "delete from BotConversationHistoryEntity where UpdatedAtTicks < ?",
                        cutoffTicks);
                    overflow = DbHelper.Db.Execute(
                        "delete from BotConversationHistoryEntity where EntityId not in "
                        + "(select EntityId from BotConversationHistoryEntity order by UpdatedAtTicks desc limit "
                        + MaxTotalRecords + ")");
                }
                if (expired > 0 || overflow > 0)
                {
                    Log.Info("Bot问答历史清理完成：过期=" + expired + "，超限=" + overflow);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("清理Bot问答历史失败：" + ex.Message, 10);
            }
            finally
            {
                Interlocked.Exchange(ref _cleanupScheduled, 0);
            }
        }

        private static void ScheduleFlush()
        {
            if (Interlocked.Exchange(ref _flushScheduled, 1) != 0) return;
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(250);
                    FlushPendingCore();
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("写入Bot问答历史失败：" + ex.Message, 10);
                }
                finally
                {
                    Interlocked.Exchange(ref _flushScheduled, 0);
                    if (!Pending.IsEmpty) ScheduleFlush();
                }
            });
        }

        private static void FlushPendingCore()
        {
            var batch = new List<BotConversationHistoryEntity>();
            foreach (var pair in Pending.ToArray())
            {
                BotConversationHistoryEntity removed;
                if (Pending.TryRemove(pair.Key, out removed) && removed != null)
                {
                    batch.Add(removed);
                }
            }
            if (batch.Count == 0) return;

            EnsureSchema();
            try
            {
                lock (DbSync)
                {
                    DbHelper.Db.SaveRecordsInTransaction(batch.Cast<object>().ToList());
                    foreach (var conversation in batch
                        .Select(x => new { x.Seller, x.Buyer })
                        .Distinct())
                    {
                        TrimConversationUnsafe(conversation.Seller, conversation.Buyer);
                    }
                }

                if (Interlocked.Add(ref _savedSinceCleanup, batch.Count) >= 200)
                {
                    Interlocked.Exchange(ref _savedSinceCleanup, 0);
                    Task.Run(() => CleanupNow());
                }
            }
            catch
            {
                foreach (var entity in batch)
                {
                    Pending[entity.EntityId] = entity;
                }
                throw;
            }
        }

        private static void TrimConversationUnsafe(string seller, string buyer)
        {
            DbHelper.Db.Execute(
                "delete from BotConversationHistoryEntity where Seller = ? and Buyer = ? and EntityId not in "
                + "(select EntityId from BotConversationHistoryEntity where Seller = ? and Buyer = ? "
                + "order by UpdatedAtTicks desc limit " + MaxRecordsPerConversation + ")",
                seller,
                buyer,
                seller,
                buyer);
        }

        private static void EnsureSchema()
        {
            if (Volatile.Read(ref _schemaReady) != 0) return;
            lock (DbSync)
            {
                if (_schemaReady != 0) return;

                DbHelper.Db.Execute(
                    "create table if not exists BotConversationHistoryEntity ("
                    + "EntityId text primary key not null,"
                    + "Seller text not null,"
                    + "Buyer text not null,"
                    + "Question text,"
                    + "Answer text,"
                    + "AnswerSource text,"
                    + "StatusText text,"
                    + "StatusKind text,"
                    + "StatusDetail text,"
                    + "CanResend integer not null default 1,"
                    + "QuestionDetectedAtTicks integer not null default 0,"
                    + "AnswerReadyAtTicks integer not null default 0,"
                    + "CreatedAtTicks integer not null default 0,"
                    + "UpdatedAtTicks integer not null default 0"
                    + ")");
                DbHelper.Db.Execute(
                    "create index if not exists IX_BotConversationHistory_SellerBuyerUpdated "
                    + "on BotConversationHistoryEntity(Seller, Buyer, UpdatedAtTicks)");
                DbHelper.Db.Execute(
                    "create index if not exists IX_BotConversationHistory_Updated "
                    + "on BotConversationHistoryEntity(UpdatedAtTicks)");
                Volatile.Write(ref _schemaReady, 1);
            }
        }

        private static BotConversationHistoryEntity Clone(BotConversationHistoryEntity source)
        {
            return new BotConversationHistoryEntity
            {
                EntityId = source.EntityId,
                Seller = source.Seller,
                Buyer = source.Buyer,
                Question = source.Question,
                Answer = source.Answer,
                AnswerSource = source.AnswerSource,
                StatusText = source.StatusText,
                StatusKind = source.StatusKind,
                StatusDetail = source.StatusDetail,
                CanResend = source.CanResend,
                QuestionDetectedAtTicks = source.QuestionDetectedAtTicks,
                AnswerReadyAtTicks = source.AnswerReadyAtTicks,
                CreatedAtTicks = source.CreatedAtTicks,
                UpdatedAtTicks = source.UpdatedAtTicks
            };
        }
    }
}
