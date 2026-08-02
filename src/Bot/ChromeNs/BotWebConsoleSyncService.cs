using BotLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Bot
{
    public partial class App
    {
        // Instance field initializers always run before App's instance constructor. This avoids the
        // beforefieldinit problem that previously made optional UI/runtime bootstraps disappear.
        private readonly object _botWebConsoleBootstrap =
            ChromeNs.BotWebConsoleSyncService.InitializeForApp();
    }
}

namespace Bot.ChromeNs
{
    internal static class BotWebConsoleSyncService
    {
        private const string Scope = "ai-control-plane";
        private const string UrlKey = "ControlPlaneUrl";
        private const string TokenKey = "ControlPlaneClientToken";
        private const string PauseKey = "BotWebRemotePause";
        private const string MessageSyncKey = "BotWebMessageSync";
        private const string ManualReplyKey = "BotWebAllowManualReply";
        private const string IntervalKey = "BotWebSyncIntervalSeconds";
        private const string ProcessedCommandsKey = "BotWebProcessedCommandIds";

        private sealed class WebMessage
        {
            public string MessageKey;
            public string Seller;
            public string Buyer;
            public string Role;
            public string Text;
            public string MessageType;
            public DateTime OccurredAt;
        }

        private static readonly ConcurrentDictionary<string, WebMessage> PendingMessages =
            new ConcurrentDictionary<string, WebMessage>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<long, JObject> PendingCommandResults =
            new ConcurrentDictionary<long, JObject>();
        private static readonly ConcurrentDictionary<int, Func<BuyerMessageBurstLease, Task>> InstalledWrappers =
            new ConcurrentDictionary<int, Func<BuyerMessageBurstLease, Task>>();
        private static readonly ConcurrentDictionary<int, byte> HandlerReplacementWarnings =
            new ConcurrentDictionary<int, byte>();
        private static readonly object ProcessedSync = new object();
        private static readonly HashSet<long> ProcessedCommands = new HashSet<long>();
        private static readonly DateTime ProcessStartedAt = SafeProcessStart();

        private static Timer _syncTimer;
        private static Timer _patchTimer;
        private static int _initialized;
        private static int _syncing;
        private static volatile bool _remotePause;
        private static volatile bool _messageSyncEnabled = true;
        private static volatile bool _allowManualReply = true;
        private static volatile int _syncIntervalSeconds = 3;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return new object();
            LoadSettings();
            PatchExisting();
            _patchTimer = new Timer(_ => PatchExisting(), null, 350, 700);
            _syncTimer = new Timer(_ => QueueSync(), null, 2000, Math.Max(2, _syncIntervalSeconds) * 1000);
            Log.Info("Bot Web端同步已启动：使用客户端令牌同步状态、最近消息和远程设置。" );
            return new object();
        }

        internal static bool IsRemotePaused
        {
            get { return _remotePause; }
        }

