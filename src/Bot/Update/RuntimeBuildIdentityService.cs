using BotLib;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Bot.UpdateNs
{
    /// <summary>
    /// 每次启动都记录实际运行的 Bot.exe 路径、哈希和构建信息。
    /// 这样即使电脑中存在多个安装目录，也能直接从日志确认当前进程到底运行了哪一份程序。
    /// </summary>
    internal static class RuntimeBuildIdentityService
    {
        private const string UpdateHealthFileEnvironmentVariable = "QIANNIU_BOT_UPDATE_HEALTH_FILE";
        private const string RuntimeLogVersionMarkerFileName = "runtime-log-release-version.txt";
        private static int _initialized;

        /// <summary>
        /// 在主日志打开前处理“更新时允许不足 1024 KiB 提前归档”的唯一例外。
        /// 普通启动/崩溃重启绝不会从这里切分日志：必须同时满足版本 A→B 变化，且本进程
        /// 是官方更新器通过 QIANNIU_BOT_UPDATE_HEALTH_FILE 交接拉起的首个 Bot 进程。
        /// 版本标记缺失或损坏时只建立当前版本基线，不归档活动日志。
        /// </summary>
        internal static string PrepareRuntimeLogForStartup(string userDataRoot)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(userDataRoot)) return string.Empty;

                var currentVersion = NormalizeReleaseVersion(BotUpdateService.CurrentVersion);
                if (string.IsNullOrWhiteSpace(currentVersion)) return string.Empty;

                var markerDirectory = Path.Combine(userDataRoot, "data");
                var markerPath = Path.Combine(markerDirectory, RuntimeLogVersionMarkerFileName);
                var previousVersion = ReadRuntimeLogVersionMarker(markerPath);

                // First run after this contract is introduced (or after a corrupt marker) cannot
                // prove an A→B transition. Establish a baseline only; never manufacture a tiny log.
                if (string.IsNullOrWhiteSpace(previousVersion))
                {
                    TryPersistRuntimeLogVersionMarker(markerPath, currentVersion);
                    return string.Empty;
                }

                if (string.Equals(previousVersion, currentVersion, StringComparison.OrdinalIgnoreCase))
                {
                    return string.Empty;
                }

                // Persist B before touching the active log. This makes the update-only archive
                // strictly at-most-once even if a later move is blocked by antivirus/file locking.
                if (!TryPersistRuntimeLogVersionMarker(markerPath, currentVersion))
                {
                    return string.Empty;
                }

                var updateHealthFile = Environment.GetEnvironmentVariable(UpdateHealthFileEnvironmentVariable);
                if (string.IsNullOrWhiteSpace(updateHealthFile))
                {
                    return string.Empty;
                }

                var activeLogPath = Path.Combine(userDataRoot, "logs", "运行日志.txt");
                var archived = RotateRuntimeLogForVerifiedUpdate(activeLogPath);
                if (string.IsNullOrWhiteSpace(archived)) return string.Empty;

                return "检测到真实客户端版本更新，已允许不足1024KiB的活动日志归档一次: "
                    + previousVersion + " -> " + currentVersion
                    + ", archived=" + archived;
            }
            catch
            {
                // 日志准备必须 fail-open，任何版本标记问题都不能阻止 Bot 启动。
                return string.Empty;
            }
        }

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var exe = Path.GetFullPath(assembly.Location);
                var root = ResolveInstallRoot(exe);
                var release = ReadReleaseInfo(root, Path.GetDirectoryName(exe));
                var assemblyVersion = assembly.GetName().Version == null
                    ? "未知"
                    : assembly.GetName().Version.ToString();
                var fileVersion = FileVersionInfo.GetVersionInfo(exe).FileVersion ?? "未知";
                var sha256 = File.Exists(exe) ? HashFile(exe) : "文件不存在";

                Log.Info("运行构建身份: pid=" + Process.GetCurrentProcess().Id
                    + ", exe=" + exe
                    + ", sha256=" + sha256
                    + ", assemblyVersion=" + assemblyVersion
                    + ", fileVersion=" + fileVersion
                    + ", releaseVersion=" + Value(release, "version", BotUpdateService.CurrentVersion)
                    + ", commit=" + Value(release, "commit", "未记录")
                    + ", sourceRunId=" + Value(release, "source_run_id", "未记录")
                    + ", installRoot=" + root);
            }
            catch (Exception ex)
            {
                Log.Info("读取运行构建身份失败: " + ex.Message);
            }
        }

        private static string ReadRuntimeLogVersionMarker(string markerPath)
        {
            try
            {
                if (!File.Exists(markerPath)) return string.Empty;
                return NormalizeReleaseVersion(File.ReadAllText(markerPath, Encoding.UTF8));
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool TryPersistRuntimeLogVersionMarker(string markerPath, string version)
        {
            var tempPath = markerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var directory = Path.GetDirectoryName(markerPath);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(tempPath, version, new UTF8Encoding(false));
                if (File.Exists(markerPath))
                {
                    File.Replace(tempPath, markerPath, null, true);
                }
                else
                {
                    File.Move(tempPath, markerPath);
                }
                return true;
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                return false;
            }
        }

        private static string NormalizeReleaseVersion(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.StartsWith("bot-v", StringComparison.OrdinalIgnoreCase)) value = value.Substring(5);
            else if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase)) value = value.Substring(1);
            var dash = value.IndexOf('-');
            if (dash >= 0) value = value.Substring(0, dash);
            value = value.Trim();
            Version parsed;
            return Version.TryParse(value, out parsed) ? parsed.ToString() : string.Empty;
        }

        private static string RotateRuntimeLogForVerifiedUpdate(string activeLogPath)
        {
            try
            {
                if (!File.Exists(activeLogPath)) return string.Empty;
                var info = new FileInfo(activeLogPath);
                if (info.Length <= 0) return string.Empty;

                var directory = Path.GetDirectoryName(activeLogPath) ?? string.Empty;
                var stem = Path.GetFileNameWithoutExtension(activeLogPath);
                var extension = Path.GetExtension(activeLogPath);
                var stampUtc = info.LastWriteTimeUtc == DateTime.MinValue ? DateTime.UtcNow : info.LastWriteTimeUtc;
                for (var sequence = 0; sequence < 10000; sequence++)
                {
                    var name = stem + "." + stampUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss-fff")
                        + "." + sequence.ToString("D3") + extension;
                    var destination = Path.Combine(directory, name);
                    if (File.Exists(destination)) continue;
                    File.Move(activeLogPath, destination);
                    return destination;
                }
            }
            catch
            {
            }
            return string.Empty;
        }

        private static string ResolveInstallRoot(string exe)
        {
            var directory = Path.GetDirectoryName(exe) ?? AppDomain.CurrentDomain.BaseDirectory;
            return string.Equals(Path.GetFileName(directory), "Bin", StringComparison.OrdinalIgnoreCase)
                ? Directory.GetParent(directory).FullName
                : directory;
        }

        private static JObject ReadReleaseInfo(params string[] roots)
        {
            foreach (var root in (roots ?? new string[0])
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var path = Path.Combine(root, "release-info.json");
                    if (File.Exists(path)) return JObject.Parse(File.ReadAllText(path));
                }
                catch
                {
                }
            }
            return null;
        }

        private static string Value(JObject json, string name, string fallback)
        {
            if (json == null || json[name] == null) return fallback;
            var value = Convert.ToString(json[name]).Trim();
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static string HashFile(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }
    }
}