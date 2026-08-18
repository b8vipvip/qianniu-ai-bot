using BotLib;
using BotLib.Db.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bot.UpdateNs
{
    internal static partial class BotUpdateService
    {
        // Compatibility hook retained for older call sites. It no longer creates a timer or
        // invokes CheckNowAsync. Background release discovery is server-side; the client only
        // keeps the SSE notification channel connected.
        private static void RestartTimer()
        {
            if (_timer != null)
            {
                try { _timer.Dispose(); } catch { }
                _timer = null;
            }
            RestartServerPushListener();
        }

        private static Task TimerTickAsync()
        {
            // Intentionally disabled. Never reintroduce a client-side periodic version check here.
            return Task.CompletedTask;
        }

        private static void UpdateLastCheckTime()
        {
            var settings = GetSettings();
            settings.LastCheckAt = DateTime.Now.ToString("o");
            lock (SettingsSync)
            {
                _settings = CloneSettings(settings);
                SaveSettingsInternal(_settings);
            }
        }

        private static void RaiseStatus(BotUpdateCheckResult result)
        {
            LastResult = result;
            var handler = StatusChanged;
            if (handler != null)
            {
                try { handler(result); } catch { }
            }
        }

        private static void LoadSettings()
        {
            lock (SettingsSync)
            {
                _settings = LoadSettingsInternal();
            }
        }

        private static BotUpdateSettings LoadSettingsInternal()
        {
            try
            {
                var path = GetSettingsPath();
                if (!File.Exists(path)) return NormalizeSettings(new BotUpdateSettings());
                return NormalizeSettings(
                    JsonConvert.DeserializeObject<BotUpdateSettings>(
                        File.ReadAllText(path, Encoding.UTF8)));
            }
            catch (Exception ex)
            {
                Log.Info(
                    "读取Bot更新设置失败，使用默认值: "
                    + ex.Message);
                return NormalizeSettings(new BotUpdateSettings());
            }
        }

        private static void SaveSettingsInternal(
            BotUpdateSettings settings)
        {
            settings = settings ?? new BotUpdateSettings();

            // Older updater code intentionally writes SkippedVersion directly during the
            // two-phase handoff so a rollback cannot immediately reinstall the same failed build.
            // Preserve that safety behavior, but canonicalize it as FailedInstallVersion instead
            // of confusing it with an explicit user “跳过此版本” choice.
            var compatibilitySkip = CleanVersionText(settings.SkippedVersion);
            var canonicalSkip = GetCanonicalSkippedVersion(settings);
            if (!string.Equals(
                    compatibilitySkip,
                    canonicalSkip,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(compatibilitySkip))
                {
                    settings.FailedInstallVersion = compatibilitySkip;
                    settings.FailedInstallAt = DateTime.Now.ToString("o");
                }
                else if (string.IsNullOrWhiteSpace(settings.UserSkippedVersion))
                {
                    settings.FailedInstallVersion = string.Empty;
                    settings.FailedInstallAt = string.Empty;
                }
            }

            settings = NormalizeSettings(settings);
            var path = GetSettingsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temp = path + ".tmp";
            File.WriteAllText(
                temp,
                JsonConvert.SerializeObject(
                    settings,
                    Formatting.Indented),
                new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        private static BotUpdateSettings NormalizeSettings(
            BotUpdateSettings settings)
        {
            settings = settings ?? new BotUpdateSettings();
            if (settings.AutoInstall)
            {
                // AutoCheck is retained as the persisted compatibility name for receiving the
                // server push stream. It never enables a client-side version polling timer.
                settings.AutoCheck = true;
                settings.AutoDownload = false;
            }
            settings.CheckIntervalHours = Math.Max(
                1,
                Math.Min(
                    168,
                    settings.CheckIntervalHours <= 0
                        ? 6
                        : settings.CheckIntervalHours));

            settings.SkippedVersion = CleanVersionText(settings.SkippedVersion);
            settings.UserSkippedVersion = CleanVersionText(settings.UserSkippedVersion);
            settings.FailedInstallVersion = CleanVersionText(settings.FailedInstallVersion);
            settings.FailedInstallAt = (settings.FailedInstallAt ?? string.Empty).Trim();

            // Migration for 1.1.787 and earlier: those builds stored updater handoff quarantine in
            // exactly the same field as a user skip. Treat a legacy-only value as failure
            // quarantine. It remains effective for loop prevention, but the UI can now clear it
            // deterministically instead of the updater immediately recreating a fake user skip.
            if (string.IsNullOrWhiteSpace(settings.UserSkippedVersion)
                && string.IsNullOrWhiteSpace(settings.FailedInstallVersion)
                && !string.IsNullOrWhiteSpace(settings.SkippedVersion))
            {
                settings.FailedInstallVersion = settings.SkippedVersion;
            }

            var current = NormalizeVersion(CurrentVersion);
            if (!string.IsNullOrWhiteSpace(settings.FailedInstallVersion)
                && string.Equals(
                    NormalizeVersion(settings.FailedInstallVersion),
                    current,
                    StringComparison.OrdinalIgnoreCase))
            {
                // A successful install can inherit the provisional quarantine written by the old
                // process immediately before shutdown. Once that exact version is running, the
                // quarantine has served its purpose and must not appear as “已跳过”.
                settings.FailedInstallVersion = string.Empty;
                settings.FailedInstallAt = string.Empty;
            }
            if (!string.IsNullOrWhiteSpace(settings.UserSkippedVersion)
                && string.Equals(
                    NormalizeVersion(settings.UserSkippedVersion),
                    current,
                    StringComparison.OrdinalIgnoreCase))
            {
                settings.UserSkippedVersion = string.Empty;
            }

            settings.SkippedVersion = GetCanonicalSkippedVersion(settings);
            settings.LastNotifiedVersion =
                (settings.LastNotifiedVersion ?? string.Empty).Trim();
            settings.LastNotifiedAt =
                (settings.LastNotifiedAt ?? string.Empty).Trim();
            settings.LastCheckAt =
                (settings.LastCheckAt ?? string.Empty).Trim();
            return settings;
        }

        private static BotUpdateSettings CloneSettings(
            BotUpdateSettings settings)
        {
            settings = settings ?? new BotUpdateSettings();
            var canonicalSkip = GetCanonicalSkippedVersion(settings);
            var compatibilitySkip = CleanVersionText(settings.SkippedVersion);
            return new BotUpdateSettings
            {
                AutoCheck = settings.AutoCheck,
                NotifyPopup = settings.NotifyPopup,
                AutoDownload = settings.AutoDownload,
                AutoInstall = settings.AutoInstall,
                CheckIntervalHours = settings.CheckIntervalHours,
                // Preserve a direct compatibility override long enough for SaveSettingsInternal
                // to recognize the old updater's quarantine write.
                SkippedVersion = string.Equals(
                    compatibilitySkip,
                    canonicalSkip,
                    StringComparison.OrdinalIgnoreCase)
                    ? canonicalSkip
                    : compatibilitySkip,
                UserSkippedVersion = CleanVersionText(settings.UserSkippedVersion),
                FailedInstallVersion = CleanVersionText(settings.FailedInstallVersion),
                FailedInstallAt = (settings.FailedInstallAt ?? string.Empty).Trim(),
                LastNotifiedVersion = settings.LastNotifiedVersion,
                LastNotifiedAt = settings.LastNotifiedAt,
                LastCheckAt = settings.LastCheckAt
            };
        }

        private static string GetCanonicalSkippedVersion(BotUpdateSettings settings)
        {
            if (settings == null) return string.Empty;
            var userSkip = CleanVersionText(settings.UserSkippedVersion);
            if (!string.IsNullOrWhiteSpace(userSkip)) return userSkip;
            return CleanVersionText(settings.FailedInstallVersion);
        }

        private static string CleanVersionText(string value)
        {
            return (value ?? string.Empty).Trim();
        }

        private static string ResolveCurrentVersion()
        {
            try
            {
                var installRoot = GetInstallRoot();
                var candidates = new[]
                {
                    Path.Combine(
                        installRoot,
                        "release-info.json"),
                    Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory,
                        "release-info.json")
                };
                foreach (var path in candidates.Distinct(
                    StringComparer.OrdinalIgnoreCase))
                {
                    if (!File.Exists(path)) continue;
                    var json = JObject.Parse(
                        File.ReadAllText(path, Encoding.UTF8));
                    var version = NormalizeVersion(
                        json.Value<string>("version")
                        ?? string.Empty);
                    if (IsValidVersion(version)) return version;
                }
            }
            catch
            {
            }
            var assemblyVersion =
                Assembly.GetExecutingAssembly().GetName().Version;
            return assemblyVersion == null
                ? "1.0.0"
                : assemblyVersion.ToString(3);
        }

        private static string GetInstallRoot()
        {
            var baseDir = Path.GetFullPath(
                AppDomain.CurrentDomain.BaseDirectory.TrimEnd(
                    Path.DirectorySeparatorChar));
            return string.Equals(
                Path.GetFileName(baseDir),
                "Bin",
                StringComparison.OrdinalIgnoreCase)
                ? Directory.GetParent(baseDir).FullName
                : baseDir;
        }

        private static string GetSettingsPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "QianniuAiBot",
                "data",
                "bot-update-settings.json");
        }

        private static string GetUpdateRoot()
        {
            return Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "QianniuAiBot",
                "updates");
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient();
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "QianniuAiBot-Updater/1.3");
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue(
                    "application/json"));
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "X-GitHub-Api-Version",
                "2022-11-28");
            return client;
        }

        private static int CompareVersions(
            string left,
            string right)
        {
            return ParseVersion(left).CompareTo(
                ParseVersion(right));
        }

        private static Version ParseVersion(string value)
        {
            value = NormalizeVersion(value);
            Version parsed;
            return Version.TryParse(value, out parsed)
                ? parsed
                : new Version(0, 0, 0, 0);
        }

        private static bool IsValidVersion(string value)
        {
            Version ignored;
            return Version.TryParse(
                NormalizeVersion(value),
                out ignored);
        }

        private static string NormalizeVersion(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.StartsWith(
                "bot-v",
                StringComparison.OrdinalIgnoreCase))
                value = value.Substring(5);
            else if (value.StartsWith(
                "v",
                StringComparison.OrdinalIgnoreCase))
                value = value.Substring(1);
            var dash = value.IndexOf('-');
            if (dash >= 0) value = value.Substring(0, dash);
            return value.Trim();
        }

        private static string HashFile(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                return BitConverter.ToString(
                    sha.ComputeHash(stream))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
            }
        }

        private static string SanitizeFileName(string value)
        {
            value = (value ?? "unknown").Trim();
            foreach (var ch in Path.GetInvalidFileNameChars())
                value = value.Replace(ch, '_');
            return value.Length == 0 ? "unknown" : value;
        }

        private static string QuoteArgument(string value)
        {
            return "\""
                + (value ?? string.Empty)
                    .Replace("\"", "\\\"")
                + "\"";
        }

        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(
                    new ProcessStartInfo(url)
                    {
                        UseShellExecute = true
                    });
            }
            catch (Exception ex)
            {
                Log.Info(
                    "打开更新页面失败: " + ex.Message);
            }
        }

        private static string Short(string value, int max)
        {
            value = (value ?? string.Empty)
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();
            return value.Length <= max
                ? value
                : value.Substring(0, max) + "...";
        }
    }
}