        private static void LoadSettings()
        {
            _remotePause = ReadBool(PauseKey, false);
            _messageSyncEnabled = ReadBool(MessageSyncKey, true);
            _allowManualReply = ReadBool(ManualReplyKey, true);
            int parsed;
            var interval = BotLib.Db.Sqlite.PersistentParams.GetParam2Key(IntervalKey, Scope, "3");
            _syncIntervalSeconds = int.TryParse(interval, out parsed) ? Math.Max(2, Math.Min(60, parsed)) : 3;
            lock (ProcessedSync)
            {
                ProcessedCommands.Clear();
                var raw = BotLib.Db.Sqlite.PersistentParams.GetParam2Key(ProcessedCommandsKey, Scope, string.Empty);
                foreach (var part in (raw ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    long id;
                    if (long.TryParse(part, out id) && id > 0) ProcessedCommands.Add(id);
                }
            }
        }

        private static bool ReadBool(string key, bool defaultValue)
        {
            return string.Equals(
                BotLib.Db.Sqlite.PersistentParams.GetParam2Key(key, Scope, defaultValue ? "true" : "false"),
                "true",
                StringComparison.OrdinalIgnoreCase);
        }

        private static void SaveBool(string key, bool value)
        {
            BotLib.Db.Sqlite.PersistentParams.TrySaveParam2Key(key, Scope, value ? "true" : "false");
        }

        private static void PatchExisting()
        {
            try
            {
                QN[] qns;
                try { qns = QN.QNSet == null ? new QN[0] : QN.QNSet.ToArray(); }
                catch { return; }

                var coordinatorField = typeof(QN).GetField(
                    "_buyerMessageBurstCoordinator",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var handlerField = typeof(BuyerMessageBurstCoordinator).GetField(
                    "_handler",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (coordinatorField == null || handlerField == null) return;

                foreach (var qn in qns)
                {
                    if (qn == null) continue;
                    var coordinator = coordinatorField.GetValue(qn) as BuyerMessageBurstCoordinator;
                    if (coordinator == null) continue;
                    var key = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(coordinator);
                    var current = handlerField.GetValue(coordinator) as Func<BuyerMessageBurstLease, Task>;
                    if (current == null) continue;

                    Func<BuyerMessageBurstLease, Task> installed;
                    if (InstalledWrappers.TryGetValue(key, out installed))
                    {
                        // Another observer may wrap our installed handler later. Re-wrapping that
                        // outer delegate every 700 ms creates an ever-growing closure chain. The
                        // next buyer message then walks the whole chain and can terminate clr.dll
                        // with 0xc00000fd (stack overflow). One coordinator must be wrapped at most
                        // once by this service for the lifetime of the process.
                        if (!ReferenceEquals(current, installed)
                            && HandlerReplacementWarnings.TryAdd(key, 0))
                        {
                            Log.Info("Bot Web端消息处理器已被其他模块继续包装；已保持单次安装，避免递归栈溢出。" );
                        }
                        continue;
                    }

                    var next = current;
                    Func<BuyerMessageBurstLease, Task> wrapped = lease => HandleBurstAsync(next, lease);
                    handlerField.SetValue(coordinator, wrapped);
                    InstalledWrappers[key] = wrapped;
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("安装 Bot Web端消息观察器失败：" + Safe(ex.Message, 260), 10);
            }
        }

        private static async Task HandleBurstAsync(
            Func<BuyerMessageBurstLease, Task> next,
            BuyerMessageBurstLease lease)
        {
            if (next == null) return;
            var burst = lease == null ? null : lease.Burst;
            if (burst != null)
            {
                CaptureConversation(burst);
                if (_remotePause && burst.HasReplyableItem)
                {
                    Log.Info("Bot Web端已暂停智能自动回复: seller=" + Safe(burst.SellerNick, 80)
                        + ", buyer=" + Safe(burst.BuyerNick, 80));
                    return;
                }
            }
            await next(lease);
        }

        private static void CaptureConversation(BuyerMessageBurst burst)
        {
            if (!_messageSyncEnabled || burst == null) return;
            var seller = (burst.SellerNick ?? string.Empty).Trim();
            var buyer = (burst.BuyerNick ?? string.Empty).Trim();
            if (seller.Length == 0 || buyer.Length == 0) return;

            try
            {
                var turns = ConversationContextStore.GetRecentTurns(seller, buyer, string.Empty, 24);
                foreach (var turn in turns)
                {
                    if (turn == null || turn.Withdrawn || string.IsNullOrWhiteSpace(turn.Text)) continue;
                    EnqueueMessage(
                        seller,
                        buyer,
                        turn.Role,
                        turn.Text,
                        "text",
                        turn.Timestamp == DateTime.MinValue ? DateTime.Now : turn.Timestamp,
                        turn.MessageKey);
                }

                if (!string.IsNullOrWhiteSpace(burst.CombinedQuestion))
                {
                    var at = burst.Items == null
                        ? DateTime.Now
                        : burst.Items.Where(x => x != null)
                            .Select(x => x.ReceivedAt == DateTime.MinValue ? DateTime.Now : x.ReceivedAt)
                            .DefaultIfEmpty(DateTime.Now)
                            .Max();
                    EnqueueMessage(seller, buyer, "user", burst.CombinedQuestion, "text", at, string.Empty);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("收集 Bot Web端会话消息失败：" + Safe(ex.Message, 260), 10);
            }
        }

        private static void EnqueueMessage(
            string seller,
            string buyer,
            string role,
            string text,
            string messageType,
            DateTime occurredAt,
            string messageKey)
        {
            if (!_messageSyncEnabled) return;
            seller = Safe(seller, 120);
            buyer = Safe(buyer, 120);
            role = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
                ? "assistant"
                : (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase) ? "user" : "system");
            text = (text ?? string.Empty).Replace("\0", string.Empty).Trim();
            if (text.Length == 0) return;
            if (text.Length > 6000) text = text.Substring(0, 6000);
            if (occurredAt == DateTime.MinValue) occurredAt = DateTime.Now;
            if (string.IsNullOrWhiteSpace(messageKey))
            {
                messageKey = Hash(seller + "|" + buyer + "|" + role + "|"
                    + occurredAt.ToUniversalTime().Ticks + "|" + text);
            }
            var item = new WebMessage
            {
                MessageKey = Safe(messageKey, 200),
                Seller = seller,
                Buyer = buyer,
                Role = role,
                Text = text,
                MessageType = Safe(messageType, 50),
                OccurredAt = occurredAt
            };
            PendingMessages[item.MessageKey] = item;
            if (PendingMessages.Count > 1500)
            {
                foreach (var old in PendingMessages.Values.OrderBy(x => x.OccurredAt).Take(PendingMessages.Count - 1200))
                {
                    WebMessage ignored;
                    PendingMessages.TryRemove(old.MessageKey, out ignored);
                }
            }
        }

        private static void QueueSync()
        {
            if (Interlocked.Exchange(ref _syncing, 1) != 0) return;
            Task.Run(async () =>
            {
                try { await SyncOnceAsync(); }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("Bot Web端同步失败：" + Safe(ex.Message, 300), 20);
                }
                finally { Interlocked.Exchange(ref _syncing, 0); }
            });
        }

        private static async Task SyncOnceAsync()
        {
            string serverUrl;
            string token;
            ReadConnection(out serverUrl, out token);
            if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(token)) return;

            var messageSnapshot = _messageSyncEnabled
                ? PendingMessages.Values.OrderBy(x => x.OccurredAt).Take(500).ToList()
                : new List<WebMessage>();
            var resultSnapshot = PendingCommandResults.ToArray();
            var status = BuildStatus();
            var currentSettings = new JObject
            {
                ["auto_reply_enabled"] = !_remotePause,
                ["message_sync_enabled"] = _messageSyncEnabled,
                ["allow_web_manual_reply"] = _allowManualReply,
                ["sync_interval_seconds"] = _syncIntervalSeconds
            };
            var payload = new JObject
            {
                ["status"] = status,
                ["current_settings"] = currentSettings,
                ["messages"] = new JArray(messageSnapshot.Select(ToJson)),
                ["command_results"] = new JArray(resultSnapshot.Select(x => x.Value))
            };

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var handler = new HttpClientHandler
            {
                UseProxy = true,
                Proxy = WebRequest.DefaultWebProxy
            })
            using (var http = new HttpClient(handler))
            using (var request = new HttpRequestMessage(
                HttpMethod.Post,
                serverUrl.TrimEnd('/') + "/api/runtime/v1/bot-web/sync"))
            {
                http.Timeout = TimeSpan.FromSeconds(25);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                request.Headers.TryAddWithoutValidation("User-Agent", "qianniu-bot-web-sync/1.0");
                request.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
                using (var response = await http.SendAsync(request))
                {
                    var body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                        throw new Exception("HTTP " + (int)response.StatusCode + " " + Safe(body, 300));
                    var root = JObject.Parse(body);
                    ApplyDesiredSettings(root["desired_settings"] as JObject);
                    await ApplyCommandsAsync(root["commands"] as JArray);
                }
            }

            foreach (var item in messageSnapshot)
            {
                WebMessage ignored;
                PendingMessages.TryRemove(item.MessageKey, out ignored);
            }
            foreach (var pair in resultSnapshot)
            {
                JObject ignored;
                PendingCommandResults.TryRemove(pair.Key, out ignored);
            }
        }

        private static JObject BuildStatus()
        {
            QN[] qns;
            try { qns = QN.QNSet == null ? new QN[0] : QN.QNSet.ToArray(); }
            catch { qns = new QN[0]; }
            var sellers = qns
                .Where(x => x != null && x.Seller != null && !string.IsNullOrWhiteSpace(x.Seller.Nick))
                .Select(x => x.Seller.Nick.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(30)
                .ToList();
            var currentSeller = QN.CurQN == null || QN.CurQN.Seller == null
                ? string.Empty
                : (QN.CurQN.Seller.Nick ?? string.Empty).Trim();
            var windowsAuto = false;
            try { windowsAuto = Params.Robot.GetIsAutoReply(); } catch { }
            var uptime = Math.Max(0, (long)(DateTime.Now - ProcessStartedAt).TotalSeconds);
            return new JObject
            {
                ["app_version"] = GetVersion(),
                ["seller_nicks"] = new JArray(sellers),
                ["seller_count"] = sellers.Count,
                ["current_seller"] = currentSeller,
                ["windows_auto_reply_enabled"] = windowsAuto,
                ["remote_pause"] = _remotePause,
                ["effective_auto_reply_enabled"] = windowsAuto && !_remotePause,
                ["message_sync_enabled"] = _messageSyncEnabled,
                ["allow_web_manual_reply"] = _allowManualReply,
                ["pending_message_count"] = PendingMessages.Count,
                ["uptime_seconds"] = uptime,
                ["process_id"] = Process.GetCurrentProcess().Id,
                ["synced_at"] = DateTime.UtcNow.ToString("o")
            };
        }

        private static JObject ToJson(WebMessage item)
        {
            return new JObject
            {
                ["message_key"] = item.MessageKey,
                ["seller"] = item.Seller,
                ["buyer"] = item.Buyer,
                ["role"] = item.Role,
                ["text"] = item.Text,
                ["message_type"] = item.MessageType,
                ["occurred_at"] = item.OccurredAt.ToUniversalTime().ToString("o")
            };
        }

        private static void ApplyDesiredSettings(JObject desired)
        {
            if (desired == null) return;
            var pause = desired["auto_reply_enabled"] != null
                && desired["auto_reply_enabled"].Type != JTokenType.Null
                ? !desired.Value<bool>("auto_reply_enabled")
                : _remotePause;
            var messageSync = desired["message_sync_enabled"] == null
                ? _messageSyncEnabled
                : desired.Value<bool>("message_sync_enabled");
            var manual = desired["allow_web_manual_reply"] == null
                ? _allowManualReply
                : desired.Value<bool>("allow_web_manual_reply");
            var interval = desired["sync_interval_seconds"] == null
                ? _syncIntervalSeconds
                : Math.Max(2, Math.Min(60, desired.Value<int>("sync_interval_seconds")));

            if (_remotePause != pause)
            {
                _remotePause = pause;
                SaveBool(PauseKey, pause);
                Log.Info("Bot Web端远程智能回复开关已应用: enabled=" + (!pause));
            }
            if (_messageSyncEnabled != messageSync)
            {
                _messageSyncEnabled = messageSync;
                SaveBool(MessageSyncKey, messageSync);
                if (!messageSync) PendingMessages.Clear();
            }
            if (_allowManualReply != manual)
            {
                _allowManualReply = manual;
                SaveBool(ManualReplyKey, manual);
            }
            if (_syncIntervalSeconds != interval)
            {
                _syncIntervalSeconds = interval;
                BotLib.Db.Sqlite.PersistentParams.TrySaveParam2Key(IntervalKey, Scope, interval.ToString());
                if (_syncTimer != null) _syncTimer.Change(interval * 1000, interval * 1000);
            }
        }

        private static async Task ApplyCommandsAsync(JArray commands)
        {
            if (commands == null) return;
            foreach (var token in commands.Take(30))
            {
                var command = token as JObject;
                if (command == null) continue;
                var id = command.Value<long?>("id") ?? 0;
                if (id < 1) continue;
                if (WasProcessed(id))
                {
                    PendingCommandResults[id] = Result(id, true, string.Empty, new JObject { ["duplicate"] = true });
                    continue;
                }

                var type = Convert.ToString(command["type"]);
                try
                {
                    JObject result;
                    if (string.Equals(type, "send_text", StringComparison.OrdinalIgnoreCase))
                        result = await ExecuteSendTextAsync(id, command["payload"] as JObject);
                    else
                        throw new Exception("不支持的远程命令：" + Safe(type, 80));
                    MarkProcessed(id);
                    PendingCommandResults[id] = Result(id, true, string.Empty, result);
                }
                catch (Exception ex)
                {
                    MarkProcessed(id);
                    PendingCommandResults[id] = Result(id, false, Safe(ex.Message, 600), new JObject());
                }
            }
        }

        private static async Task<JObject> ExecuteSendTextAsync(long commandId, JObject payload)
        {
            if (!_allowManualReply) throw new Exception("Web端人工回复已关闭");
            if (payload == null) throw new Exception("远程回复参数为空");
            var seller = Safe(Convert.ToString(payload["seller"]), 120);
            var buyer = Safe(Convert.ToString(payload["buyer"]), 120);
            var text = Convert.ToString(payload["text"] ?? string.Empty).Trim();
            if (seller.Length == 0 || buyer.Length == 0 || text.Length == 0)
                throw new Exception("客服账号、买家或回复内容为空");
            if (text.Length > 2000) throw new Exception("回复内容超过 2000 字");

            var qn = QN.FindExistingBySellerNick(seller) ?? QN.CurQN;
            if (qn == null || qn.Seller == null
                || !string.Equals((qn.Seller.Nick ?? string.Empty).Trim(), seller, StringComparison.OrdinalIgnoreCase))
                throw new Exception("未找到对应的在线客服账号");
            var resolvedBuyer = BuyerIdentityAliasService.ResolveInternalNick(seller, buyer);
            var ok = await qn.SendTextWithRetryAsync(resolvedBuyer, text, 1);
            if (!ok)
            {
                var reason = qn.Rpa == null ? "未知发送失败" : qn.Rpa.GetSendFailureReason();
                throw new Exception(reason);
            }
            EnqueueMessage(seller, resolvedBuyer, "assistant", text, "web_sent", DateTime.Now, "command:" + commandId);
            return new JObject { ["sent"] = true };
        }

        private static JObject Result(long id, bool success, string error, JObject result)
        {
            return new JObject
            {
                ["id"] = id,
                ["success"] = success,
                ["error"] = error ?? string.Empty,
                ["result"] = result ?? new JObject()
            };
        }

        private static bool WasProcessed(long id)
        {
            lock (ProcessedSync) return ProcessedCommands.Contains(id);
        }

        private static void MarkProcessed(long id)
        {
            lock (ProcessedSync)
            {
                ProcessedCommands.Add(id);
                while (ProcessedCommands.Count > 200)
                    ProcessedCommands.Remove(ProcessedCommands.OrderBy(x => x).First());
                BotLib.Db.Sqlite.PersistentParams.TrySaveParam2Key(
                    ProcessedCommandsKey,
                    Scope,
                    string.Join(",", ProcessedCommands.OrderBy(x => x)));
            }
        }

        private static void ReadConnection(out string serverUrl, out string token)
        {
            serverUrl = BotLib.Db.Sqlite.PersistentParams.GetParam2Key(UrlKey, Scope, string.Empty);
            token = BotLib.Db.Sqlite.PersistentParams.GetParam2Key(TokenKey, Scope, string.Empty);
            serverUrl = (serverUrl ?? string.Empty).Trim().TrimEnd('/');
            if (serverUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                serverUrl = serverUrl.Substring(0, serverUrl.Length - 3).TrimEnd('/');
            token = (token ?? string.Empty).Trim();
        }

        private static string GetVersion()
        {
            try
            {
                var version = Assembly.GetExecutingAssembly().GetName().Version;
                return version == null ? string.Empty : version.ToString();
            }
            catch { return string.Empty; }
        }

        private static DateTime SafeProcessStart()
        {
            try { return Process.GetCurrentProcess().StartTime; }
            catch { return DateTime.Now; }
        }

        private static string Hash(string value)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
            }
        }

        private static string Safe(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }
}
