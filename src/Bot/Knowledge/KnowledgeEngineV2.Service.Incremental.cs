using System;
using System.Collections.Generic;
using System.Linq;

namespace Bot.Knowledge
{
    internal static partial class KnowledgeEngineV2Service
    {
        /// <summary>
        /// Applies one enabled record to an already-built snapshot without re-reading or re-parsing
        /// the whole knowledge repository. A new immutable-style snapshot is assembled under the
        /// per-shop build lock and then swapped atomically, so concurrent buyer queries keep seeing
        /// either the previous complete snapshot or the next complete snapshot.
        /// </summary>
        internal static void ApplyRecordUpdate(string seller, KnowledgeV2Record record)
        {
            if (record == null || !record.Enabled || string.IsNullOrWhiteSpace(record.Id))
            {
                Invalidate(seller);
                return;
            }
            var shop = ResolveShop(seller);
            if (shop == null) return;

            Snapshot current;
            if (!Snapshots.TryGetValue(shop.ShopKey, out current) || current == null)
                return; // No warm snapshot yet; the next query/warm builds once from SQLite.

            lock (BuildLocks.GetOrAdd(shop.ShopKey, _ => new object()))
            {
                if (!Snapshots.TryGetValue(shop.ShopKey, out current) || current == null) return;
                var next = CloneSnapshot(current);
                var index = next.Records.FindIndex(x => x != null
                    && string.Equals(x.Id, record.Id, StringComparison.Ordinal));
                if (index >= 0)
                {
                    RemoveRecordFromIndexes(next, next.Records[index], index);
                    next.Records[index] = Clone(record);
                }
                else
                {
                    index = next.Records.Count;
                    next.Records.Add(Clone(record));
                }
                AddRecordToIndexes(next, next.Records[index], index);
                next.BuiltAt = DateTime.Now;
                Snapshots[shop.ShopKey] = next;
            }
        }

        private static Snapshot CloneSnapshot(Snapshot source)
        {
            return new Snapshot
            {
                ShopKey = source.ShopKey,
                BuiltAt = source.BuiltAt,
                Records = source.Records.Select(Clone).ToList(),
                Exact = CloneIndex(source.Exact),
                Intent = CloneIndex(source.Intent),
                Predicate = CloneIndex(source.Predicate),
                Entity = CloneIndex(source.Entity),
                Ngram = CloneIndex(source.Ngram)
            };
        }

        private static Dictionary<string, HashSet<int>> CloneIndex(Dictionary<string, HashSet<int>> source)
        {
            var result = NewIndex();
            foreach (var pair in source ?? NewIndex())
                result[pair.Key] = new HashSet<int>(pair.Value ?? new HashSet<int>());
            return result;
        }

        private static void AddRecordToIndexes(Snapshot snapshot, KnowledgeV2Record record, int id)
        {
            if (snapshot == null || record == null) return;
            Add(snapshot.Intent, record.Intent, id);
            Add(snapshot.Predicate, record.Predicate, id);
            foreach (var entity in record.Entities ?? new List<string>())
                Add(snapshot.Entity, KnowledgeEngineV2Semantics.Compact(entity), id);
            foreach (var alias in (record.Aliases ?? new List<string>()).Concat(new[] { record.Title }))
            {
                var exact = KnowledgeEngineV2Semantics.Compact(alias);
                if (exact.Length >= 2) Add(snapshot.Exact, exact, id);
                foreach (var gram in KnowledgeEngineV2Semantics.Ngrams(exact, 2).Take(24))
                    Add(snapshot.Ngram, gram, id);
            }
        }

        private static void RemoveRecordFromIndexes(Snapshot snapshot, KnowledgeV2Record record, int id)
        {
            if (snapshot == null || record == null) return;
            Remove(snapshot.Intent, record.Intent, id);
            Remove(snapshot.Predicate, record.Predicate, id);
            foreach (var entity in record.Entities ?? new List<string>())
                Remove(snapshot.Entity, KnowledgeEngineV2Semantics.Compact(entity), id);
            foreach (var alias in (record.Aliases ?? new List<string>()).Concat(new[] { record.Title }))
            {
                var exact = KnowledgeEngineV2Semantics.Compact(alias);
                if (exact.Length >= 2) Remove(snapshot.Exact, exact, id);
                foreach (var gram in KnowledgeEngineV2Semantics.Ngrams(exact, 2).Take(24))
                    Remove(snapshot.Ngram, gram, id);
            }
        }

        private static void Remove(Dictionary<string, HashSet<int>> index, string key, int id)
        {
            key = (key ?? string.Empty).Trim();
            if (key.Length == 0 || key == "general") return;
            HashSet<int> values;
            if (!index.TryGetValue(key, out values) || values == null) return;
            values.Remove(id);
            if (values.Count == 0) index.Remove(key);
        }
    }
}
