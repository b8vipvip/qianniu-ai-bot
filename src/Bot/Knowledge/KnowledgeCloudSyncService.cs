using Bot.ChromeNs;
using BotLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bot
{
    public partial class App
    {
        private readonly object _knowledgeCloudSyncBootstrap =
            Knowledge.KnowledgeCloudSyncService.InitializeForApp();
    }
}

namespace Bot.Knowledge
{
    internal static class KnowledgeCloudSyncService
    {
        private const string Scope = "ai-control-plane";
        private const string UrlKey = "ControlPlaneUrl";
        private const string TokenKey = "ControlPlaneClientToken";
        private const string EnabledKey = "KnowledgeCloudSyncEnabled";
        private const string RevisionKey = "KnowledgeCloudRevision";
        private const string LastHashKey = "KnowledgeCloudLastHash";

        private static readonly object UiSync = new object();
        private static readonly List<WeakReference> StatusBlocks = new List<WeakReference>();
        private static Timer _timer;
        private static int _initialized;
        private static int _syncing;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                EventManager.RegisterClassHandler(
                    typeof(KnowledgeManagerControl),
                    FrameworkElement.LoadedEvent,
                    new RoutedEventHandler(OnKnowledgeManagerLoaded),
                    true);
                _timer = new Timer(_ => QueueSync(), null, 5000, 8000);
                Log.Info("知识库云同步服务已启动：仅在知识库界面启用后上传和应用云端知识。" );
            }
            return new object();
        }

        internal static bool IsEnabled
        {
            get
            {
                return string.Equals(
                    BotLib.Db.Sqlite.PersistentParams.GetParam2Key(EnabledKey, Scope, "false"),
                    "true",
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        internal static void SetEnabled(bool enabled)
        {
            BotLib.Db.Sqlite.PersistentParams.TrySaveParam2Key(
                EnabledKey,
                Scope,
                enabled ? "true" : "false");
            UpdateStatus(enabled ? "云同步已启用，正在连接服务端..." : "云同步已关闭", enabled ? Brushes.SteelBlue : Brushes.Gray);
            QueueSync();
        }

        private static void OnKnowledgeManagerLoaded(object sender, RoutedEventArgs e)
        {
            var control = sender as KnowledgeManagerControl;
            if (control == null || control.Tag as string == "knowledge-cloud-sync-attached") return;
            var root = control.Content as DockPanel;
            if (root == null) return;
            var top = root.Children.OfType<WrapPanel>().FirstOrDefault();
            if (top == null) return;

            var check = new CheckBox
            {
                Content = "启用知识库云同步",
                IsChecked = IsEnabled,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 3, 8, 6),
                ToolTip = "启用后，Windows 与手机 Web 端使用同一份知识库；写入前会自动备份本机知识。"
            };
            var status = new TextBlock
            {
                Text = IsEnabled ? "等待云端同步" : "仅保存在本机",
                Foreground = IsEnabled ? Brushes.SteelBlue : Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 6)
            };
            check.Click += (s, args) => SetEnabled(check.IsChecked == true);
            top.Children.Add(check);
            top.Children.Add(status);
            lock (UiSync)
            {
                StatusBlocks.Add(new WeakReference(status));
                CleanupStatusBlocks();
            }
            control.Tag = "knowledge-cloud-sync-attached";
            if (IsEnabled) QueueSync();
        }

        private static void QueueSync()
        {
            if (!IsEnabled) return;
            if (Interlocked.Exchange(ref _syncing, 1) != 0) return;
            Task.Run(async () =>
            {
                try { await SyncOnceAsync(); }
                catch (Exception ex)
                {
                    UpdateStatus("云同步失败：" + Safe(ex.Message, 90), Brushes.IndianRed);
                    Log.ErrorWithMaxCount("知识库云同步失败：" + Safe(ex.Message, 300), 20);
                }
                finally { Interlocked.Exchange(ref _syncing, 0); }
            });
        }

        private static async Task SyncOnceAsync()
        {
            string serverUrl;
            string token;
            ReadConnection(out serverUrl, out token);
            if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(token))
            {
                UpdateStatus("等待配置统一 API 服务和客户端令牌", Brushes.DarkOrange);
                return;
            }

            var local = BotFeatureStore.GetKnowledgeBase() ?? new List<KnowledgeBaseEntry>();
            var localJson = JsonConvert.SerializeObject(local, Formatting.None);
            var localHash = Hash(localJson);
            var lastHash = BotLib.Db.Sqlite.PersistentParams.GetParam2Key(LastHashKey, Scope, string.Empty);
            int revision;
            if (!int.TryParse(
                BotLib.Db.Sqlite.PersistentParams.GetParam2Key(RevisionKey, Scope, "0"),
                out revision)) revision = 0;

            var payload = new JObject
            {
                ["enabled"] = true,
                ["revision"] = revision,
                ["content_hash"] = localHash
            };
            if (revision == 0 || !string.Equals(localHash, lastHash, StringComparison.OrdinalIgnoreCase))
            {
                payload["items"] = JArray.Parse(localJson);
            }

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var handler = new HttpClientHandler { UseProxy = true, Proxy = WebRequest.DefaultWebProxy })
            using (var http = new HttpClient(handler))
            using (var request = new HttpRequestMessage(
                HttpMethod.Post,
                serverUrl.TrimEnd('/') + "/api/runtime/v1/bot-web/knowledge-sync"))
            {
                http.Timeout = TimeSpan.FromSeconds(30);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                request.Headers.TryAddWithoutValidation("User-Agent", "qianniu-bot-knowledge-cloud/1.0");
                request.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
                using (var response = await http.SendAsync(request))
                {
                    var body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                        throw new Exception("HTTP " + (int)response.StatusCode + " " + Safe(body, 240));
                    var root = JObject.Parse(body);
                    var cloudRevision = root.Value<int?>("revision") ?? revision;
                    var cloudHash = Convert.ToString(root["content_hash"] ?? string.Empty);
                    var items = root["items"] as JArray;
                    if (items != null && !string.IsNullOrWhiteSpace(cloudHash)
                        && !string.Equals(cloudHash, localHash, StringComparison.OrdinalIgnoreCase))
                    {
                        var cloud = items.ToObject<List<KnowledgeBaseEntry>>() ?? new List<KnowledgeBaseEntry>();
                        var backup = Backup(localJson);
                        BotFeatureStore.SaveKnowledgeBase(cloud);
                        BotLib.Db.Sqlite.PersistentParams.TrySaveParam2Key(LastHashKey, Scope, cloudHash);
                        BotLib.Db.Sqlite.PersistentParams.TrySaveParam2Key(RevisionKey, Scope, cloudRevision.ToString());
                        UpdateStatus("已应用云端知识：" + cloud.Count + " 条", Brushes.SeaGreen);
                        Log.Info("知识库云同步已应用云端版本: revision=" + cloudRevision
                            + ", count=" + cloud.Count + ", backup=" + Path.GetFileName(backup));
                        return;
                    }

                    BotLib.Db.Sqlite.PersistentParams.TrySaveParam2Key(LastHashKey, Scope,
                        string.IsNullOrWhiteSpace(cloudHash) ? localHash : cloudHash);
                    BotLib.Db.Sqlite.PersistentParams.TrySaveParam2Key(RevisionKey, Scope, cloudRevision.ToString());
                    UpdateStatus("云同步正常 · " + local.Count + " 条 · v" + cloudRevision, Brushes.SeaGreen);
                }
            }
        }

        private static string Backup(string json)
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QianniuAiBot",
                "data",
                "backups");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory,
                "knowledge-cloud-before-apply-" + DateTime.Now.ToString("yyyyMMdd-HHmmssfff") + ".json");
            File.WriteAllText(path, json ?? "[]", new UTF8Encoding(false));
            return path;
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

        private static string Hash(string value)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
            }
        }

        private static void UpdateStatus(string text, Brush brush)
        {
            lock (UiSync)
            {
                CleanupStatusBlocks();
                foreach (var weak in StatusBlocks.ToArray())
                {
                    var block = weak.Target as TextBlock;
                    if (block == null) continue;
                    try
                    {
                        block.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            block.Text = text;
                            block.Foreground = brush;
                        }));
                    }
                    catch { }
                }
            }
        }

        private static void CleanupStatusBlocks()
        {
            StatusBlocks.RemoveAll(x => x == null || !x.IsAlive || !(x.Target is TextBlock));
        }

        private static string Safe(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }
}
