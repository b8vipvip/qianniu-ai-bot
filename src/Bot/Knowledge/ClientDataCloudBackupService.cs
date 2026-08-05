using BotLib;
using BotLib.Db.Sqlite;
using BotLib.Extensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Bot
{
    public partial class App
    {
        private readonly object _clientDataCloudBackupBootstrap =
            Knowledge.ClientDataCloudBackupService.InitializeForApp();
    }
}

namespace Bot.Knowledge
{
    internal static class ClientDataCloudBackupService
    {
        private const string Scope = "ai-control-plane";
        private const string UrlKey = "ControlPlaneUrl";
        private const string TokenKey = "ControlPlaneClientToken";
        private const string Magic = "QABK1";
        private const long MaxDataBytes = 48L * 1024 * 1024;
        private const long MaxSingleFileBytes = 32L * 1024 * 1024;

        private static readonly ConditionalWeakTable<KnowledgeManagerControl, object> Attached =
            new ConditionalWeakTable<KnowledgeManagerControl, object>();
        private static int _initialized;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                EventManager.RegisterClassHandler(
                    typeof(KnowledgeManagerControl),
                    FrameworkElement.LoadedEvent,
                    new RoutedEventHandler(OnKnowledgeManagerLoaded),
                    true);
            }
            return new object();
        }

        private static void OnKnowledgeManagerLoaded(object sender, RoutedEventArgs e)
        {
            var control = sender as KnowledgeManagerControl;
            if (control == null) return;
            object marker;
            if (Attached.TryGetValue(control, out marker)) return;
            try { Attached.Add(control, new object()); } catch { return; }

            control.Dispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                new Action(() => AttachButton(control)));
        }

        private static void AttachButton(KnowledgeManagerControl control)
        {
            var root = control.Content as DockPanel;
            var top = root == null ? null : root.Children.OfType<WrapPanel>().FirstOrDefault();
            if (top == null) return;
            if (top.Children.OfType<Button>().Any(x => Convert.ToString(x.Tag) == "client-data-cloud-backup"))
                return;

            var button = new Button
            {
                Content = "云备份/换机",
                Tag = "client-data-cloud-backup",
                Width = 104,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 6),
                ToolTip = "使用当前 Bot 客户端令牌加密上传业务数据；新电脑配置相同令牌后可一键恢复。"
            };
            button.Click += (s, e) =>
            {
                var window = new ClientDataBackupWindow
                {
                    Owner = Window.GetWindow(control)
                };
                window.ShowDialog();
            };
            top.Children.Add(button);
        }

        internal static async Task<JObject> GetStatusAsync()
        {
            string serverUrl;
            string token;
            ReadConnection(out serverUrl, out token);
            EnsureConnection(serverUrl, token);

            using (var http = CreateHttp(token))
            using (var response = await http.GetAsync(
                serverUrl.TrimEnd('/') + "/api/runtime/v1/client-data-backup/status"))
            {
                var body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                    throw new Exception("HTTP " + (int)response.StatusCode + " " + Safe(body, 300));
                return JObject.Parse(body);
            }
        }

        internal static async Task<JObject> UploadAsync(Action<string> status)
        {
            string serverUrl;
            string token;
            ReadConnection(out serverUrl, out token);
            EnsureConnection(serverUrl, token);

            status("正在整理本机业务数据...");
            BackupBuildResult build = null;
            try
            {
                build = BuildEncryptedBackup(token);
                status("正在上传加密备份 " + FormatBytes(build.EncryptedBytes.Length) + "...");

                using (var http = CreateHttp(token))
                using (var request = new HttpRequestMessage(
                    HttpMethod.Put,
                    serverUrl.TrimEnd('/') + "/api/runtime/v1/client-data-backup"))
                {
                    request.Content = new ByteArrayContent(build.EncryptedBytes);
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    request.Headers.TryAddWithoutValidation("X-Backup-Sha256", Sha256(build.EncryptedBytes));
                    request.Headers.TryAddWithoutValidation("X-Backup-Created-At", build.CreatedAt);
                    request.Headers.TryAddWithoutValidation("X-Backup-Device", Safe(Environment.MachineName, 100));
                    request.Headers.TryAddWithoutValidation("X-Backup-App-Version", AppVersion());
                    request.Headers.TryAddWithoutValidation("X-Backup-File-Count", build.FileCount.ToString());
                    request.Headers.TryAddWithoutValidation("X-Backup-Data-Bytes", build.DataBytes.ToString());

                    using (var response = await http.SendAsync(request))
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        if (!response.IsSuccessStatusCode)
                            throw new Exception("HTTP " + (int)response.StatusCode + " " + Safe(body, 300));
                        Log.Info("整机业务数据云备份已上传: files=" + build.FileCount
                            + ", dataBytes=" + build.DataBytes
                            + ", encryptedBytes=" + build.EncryptedBytes.Length);
                        return JObject.Parse(body);
                    }
                }
            }
            finally
            {
                if (build != null) build.Dispose();
            }
        }

        internal static async Task<RestoreResult> DownloadAndRestoreAsync(Action<string> status)
        {
            string serverUrl;
            string token;
            ReadConnection(out serverUrl, out token);
            EnsureConnection(serverUrl, token);

            status("正在下载云端加密备份...");
            byte[] encrypted;
            using (var http = CreateHttp(token))
            using (var response = await http.GetAsync(
                serverUrl.TrimEnd('/') + "/api/runtime/v1/client-data-backup"))
            {
                var body = await response.Content.ReadAsByteArrayAsync();
                if (!response.IsSuccessStatusCode)
                    throw new Exception("HTTP " + (int)response.StatusCode + " " + Safe(Encoding.UTF8.GetString(body), 300));
                encrypted = body;
            }

            status("正在使用当前 Bot 令牌解密并校验...");
            var zipBytes = Decrypt(encrypted, token);
            status("正在备份当前电脑数据...");
            var rollback = CreateRollbackBackup();
            status("正在恢复业务数据...");
            var restored = RestoreZip(zipBytes);
            restored.RollbackPath = rollback;
            Log.Info("云端业务数据已恢复: params=" + restored.ParamCount
                + ", files=" + restored.FileCount
                + ", rollback=" + rollback);
            return restored;
        }

        internal static string DataFolder
        {
            get { return PathEx.DataDir; }
        }

        private static BackupBuildResult BuildEncryptedBackup(string token)
        {
            var createdAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var skipped = new List<string>();
            var parameters = ExportParameters();
            var tempZip = Path.Combine(Path.GetTempPath(), "qianniu-client-backup-" + Guid.NewGuid().ToString("N") + ".zip");
            var count = 0;
            long totalBytes = 0;

            using (var file = new FileStream(tempZip, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create, true, Encoding.UTF8))
            {
                WriteTextEntry(zip, "params.json", JsonConvert.SerializeObject(parameters, Formatting.Indented));

                foreach (var source in EnumerateDataFiles())
                {
                    var relative = RelativeDataPath(source);
                    if (ShouldExclude(relative)) continue;
                    long length;
                    try { length = new FileInfo(source).Length; }
                    catch
                    {
                        skipped.Add(relative + "（无法读取大小）");
                        continue;
                    }
                    if (length > MaxSingleFileBytes)
                    {
                        skipped.Add(relative + "（单文件超过32MB）");
                        continue;
                    }
                    if (totalBytes + length > MaxDataBytes)
                    {
                        skipped.Add(relative + "（总数据超过48MB）");
                        continue;
                    }
                    try
                    {
                        var entry = zip.CreateEntry("files/" + relative.Replace('\\', '/'), CompressionLevel.Optimal);
                        using (var input = new FileStream(
                            source,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete))
                        using (var output = entry.Open())
                        {
                            input.CopyTo(output);
                        }
                        totalBytes += length;
                        count++;
                    }
                    catch (Exception ex)
                    {
                        skipped.Add(relative + "（" + Safe(ex.Message, 80) + "）");
                    }
                }

                var manifest = new JObject
                {
                    ["schema"] = "qianniu-ai-bot.client-data-backup",
                    ["version"] = 1,
                    ["createdAt"] = createdAt,
                    ["deviceName"] = Environment.MachineName,
                    ["appVersion"] = AppVersion(),
                    ["fileCount"] = count,
                    ["dataBytes"] = totalBytes,
                    ["parameterCount"] = parameters.Count,
                    ["excluded"] = new JArray(
                        "运行日志和崩溃文件",
                        "backups/tmp/cache/update 等临时目录",
                        "params.db（改为结构化参数导出）",
                        "统一 API 地址和 Bot 客户端令牌",
                        "云同步修订号、哈希和设备迁移状态"),
                    ["skippedFiles"] = JArray.FromObject(skipped.Take(200).ToList())
                };
                WriteTextEntry(zip, "manifest.json", manifest.ToString(Formatting.Indented));
            }

            var plain = File.ReadAllBytes(tempZip);
            var encrypted = Encrypt(plain, token);
            return new BackupBuildResult
            {
                TempPath = tempZip,
                EncryptedBytes = encrypted,
                FileCount = count,
                DataBytes = totalBytes,
                CreatedAt = createdAt
            };
        }

        private static Dictionary<string, string> ExportParameters()
        {
            var field = typeof(PersistentParams).GetField(
                "_cache",
                BindingFlags.Static | BindingFlags.NonPublic);
            var dictionary = field == null ? null : field.GetValue(null) as IEnumerable;
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (dictionary == null) return result;

            foreach (var raw in dictionary)
            {
                var type = raw.GetType();
                var key = Convert.ToString(type.GetProperty("Key").GetValue(raw, null));
                var value = Convert.ToString(type.GetProperty("Value").GetValue(raw, null));
                if (string.IsNullOrWhiteSpace(key) || IsProtectedParameter(key)) continue;
                result[key] = value ?? string.Empty;
            }
            return result;
        }

        private static bool IsProtectedParameter(string key)
        {
            key = key ?? string.Empty;
            var exact = new[]
            {
                UrlKey + "#-#" + Scope,
                TokenKey + "#-#" + Scope,
                "KnowledgeCloudRevision#-#" + Scope,
                "KnowledgeCloudLastHash#-#" + Scope,
                "ClientDataBackupRevision#-#" + Scope,
                "ClientDataBackupLastHash#-#" + Scope,
                "HandoffRemoteRulesJson#-#" + Scope
            };
            if (exact.Any(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase))) return true;
            return key.IndexOf("cloud-backup-session", StringComparison.OrdinalIgnoreCase) >= 0
                || key.IndexOf("machine-id", StringComparison.OrdinalIgnoreCase) >= 0
                || key.IndexOf("device-id", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static IEnumerable<string> EnumerateDataFiles()
        {
            if (!Directory.Exists(PathEx.DataDir)) return Enumerable.Empty<string>();
            try
            {
                return Directory.EnumerateFiles(PathEx.DataDir, "*", SearchOption.AllDirectories).ToList();
            }
            catch
            {
                return Enumerable.Empty<string>();
            }
        }

        private static string RelativeDataPath(string fullPath)
        {
            var root = Path.GetFullPath(PathEx.DataDir);
            var full = Path.GetFullPath(fullPath);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(root.Length).TrimStart('\\', '/')
                : Path.GetFileName(full);
        }

        private static bool ShouldExclude(string relative)
        {
            relative = (relative ?? string.Empty).Replace('/', '\\').TrimStart('\\');
            var parts = relative.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            var excludedFolders = new[]
            {
                "log", "logs", "backup", "backups", "tmp", "temp",
                "cache", "caches", "crash", "crashes", "update", "updates"
            };
            if (parts.Any(x => excludedFolders.Contains(x, StringComparer.OrdinalIgnoreCase))) return true;

            var name = Path.GetFileName(relative);
            var extension = Path.GetExtension(relative);
            if (string.Equals(name, "params.db", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("params.db-", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(extension, ".log", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".tmp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".bak", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".dmp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".trace", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".etl", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".qab", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string CreateRollbackBackup()
        {
            var directory = Path.Combine(PathEx.UserDataRoot, "restore-backups");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory,
                "before-cloud-restore-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip");
            var parameters = ExportParameters();
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create, true, Encoding.UTF8))
            {
                WriteTextEntry(zip, "params.json", JsonConvert.SerializeObject(parameters, Formatting.Indented));
                foreach (var source in EnumerateDataFiles())
                {
                    var relative = RelativeDataPath(source);
                    if (ShouldExclude(relative)) continue;
                    try
                    {
                        var entry = zip.CreateEntry("files/" + relative.Replace('\\', '/'), CompressionLevel.Fastest);
                        using (var input = new FileStream(
                            source,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete))
                        using (var output = entry.Open())
                        {
                            input.CopyTo(output);
                        }
                    }
                    catch { }
                }
            }
            return path;
        }

        private static RestoreResult RestoreZip(byte[] zipBytes)
        {
            var result = new RestoreResult();
            using (var stream = new MemoryStream(zipBytes, false))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, false, Encoding.UTF8))
            {
                var manifestEntry = zip.GetEntry("manifest.json");
                if (manifestEntry == null) throw new Exception("备份缺少 manifest.json");
                var manifest = JObject.Parse(ReadTextEntry(manifestEntry));
                if (!string.Equals(
                    Convert.ToString(manifest["schema"]),
                    "qianniu-ai-bot.client-data-backup",
                    StringComparison.Ordinal))
                    throw new Exception("云端文件不是千牛 Bot 数据备份");

                var paramsEntry = zip.GetEntry("params.json");
                if (paramsEntry == null) throw new Exception("备份缺少 params.json");
                var parameters = JsonConvert.DeserializeObject<Dictionary<string, string>>(
                    ReadTextEntry(paramsEntry)) ?? new Dictionary<string, string>();
                foreach (var item in parameters)
                {
                    if (IsProtectedParameter(item.Key)) continue;
                    PersistentParams.TrySaveParam(item.Key, item.Value ?? string.Empty);
                    result.ParamCount++;
                }

                var root = Path.GetFullPath(PathEx.DataDir);
                foreach (var entry in zip.Entries.Where(x => x.FullName.StartsWith("files/", StringComparison.Ordinal)))
                {
                    var relative = entry.FullName.Substring("files/".Length).Replace('/', '\\');
                    if (string.IsNullOrWhiteSpace(relative) || ShouldExclude(relative)) continue;
                    var target = Path.GetFullPath(Path.Combine(root, relative));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new Exception("备份中包含不安全路径：" + relative);
                    var directory = Path.GetDirectoryName(target);
                    if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                    using (var input = entry.Open())
                    using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        input.CopyTo(output);
                    }
                    result.FileCount++;
                }
                result.CreatedAt = Convert.ToString(manifest["createdAt"] ?? string.Empty);
                result.SourceDevice = Convert.ToString(manifest["deviceName"] ?? string.Empty);
            }
            return result;
        }

        private static byte[] Encrypt(byte[] plain, string token)
        {
            var salt = RandomBytes(16);
            var iv = RandomBytes(16);
            byte[] encryptionKey;
            byte[] macKey;
            DeriveKeys(token, salt, out encryptionKey, out macKey);

            byte[] cipher;
            using (var aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = encryptionKey;
                aes.IV = iv;
                using (var encryptor = aes.CreateEncryptor())
                {
                    cipher = encryptor.TransformFinalBlock(plain, 0, plain.Length);
                }
            }

            var prefix = Encoding.ASCII.GetBytes(Magic)
                .Concat(salt)
                .Concat(iv)
                .Concat(cipher)
                .ToArray();
            byte[] mac;
            using (var hmac = new HMACSHA256(macKey)) mac = hmac.ComputeHash(prefix);
            return prefix.Concat(mac).ToArray();
        }

        private static byte[] Decrypt(byte[] encrypted, string token)
        {
            if (encrypted == null || encrypted.Length < Magic.Length + 16 + 16 + 32 + 1)
                throw new Exception("云备份文件不完整");
            var magic = Encoding.ASCII.GetString(encrypted, 0, Magic.Length);
            if (!string.Equals(magic, Magic, StringComparison.Ordinal))
                throw new Exception("云备份文件版本不受支持");

            var salt = encrypted.Skip(Magic.Length).Take(16).ToArray();
            var iv = encrypted.Skip(Magic.Length + 16).Take(16).ToArray();
            var cipherLength = encrypted.Length - Magic.Length - 16 - 16 - 32;
            var cipher = encrypted.Skip(Magic.Length + 32).Take(cipherLength).ToArray();
            var suppliedMac = encrypted.Skip(encrypted.Length - 32).Take(32).ToArray();
            byte[] encryptionKey;
            byte[] macKey;
            DeriveKeys(token, salt, out encryptionKey, out macKey);

            var signed = encrypted.Take(encrypted.Length - 32).ToArray();
            byte[] expectedMac;
            using (var hmac = new HMACSHA256(macKey)) expectedMac = hmac.ComputeHash(signed);
            if (!ConstantTimeEquals(suppliedMac, expectedMac))
                throw new Exception("解密失败：Bot 令牌不一致或云备份已损坏");

            try
            {
                using (var aes = Aes.Create())
                {
                    aes.KeySize = 256;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    aes.Key = encryptionKey;
                    aes.IV = iv;
                    using (var decryptor = aes.CreateDecryptor())
                    {
                        return decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
                    }
                }
            }
            catch (CryptographicException)
            {
                throw new Exception("解密失败：Bot 令牌不一致或云备份已损坏");
            }
        }

        private static void DeriveKeys(string token, byte[] salt, out byte[] encryptionKey, out byte[] macKey)
        {
            using (var derive = new Rfc2898DeriveBytes(token ?? string.Empty, salt, 120000))
            {
                var material = derive.GetBytes(64);
                encryptionKey = material.Take(32).ToArray();
                macKey = material.Skip(32).Take(32).ToArray();
            }
        }

        private static bool ConstantTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            var different = 0;
            for (var i = 0; i < left.Length; i++) different |= left[i] ^ right[i];
            return different == 0;
        }

        private static byte[] RandomBytes(int count)
        {
            var bytes = new byte[count];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return bytes;
        }

        private static void WriteTextEntry(ZipArchive zip, string name, string text)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                writer.Write(text ?? string.Empty);
        }

        private static string ReadTextEntry(ZipArchiveEntry entry)
        {
            using (var reader = new StreamReader(entry.Open(), Encoding.UTF8, true))
                return reader.ReadToEnd();
        }

        private static HttpClient CreateHttp(string token)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var handler = new HttpClientHandler
            {
                UseProxy = true,
                Proxy = WebRequest.DefaultWebProxy
            };
            var http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMinutes(5)
            };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "qianniu-bot-client-data-backup/1.0");
            return http;
        }

        private static void ReadConnection(out string serverUrl, out string token)
        {
            serverUrl = PersistentParams.GetParam2Key(UrlKey, Scope, string.Empty);
            token = PersistentParams.GetParam2Key(TokenKey, Scope, string.Empty);
            serverUrl = (serverUrl ?? string.Empty).Trim().TrimEnd('/');
            if (serverUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                serverUrl = serverUrl.Substring(0, serverUrl.Length - 3).TrimEnd('/');
            token = (token ?? string.Empty).Trim();
        }

        private static void EnsureConnection(string serverUrl, string token)
        {
            if (string.IsNullOrWhiteSpace(serverUrl))
                throw new Exception("请先配置统一 API 服务地址。");
            if (string.IsNullOrWhiteSpace(token))
                throw new Exception("请先配置 Bot 客户端令牌；新电脑必须使用与旧电脑相同的令牌。");
        }

        internal static void RestartApplication()
        {
            var exe = Process.GetCurrentProcess().MainModule.FileName;
            var arguments = "/c ping 127.0.0.1 -n 3 >nul & start \"\" \"" + exe.Replace("\"", "\"\"") + "\"";
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            Application.Current.Shutdown();
        }

        private static string AppVersion()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version == null ? "unknown" : version.ToString();
        }

        private static string Sha256(byte[] value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(value ?? new byte[0]))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
        }

        internal static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024d).ToString("0.0") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024d / 1024d).ToString("0.0") + " MB";
            return (bytes / 1024d / 1024d / 1024d).ToString("0.00") + " GB";
        }

        private static string Safe(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }

        private sealed class BackupBuildResult : IDisposable
        {
            public string TempPath { get; set; }
            public byte[] EncryptedBytes { get; set; }
            public int FileCount { get; set; }
            public long DataBytes { get; set; }
            public string CreatedAt { get; set; }

            public void Dispose()
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(TempPath) && File.Exists(TempPath)) File.Delete(TempPath);
                }
                catch { }
            }
        }
    }

    internal sealed class RestoreResult
    {
        public int ParamCount { get; set; }
        public int FileCount { get; set; }
        public string CreatedAt { get; set; }
        public string SourceDevice { get; set; }
        public string RollbackPath { get; set; }
    }

    internal sealed class ClientDataBackupWindow : Window
    {
        private readonly TextBlock _cloudStatus;
        private readonly TextBlock _operationStatus;
        private readonly Button _upload;
        private readonly Button _restore;
        private readonly Button _refresh;
        private bool _busy;

        public ClientDataBackupWindow()
        {
            Title = "云备份与更换电脑";
            Width = 680;
            Height = 510;
            MinWidth = 620;
            MinHeight = 460;
            ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var root = new DockPanel { Margin = new Thickness(20) };
            Content = root;

            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0)
            };
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);
            AddButton(footer, "打开本机数据目录", 132, (s, e) =>
            {
                Directory.CreateDirectory(ClientDataCloudBackupService.DataFolder);
                PathEx.OpenFolder(ClientDataCloudBackupService.DataFolder);
            });
            AddButton(footer, "关闭", 82, (s, e) => Close());

            var content = new StackPanel();
            root.Children.Add(content);
            content.Children.Add(new TextBlock
            {
                Text = "云备份与更换电脑",
                FontSize = 21,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 10)
            });
            content.Children.Add(new TextBlock
            {
                Text = "备份包含知识库、知识策略与可靠度、店铺资料和场景规则、AI转人工策略、自动回复与通知配置、模型/API等业务参数，以及 data 目录中的其他业务文件。不会上传运行日志、崩溃文件、缓存、临时文件、本机备份目录、统一API地址和Bot客户端令牌。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                LineHeight = 22,
                Margin = new Thickness(0, 0, 0, 14)
            });
            content.Children.Add(new Border
            {
                BorderBrush = Brushes.LightSteelBlue,
                BorderThickness = new Thickness(1),
                Background = Brushes.AliceBlue,
                Padding = new Thickness(12),
                Child = new TextBlock
                {
                    Text = "安全说明：备份在 Windows 客户端内使用当前 Bot 令牌派生密钥加密后再上传。新电脑必须配置相同的统一 API 地址和同一个 Bot 令牌才能下载并解密。若轮换令牌，请先用新令牌重新上传一次备份。",
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 21
                }
            });

            content.Children.Add(new TextBlock
            {
                Text = "云端备份状态",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 18, 0, 6)
            });
            _cloudStatus = new TextBlock
            {
                Text = "正在读取...",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 0, 0, 12)
            };
            content.Children.Add(_cloudStatus);

            var actions = new WrapPanel();
            content.Children.Add(actions);
            _upload = AddButton(actions, "上传当前电脑数据", 150, async (s, e) => await UploadAsync());
            _restore = AddButton(actions, "从云端一键恢复", 150, async (s, e) => await RestoreAsync());
            _refresh = AddButton(actions, "刷新状态", 96, async (s, e) => await RefreshAsync());

            _operationStatus = new TextBlock
            {
                Text = "",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.SteelBlue,
                Margin = new Thickness(0, 16, 0, 0)
            };
            content.Children.Add(_operationStatus);
            Loaded += async (s, e) => await RefreshAsync();
        }

        private async Task RefreshAsync()
        {
            if (_busy) return;
            SetBusy(true, "正在读取云端备份状态...");
            try
            {
                var state = await ClientDataCloudBackupService.GetStatusAsync();
                var exists = state.Value<bool?>("exists") == true;
                if (!exists)
                {
                    _cloudStatus.Text = "当前 Bot 令牌还没有云端整机数据备份。请先在旧电脑点击“上传当前电脑数据”。";
                    _cloudStatus.Foreground = Brushes.DarkOrange;
                    _restore.IsEnabled = false;
                }
                else
                {
                    _cloudStatus.Text = "版本 v" + (state.Value<int?>("revision") ?? 0)
                        + " · 上传时间 " + Convert.ToString(state["updated_at"] ?? state["created_at"] ?? "未知")
                        + "\n来源电脑：" + Convert.ToString(state["device_name"] ?? "未知")
                        + " · 程序版本：" + Convert.ToString(state["app_version"] ?? "未知")
                        + "\n加密包：" + ClientDataCloudBackupService.FormatBytes(state.Value<long?>("size_bytes") ?? 0)
                        + " · 业务文件：" + (state.Value<int?>("file_count") ?? 0)
                        + " · 原始数据：" + ClientDataCloudBackupService.FormatBytes(state.Value<long?>("data_bytes") ?? 0);
                    _cloudStatus.Foreground = Brushes.SeaGreen;
                    _restore.IsEnabled = true;
                }
                _operationStatus.Text = "";
            }
            catch (Exception ex)
            {
                _cloudStatus.Text = "读取失败：" + ex.Message;
                _cloudStatus.Foreground = Brushes.IndianRed;
                _operationStatus.Text = "";
            }
            finally { SetBusy(false, null); }
        }

        private async Task UploadAsync()
        {
            if (_busy) return;
            var answer = MessageBox.Show(
                "将使用当前 Bot 令牌加密并覆盖该令牌现有的云端备份。\n\n不会上传运行日志、缓存、临时文件和 Bot 令牌本身。是否继续？",
                "上传当前电脑数据",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            SetBusy(true, "正在准备上传...");
            try
            {
                var result = await ClientDataCloudBackupService.UploadAsync(SetOperation);
                MessageBox.Show(
                    "云备份上传成功。\n版本：v" + (result.Value<int?>("revision") ?? 0)
                    + "\n加密包大小：" + ClientDataCloudBackupService.FormatBytes(result.Value<long?>("size_bytes") ?? 0)
                    + "\n\n新电脑配置相同 Bot 令牌后，即可点击“从云端一键恢复”。",
                    "云备份完成",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("上传云备份失败：" + ex.Message, "云备份", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false, null);
                await RefreshAsync();
            }
        }

        private async Task RestoreAsync()
        {
            if (_busy) return;
            var answer = MessageBox.Show(
                "将把云端业务数据覆盖到当前电脑，并在恢复前自动生成本机回滚备份。\n\n当前统一 API 地址和 Bot 令牌会保留；运行日志不会被修改。恢复后程序将自动重启。是否继续？",
                "从云端一键恢复",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;

            SetBusy(true, "正在下载云端备份...");
            try
            {
                var result = await ClientDataCloudBackupService.DownloadAndRestoreAsync(SetOperation);
                MessageBox.Show(
                    "恢复成功。\n来源电脑：" + (string.IsNullOrWhiteSpace(result.SourceDevice) ? "未知" : result.SourceDevice)
                    + "\n备份时间：" + (string.IsNullOrWhiteSpace(result.CreatedAt) ? "未知" : result.CreatedAt)
                    + "\n恢复参数：" + result.ParamCount
                    + "\n恢复文件：" + result.FileCount
                    + "\n本机回滚包：" + result.RollbackPath
                    + "\n\n程序现在将自动重启。",
                    "换机恢复完成",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                ClientDataCloudBackupService.RestartApplication();
            }
            catch (Exception ex)
            {
                MessageBox.Show("从云端恢复失败：" + ex.Message, "换机恢复", MessageBoxButton.OK, MessageBoxImage.Error);
                SetBusy(false, null);
            }
        }

        private void SetOperation(string text)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _operationStatus.Text = text ?? string.Empty;
                _operationStatus.Foreground = Brushes.SteelBlue;
            }));
        }

        private void SetBusy(bool busy, string text)
        {
            _busy = busy;
            _upload.IsEnabled = !busy;
            _refresh.IsEnabled = !busy;
            if (busy) _restore.IsEnabled = false;
            if (!string.IsNullOrWhiteSpace(text)) SetOperation(text);
        }

        private static Button AddButton(Panel panel, string text, double width, RoutedEventHandler click)
        {
            var button = new Button
            {
                Content = text,
                Width = width,
                Height = 32,
                Margin = new Thickness(0, 0, 10, 8)
            };
            button.Click += click;
            panel.Children.Add(button);
            return button;
        }
    }
}
