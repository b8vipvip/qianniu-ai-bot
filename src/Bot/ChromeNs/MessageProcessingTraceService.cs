using Bot.ShopScope;
using BotLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Bot
{
    public partial class App
    {
        private readonly object _messageProcessingTraceBootstrap =
            ChromeNs.MessageProcessingTraceService.InitializeForApp();
    }
}

namespace Bot.ChromeNs
{
    internal static class MessageProcessingTraceService
    {
        private sealed class TraceItem
        {
            public string EventId;
            public string TraceId;
            public string Seller;
            public string Buyer;
            public string Stage;
            public string Status;
            public string Summary;
            public string Detail;
            public long DurationMs;
            public DateTime OccurredAt;
        }

        private sealed class ActiveTrace
        {
            public string TraceId;
            public DateTime LastAt;
        }

        private sealed class ShopTraceState
        {
            public ShopContext Shop;
            public readonly ConcurrentQueue<TraceItem> Pending = new ConcurrentQueue<TraceItem>();
            public int Syncing;
        }

        private static readonly ShopScopedPathProvider Paths = new ShopScopedPathProvider();
        private static readonly ConcurrentDictionary<string, ShopTraceState> States =
            new ConcurrentDictionary<string, ShopTraceState>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, ActiveTrace> Active =
            new ConcurrentDictionary<string, ActiveTrace>(StringComparer.Ordinal);
        private static Timer _timer;
        private static int _initialized;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return new object();
            _timer = new Timer(_ => FlushDue(), null, 1800, 1800);
            Log.Info("消息处理链路追踪已启动：按 ShopKey 异步上传到控制台。" );
            return new object();
        }

        public static void RecordQuestion(string seller, string buyer, string question)
        {
            Record(seller, buyer, "message_received", "processing", "已识别买家消息", question, 0, true, false);
        }

        public static void RecordGenerationStarted(string seller, string buyer, string question, long queueMs)
        {
            Record(seller, buyer, "answer_generation_started", "processing", "开始获取答案", question, queueMs, false, false);
        }

        public static void RecordAnswerReady(
            string seller,
            string buyer,
            string question,
            string answer,
            string source,
            long responseMs)
        {
            var failed = string.IsNullOrWhiteSpace(answer)
                || answer.StartsWith("错误：", StringComparison.Ordinal);
            var detail = "来源=" + Safe(source, 120)
                + (string.IsNullOrWhiteSpace(answer) ? string.Empty : "；答案=" + Safe(answer, 1200));
            Record(
                seller,
                buyer,
                "answer_ready",
                failed ? "failed" : "ready",
                failed ? "答案生成失败" : "答案已生成",
                detail,
                responseMs,
                false,
                failed);
        }

        public static void RecordDelivery(
            string seller,
            string buyer,
            bool success,
            string detail)
        {
            Record(
                seller,
                buyer,
                success ? "delivery_confirmed" : "delivery_failed",
                success ? "success" : "failed",
                success ? "买家回复已确认发送" : "买家回复发送失败",
                detail,
                0,
                false,
                true);
        }

        public static void RecordManualIntervention(string seller, string buyer, string detail)
        {
            Record(
                seller,
                buyer,
                "manual_intervention",
                "cancelled",
                "检测到人工客服介入",
                detail,
                0,
                false,
                true);
        }

        public static void RecordFailure(string seller, string buyer, string detail)
        {
            Record(
                seller,
                buyer,
                "processing_failed",
                "failed",
                "消息处理失败",
                detail,
                0,
                false,
                true);
        }

        private static void Record(
            string seller,
            string buyer,
            string stage,
            string status,
            string summary,
            string detail,
            long durationMs,
            bool startOrRefresh,
            bool terminal)
        {
            seller = Safe(seller, 160);
            buyer = Safe(buyer, 160);
            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(buyer)) return;

