using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Bot.ChromeNs
{
    public class ApiUsageSnapshot
    {
        public string EndpointId { get; set; }
        public string EndpointName { get; set; }
        public long TotalCalls { get; set; }
        public long TodayCalls { get; set; }
        public long TotalInputTokens { get; set; }
        public long TodayInputTokens { get; set; }
        public long TotalOutputTokens { get; set; }
        public long TodayOutputTokens { get; set; }
        public long TotalTokens { get; set; }
        public long TodayTokens { get; set; }
        public long FailedCalls { get; set; }
        public long TodayFailedCalls { get; set; }
        public long AvgLatencyMs { get; set; }
        public string LastStatus { get; set; }
    }

    public class RuntimeStatsSnapshot
    {
        public long TotalReceivedMessages { get; set; }
        public long TodayReceivedMessages { get; set; }
        public long TotalReceptionCount { get; set; }
        public long TodayReceptionCount { get; set; }
        public long TotalAutoReplies { get; set; }
        public long TodayAutoReplies { get; set; }
        public long TotalManualReviewAnswers { get; set; }
        public long TodayManualReviewAnswers { get; set; }
        public long SendFailedCount { get; set; }
        public long TodaySendFailedCount { get; set; }
        public long TotalAiCalls { get; set; }
        public long TodayAiCalls { get; set; }
        public long TotalAiFailedCalls { get; set; }
        public long TodayAiFailedCalls { get; set; }
        public long TotalTokens { get; set; }
        public long TodayTokens { get; set; }
        public long TotalInputTokens { get; set; }
        public long TodayInputTokens { get; set; }
        public long TotalOutputTokens { get; set; }
        public long TodayOutputTokens { get; set; }
        public long AvgLatencyMs { get; set; }
        public string LastError { get; set; }
        public List<ApiUsageSnapshot> ApiUsages { get; set; }
    }

    public static class BotRuntimeStats
    {
        private class ApiUsageCounter
        {
            public string EndpointId;
            public string EndpointName;
            public long TotalCalls;
            public long TodayCalls;
            public long TotalInputTokens;
            public long TodayInputTokens;
            public long TotalOutputTokens;
            public long TodayOutputTokens;
            public long FailedCalls;
            public long TodayFailedCalls;
            public long LatencyTotalMs;
            public long LatencyCount;
            public string LastStatus;
        }

        private static readonly object SyncObj = new object();
        private static readonly Dictionary<string, ApiUsageCounter> ApiCounters = new Dictionary<string, ApiUsageCounter>();
        private static DateTime Today = DateTime.Today;
        private static long totalReceivedMessages;
        private static long todayReceivedMessages;
        private static long totalReceptionCount;
        private static long todayReceptionCount;
        private static long totalAutoReplies;
        private static long todayAutoReplies;
        private static long totalManualReviewAnswers;
        private static long todayManualReviewAnswers;
        private static long sendFailedCount;
        private static long todaySendFailedCount;
        private static long totalAiCalls;
        private static long todayAiCalls;
        private static long totalAiFailedCalls;
        private static long todayAiFailedCalls;
        private static long totalInputTokens;
        private static long todayInputTokens;
        private static long totalOutputTokens;
        private static long todayOutputTokens;
        private static long latencyTotalMs;
        private static long latencyCount;
        private static string lastError = string.Empty;

        private static void EnsureToday()
        {
            if (Today == DateTime.Today) return;
            Today = DateTime.Today;
            todayReceivedMessages = 0;
            todayReceptionCount = 0;
            todayAutoReplies = 0;
            todayManualReviewAnswers = 0;
            todaySendFailedCount = 0;
            todayAiCalls = 0;
            todayAiFailedCalls = 0;
            todayInputTokens = 0;
            todayOutputTokens = 0;
            foreach (var item in ApiCounters.Values)
            {
                item.TodayCalls = 0;
                item.TodayInputTokens = 0;
                item.TodayOutputTokens = 0;
                item.TodayFailedCalls = 0;
            }
        }

        public static void RecordReceivedMessage()
        {
            lock (SyncObj)
            {
                EnsureToday();
                totalReceivedMessages++;
                todayReceivedMessages++;
            }
        }

        public static void RecordReception()
        {
            lock (SyncObj)
            {
                EnsureToday();
                totalReceptionCount++;
                todayReceptionCount++;
            }
        }

        public static void RecordDisplayedAnswer(bool autoReply)
        {
            lock (SyncObj)
            {
                EnsureToday();
                if (autoReply)
                {
                    totalAutoReplies++;
                    todayAutoReplies++;
                }
                else
                {
                    totalManualReviewAnswers++;
                    todayManualReviewAnswers++;
                }
            }
        }

        public static void RecordSendResult(bool success)
        {
            if (success) return;
            lock (SyncObj)
            {
                EnsureToday();
                sendFailedCount++;
                todaySendFailedCount++;
            }
        }

        public static void RecordAiCall(AiEndpointConfig endpoint, int inputTokens, int outputTokens, bool success, long latencyMs, string status)
        {
            lock (SyncObj)
            {
                EnsureToday();
                var endpointId = endpoint == null ? "default" : (endpoint.Id ?? "default");
                var endpointName = endpoint == null ? "默认接口" : (endpoint.Name ?? "默认接口");
                ApiUsageCounter counter;
                if (!ApiCounters.TryGetValue(endpointId, out counter))
                {
                    counter = new ApiUsageCounter { EndpointId = endpointId, EndpointName = endpointName, LastStatus = string.Empty };
                    ApiCounters[endpointId] = counter;
                }
                counter.EndpointName = endpointName;
                counter.TotalCalls++;
                counter.TodayCalls++;
                counter.TotalInputTokens += inputTokens;
                counter.TodayInputTokens += inputTokens;
                counter.TotalOutputTokens += outputTokens;
                counter.TodayOutputTokens += outputTokens;
                counter.LatencyTotalMs += Math.Max(0, latencyMs);
                counter.LatencyCount++;
                counter.LastStatus = status ?? string.Empty;
                if (!success)
                {
                    counter.FailedCalls++;
                    counter.TodayFailedCalls++;
                    lastError = status ?? string.Empty;
                }

                totalAiCalls++;
                todayAiCalls++;
                totalInputTokens += inputTokens;
                todayInputTokens += inputTokens;
                totalOutputTokens += outputTokens;
                todayOutputTokens += outputTokens;
                latencyTotalMs += Math.Max(0, latencyMs);
                latencyCount++;
                if (!success)
                {
                    totalAiFailedCalls++;
                    todayAiFailedCalls++;
                }
            }
        }

        public static RuntimeStatsSnapshot GetSnapshot()
        {
            lock (SyncObj)
            {
                EnsureToday();
                var apiUsages = ApiCounters.Values
                    .OrderByDescending(a => a.TodayCalls)
                    .ThenBy(a => a.EndpointName)
                    .Select(a => new ApiUsageSnapshot
                    {
                        EndpointId = a.EndpointId,
                        EndpointName = a.EndpointName,
                        TotalCalls = a.TotalCalls,
                        TodayCalls = a.TodayCalls,
                        TotalInputTokens = a.TotalInputTokens,
                        TodayInputTokens = a.TodayInputTokens,
                        TotalOutputTokens = a.TotalOutputTokens,
                        TodayOutputTokens = a.TodayOutputTokens,
                        TotalTokens = a.TotalInputTokens + a.TotalOutputTokens,
                        TodayTokens = a.TodayInputTokens + a.TodayOutputTokens,
                        FailedCalls = a.FailedCalls,
                        TodayFailedCalls = a.TodayFailedCalls,
                        AvgLatencyMs = a.LatencyCount <= 0 ? 0 : a.LatencyTotalMs / a.LatencyCount,
                        LastStatus = a.LastStatus ?? string.Empty
                    }).ToList();

                return new RuntimeStatsSnapshot
                {
                    TotalReceivedMessages = totalReceivedMessages,
                    TodayReceivedMessages = todayReceivedMessages,
                    TotalReceptionCount = totalReceptionCount,
                    TodayReceptionCount = todayReceptionCount,
                    TotalAutoReplies = totalAutoReplies,
                    TodayAutoReplies = todayAutoReplies,
                    TotalManualReviewAnswers = totalManualReviewAnswers,
                    TodayManualReviewAnswers = todayManualReviewAnswers,
                    SendFailedCount = sendFailedCount,
                    TodaySendFailedCount = todaySendFailedCount,
                    TotalAiCalls = totalAiCalls,
                    TodayAiCalls = todayAiCalls,
                    TotalAiFailedCalls = totalAiFailedCalls,
                    TodayAiFailedCalls = todayAiFailedCalls,
                    TotalInputTokens = totalInputTokens,
                    TodayInputTokens = todayInputTokens,
                    TotalOutputTokens = totalOutputTokens,
                    TodayOutputTokens = todayOutputTokens,
                    TotalTokens = totalInputTokens + totalOutputTokens,
                    TodayTokens = todayInputTokens + todayOutputTokens,
                    AvgLatencyMs = latencyCount <= 0 ? 0 : latencyTotalMs / latencyCount,
                    LastError = lastError ?? string.Empty,
                    ApiUsages = apiUsages
                };
            }
        }
    }
}
