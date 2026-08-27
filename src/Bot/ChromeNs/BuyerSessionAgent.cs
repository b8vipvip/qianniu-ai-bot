using BotLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Bot.ChromeNs
{
    public enum BuyerSessionAgentState
    {
        Idle = 0,
        Observed = 1,
        Coalescing = 2,
        Processing = 3,
        Generating = 4,
        Ready = 5,
        Sending = 6,
        Waiting = 7,
        Completed = 8,
        Cancelled = 9,
        Failed = 10
    }

    public sealed class BuyerSessionAgentSnapshot
    {
        public string SellerNick { get; set; }
        public string BuyerNick { get; set; }
        public long Generation { get; set; }
        public BuyerSessionAgentState State { get; set; }
        public string LastMessageKey { get; set; }
        public long LastSortValue { get; set; }
        public DateTime LastObservedAt { get; set; }
        public DateTime StateChangedAt { get; set; }
        public string Reason { get; set; }
    }

    public sealed class BuyerSessionAgentObservation
    {
        public string SessionKey { get; set; }
        public long Generation { get; set; }
        public CancellationToken CancellationToken { get; set; }
        public bool SupersededPreviousGeneration { get; set; }
    }

    /// <summary>
    /// Per seller/buyer generation state machine. A newer accepted buyer message always
    /// supersedes the previous generation so an old draft cannot cross the send boundary.
    /// This is deliberately independent from UI state and only tracks message lifecycle.
    /// </summary>
    public sealed class BuyerSessionAgent
    {
        private const int MaxRememberedMessageKeys = 64;
        private readonly ConcurrentDictionary<string, SessionState> _sessions =
            new ConcurrentDictionary<string, SessionState>(StringComparer.Ordinal);

        public BuyerSessionAgentObservation ObserveBuyerMessage(
            string sellerNick,
            string buyerNick,
            string messageKey,
            long sortValue,
            DateTime observedAt)
        {
            var key = BuildKey(sellerNick, buyerNick);
            var state = _sessions.GetOrAdd(key, _ => new SessionState
            {
                SellerNick = Normalize(sellerNick),
                BuyerNick = Normalize(buyerNick),
                State = BuyerSessionAgentState.Idle,
                StateChangedAt = DateTime.Now
            });

            CancellationTokenSource previous = null;
            long generation;
            bool superseded;
            lock (state.SyncRoot)
            {
                state.SellerNick = Normalize(sellerNick);
                state.BuyerNick = Normalize(buyerNick);
                state.LastObservedAt = observedAt == default(DateTime) ? DateTime.Now : observedAt;
                state.LastSortValue = Math.Max(state.LastSortValue, sortValue);

                messageKey = Normalize(messageKey);
                if (!string.IsNullOrWhiteSpace(messageKey) && state.RecentMessageKeys.Contains(messageKey))
                {
                    return new BuyerSessionAgentObservation
                    {
                        SessionKey = key,
                        Generation = state.Generation,
                        CancellationToken = state.GenerationCancellation == null
                            ? CancellationToken.None
                            : state.GenerationCancellation.Token,
                        SupersededPreviousGeneration = false
                    };
                }

                if (!string.IsNullOrWhiteSpace(messageKey))
                {
                    state.RecentMessageKeys.Enqueue(messageKey);
                    state.RecentMessageKeySet.Add(messageKey);
                    while (state.RecentMessageKeys.Count > MaxRememberedMessageKeys)
                    {
                        var removed = state.RecentMessageKeys.Dequeue();
                        state.RecentMessageKeySet.Remove(removed);
                    }
                }

                previous = state.GenerationCancellation;
                superseded = state.Generation > 0 && state.State != BuyerSessionAgentState.Completed;
                state.Generation++;
                generation = state.Generation;
                state.GenerationCancellation = new CancellationTokenSource();
                state.LastMessageKey = messageKey;
                SetStateLocked(state, BuyerSessionAgentState.Observed, "buyer_message");
            }

            if (previous != null)
            {
                try { previous.Cancel(); } catch { }
                try { previous.Dispose(); } catch { }
            }

            Log.Info("BuyerSessionAgent observed: seller=" + Normalize(sellerNick)
                + ", buyer=" + Normalize(buyerNick)
                + ", generation=" + generation
                + ", sort=" + sortValue
                + ", superseded=" + superseded);

            return new BuyerSessionAgentObservation
            {
                SessionKey = key,
                Generation = generation,
                CancellationToken = GetCancellationToken(key, generation),
                SupersededPreviousGeneration = superseded
            };
        }

        public bool IsCurrent(string sellerNick, string buyerNick, long generation)
        {
            SessionState state;
            if (!_sessions.TryGetValue(BuildKey(sellerNick, buyerNick), out state)) return false;
            lock (state.SyncRoot)
            {
                return state.Generation == generation
                    && state.State != BuyerSessionAgentState.Cancelled
                    && state.State != BuyerSessionAgentState.Failed;
            }
        }

        public CancellationToken GetCancellationToken(string sellerNick, string buyerNick, long generation)
        {
            return GetCancellationToken(BuildKey(sellerNick, buyerNick), generation);
        }

        public bool TryTransition(
            string sellerNick,
            string buyerNick,
            long generation,
            BuyerSessionAgentState next,
            string reason)
        {
            SessionState state;
            if (!_sessions.TryGetValue(BuildKey(sellerNick, buyerNick), out state)) return false;
            BuyerSessionAgentState previous;
            lock (state.SyncRoot)
            {
                if (state.Generation != generation) return false;
                previous = state.State;
                if (!CanTransition(previous, next)) return false;
                SetStateLocked(state, next, reason);
            }
            Log.Info("BuyerSessionAgent transition: seller=" + Normalize(sellerNick)
                + ", buyer=" + Normalize(buyerNick)
                + ", generation=" + generation
                + ", state=" + previous + "->" + next
                + ", reason=" + Normalize(reason));
            return true;
        }

        public void Cancel(string sellerNick, string buyerNick, long generation, string reason)
        {
            SessionState state;
            if (!_sessions.TryGetValue(BuildKey(sellerNick, buyerNick), out state)) return;
            CancellationTokenSource cts = null;
            lock (state.SyncRoot)
            {
                if (state.Generation != generation) return;
                cts = state.GenerationCancellation;
                SetStateLocked(state, BuyerSessionAgentState.Cancelled, reason);
            }
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
            }
        }

        public BuyerSessionAgentSnapshot GetSnapshot(string sellerNick, string buyerNick)
        {
            SessionState state;
            if (!_sessions.TryGetValue(BuildKey(sellerNick, buyerNick), out state)) return null;
            lock (state.SyncRoot)
            {
                return new BuyerSessionAgentSnapshot
                {
                    SellerNick = state.SellerNick,
                    BuyerNick = state.BuyerNick,
                    Generation = state.Generation,
                    State = state.State,
                    LastMessageKey = state.LastMessageKey,
                    LastSortValue = state.LastSortValue,
                    LastObservedAt = state.LastObservedAt,
                    StateChangedAt = state.StateChangedAt,
                    Reason = state.Reason
                };
            }
        }

        public void Prune(TimeSpan maxIdle)
        {
            var cutoff = DateTime.Now - maxIdle;
            foreach (var pair in _sessions.ToArray())
            {
                var remove = false;
                lock (pair.Value.SyncRoot)
                {
                    remove = pair.Value.LastObservedAt != default(DateTime)
                        && pair.Value.LastObservedAt < cutoff
                        && (pair.Value.State == BuyerSessionAgentState.Completed
                            || pair.Value.State == BuyerSessionAgentState.Cancelled
                            || pair.Value.State == BuyerSessionAgentState.Failed);
                }
                SessionState removed;
                if (remove && _sessions.TryRemove(pair.Key, out removed))
                {
                    try { if (removed.GenerationCancellation != null) removed.GenerationCancellation.Dispose(); } catch { }
                }
            }
        }

        private CancellationToken GetCancellationToken(string sessionKey, long generation)
        {
            SessionState state;
            if (!_sessions.TryGetValue(sessionKey, out state)) return CancellationToken.None;
            lock (state.SyncRoot)
            {
                if (state.Generation != generation || state.GenerationCancellation == null)
                {
                    return CancellationToken.None;
                }
                return state.GenerationCancellation.Token;
            }
        }

        private static bool CanTransition(BuyerSessionAgentState current, BuyerSessionAgentState next)
        {
            if (next == BuyerSessionAgentState.Cancelled || next == BuyerSessionAgentState.Failed) return true;
            if (current == BuyerSessionAgentState.Cancelled || current == BuyerSessionAgentState.Failed) return false;
            if (next == BuyerSessionAgentState.Observed) return true;
            if (next == BuyerSessionAgentState.Completed) return true;
            return (int)next >= (int)current;
        }

        private static void SetStateLocked(SessionState state, BuyerSessionAgentState next, string reason)
        {
            state.State = next;
            state.StateChangedAt = DateTime.Now;
            state.Reason = Normalize(reason);
        }

        private static string BuildKey(string sellerNick, string buyerNick)
        {
            return Normalize(sellerNick) + "#" + Normalize(buyerNick);
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim();
        }

        private sealed class SessionState
        {
            public readonly object SyncRoot = new object();
            public string SellerNick;
            public string BuyerNick;
            public long Generation;
            public BuyerSessionAgentState State;
            public string LastMessageKey;
            public long LastSortValue;
            public DateTime LastObservedAt;
            public DateTime StateChangedAt;
            public string Reason;
            public CancellationTokenSource GenerationCancellation;
            public readonly Queue<string> RecentMessageKeys = new Queue<string>();
            public readonly HashSet<string> RecentMessageKeySet = new HashSet<string>(StringComparer.Ordinal);
        }
    }
}