            ShopContext shop;
            try
            {
                shop = ShopSettingsScope.Current ?? ShopContextLocator.ResolveRuntimeBySellerNick(seller);
            }
            catch
            {
                return;
            }
            if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)) return;

            var key = shop.ShopKey + "|" + seller + "|" + buyer;
            var now = DateTime.Now;
            ActiveTrace context;
            if (startOrRefresh)
            {
                context = Active.AddOrUpdate(
                    key,
                    _ => new ActiveTrace { TraceId = Guid.NewGuid().ToString("N"), LastAt = now },
                    (_, existing) =>
                    {
                        if (existing == null || now - existing.LastAt > TimeSpan.FromSeconds(5))
                            return new ActiveTrace { TraceId = Guid.NewGuid().ToString("N"), LastAt = now };
                        existing.LastAt = now;
                        return existing;
                    });
            }
            else if (!Active.TryGetValue(key, out context) || context == null)
            {
                context = new ActiveTrace { TraceId = Guid.NewGuid().ToString("N"), LastAt = now };
                Active[key] = context;
            }
            else
            {
                context.LastAt = now;
            }

            var state = States.GetOrAdd(shop.ShopKey, _ => new ShopTraceState { Shop = shop });
            state.Shop = shop;
            state.Pending.Enqueue(new TraceItem
            {
                EventId = Guid.NewGuid().ToString("N"),
                TraceId = context.TraceId,
                Seller = seller,
                Buyer = buyer,
                Stage = Safe(stage, 80),
                Status = Safe(status, 40),
                Summary = Safe(summary, 300),
                Detail = Safe(detail, 1800),
                DurationMs = Math.Max(0, durationMs),
                OccurredAt = now
            });

            while (state.Pending.Count > 2500)
            {
                TraceItem ignored;
                if (!state.Pending.TryDequeue(out ignored)) break;
            }

            if (terminal)
            {
                ActiveTrace ignored;
                Active.TryRemove(key, out ignored);
            }
        }

        private static void FlushDue()
        {
            CleanupActive();
            foreach (var state in States.Values)
            {
                if (state == null || state.Pending.IsEmpty) continue;
                QueueSync(state);
            }
        }

        private static void CleanupActive()
        {
            var threshold = DateTime.Now.AddMinutes(-10);
            foreach (var pair in Active)
            {
                if (pair.Value != null && pair.Value.LastAt >= threshold) continue;
                ActiveTrace ignored;
                Active.TryRemove(pair.Key, out ignored);
            }
        }

        private static void QueueSync(ShopTraceState state)
        {
            if (Interlocked.Exchange(ref state.Syncing, 1) != 0) return;
            Task.Run(async () =>
            {
                try { await SyncOnceAsync(state); }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("上传消息处理链路追踪失败：" + Safe(ex.Message, 300), 10);
                }
                finally { Interlocked.Exchange(ref state.Syncing, 0); }
            });
        }

        private static async Task SyncOnceAsync(ShopTraceState state)
        {
            var batch = new List<TraceItem>();
            TraceItem item;
            while (batch.Count < 250 && state.Pending.TryDequeue(out item))
            {
                if (item != null) batch.Add(item);
            }
            if (batch.Count == 0) return;

            try
            {
                var connection = new ShopControlPlaneConnectionStore(state.Shop, Paths);
                var serverUrl = connection.GetServerUrl();
                string token;
                string error;
                if (string.IsNullOrWhiteSpace(serverUrl)
                    || !connection.TryGetToken(out token, out error)
                    || string.IsNullOrWhiteSpace(token))
                {
                    Requeue(state, batch);
                    return;
                }

                var payload = new JObject
                {
                    ["events"] = new JArray(batch.Select(ToJson))
                };

                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                using (var handler = new HttpClientHandler { UseProxy = true, Proxy = WebRequest.DefaultWebProxy })
                using (var http = new HttpClient(handler))
                using (var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    serverUrl.TrimEnd('/') + "/api/runtime/v1/message-processing-traces/batch"))
                {
                    http.Timeout = TimeSpan.FromSeconds(15);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    request.Headers.TryAddWithoutValidation("Accept", "application/json");
                    request.Headers.TryAddWithoutValidation("User-Agent", "qianniu-bot-processing-trace/1.0");
                    request.Headers.TryAddWithoutValidation("X-Shop-Key", state.Shop.ShopKey);
                    request.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
                    using (var response = await http.SendAsync(request))
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        if (!response.IsSuccessStatusCode)
                            throw new InvalidOperationException("HTTP " + (int)response.StatusCode + " " + Safe(body, 500));
                    }
                }
            }
            catch
            {
                Requeue(state, batch);
                throw;
            }
        }

        private static void Requeue(ShopTraceState state, IList<TraceItem> batch)
        {
            foreach (var item in batch ?? new List<TraceItem>())
                if (item != null) state.Pending.Enqueue(item);
        }

        private static JObject ToJson(TraceItem item)
        {
            return new JObject
            {
                ["event_id"] = item.EventId,
                ["trace_id"] = item.TraceId,
                ["seller"] = item.Seller,
                ["buyer"] = item.Buyer,
                ["stage"] = item.Stage,
                ["status"] = item.Status,
                ["summary"] = item.Summary,
                ["detail"] = item.Detail,
                ["duration_ms"] = item.DurationMs,
                ["occurred_at"] = item.OccurredAt.ToUniversalTime().ToString("o")
            };
        }

        private static string Safe(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }
}
