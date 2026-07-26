using BotLib;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;

namespace Bot.UpdateNs
{
    /// <summary>
    /// 每次启动都记录实际运行的 Bot.exe 路径、哈希和构建信息。
    /// 这样即使电脑中存在多个安装目录，也能直接从日志确认当前进程到底运行了哪一份程序。
    /// </summary>
    internal static class RuntimeBuildIdentityService
    {
        private static int _initialized;

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
