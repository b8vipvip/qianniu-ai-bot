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
        public static bool IsPackageReady(BotReleaseInfo release)
        {
            if (release == null || string.IsNullOrWhiteSpace(release.Sha256))
                return false;
            try
            {
                var path = Path.Combine(
                    GetUpdateRoot(),
                    SanitizeFileName(release.Version),
                    PackageAssetName);
                return File.Exists(path)
                    && HashFile(path).Equals(
                        release.Sha256,
                        StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static void ShowUpdatePrompt(BotReleaseInfo release, Window owner)
        {
            if (release == null) return;
            if (Interlocked.CompareExchange(ref _promptOpen, 1, 0) != 0) return;
            try
            {
                var window = new BotUpdatePromptWindow(release);
                if (owner != null && owner.IsVisible) window.Owner = owner;
                window.Closed += delegate
                {
                    Interlocked.Exchange(ref _promptOpen, 0);
                };
                window.Show();
                window.Activate();
            }
            catch
            {
                Interlocked.Exchange(ref _promptOpen, 0);
                throw;
            }
        }

        public static void SkipVersion(string version)
        {
            var settings = GetSettings();
            settings.SkippedVersion = (version ?? string.Empty).Trim();
            SaveSettings(settings);
        }

        public static void ClearSkippedVersion()
        {
            var settings = GetSettings();
            settings.SkippedVersion = string.Empty;
            SaveSettings(settings);
        }

        public static void OpenReleasesPage()
        {
            OpenUrl(
                LatestRelease == null
                || string.IsNullOrWhiteSpace(LatestRelease.HtmlUrl)
                    ? ReleasesPage
                    : LatestRelease.HtmlUrl);
        }

        public static void LaunchInstaller(
            string packagePath,
            BotReleaseInfo release)
        {
            if (release == null) throw new ArgumentNullException("release");
            if (string.IsNullOrWhiteSpace(packagePath)
                || !File.Exists(packagePath))
                throw new FileNotFoundException(
                    "更新安装包不存在。",
                    packagePath);
            if (!HashFile(packagePath).Equals(
                release.Sha256,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception(
                    "安装前 SHA-256 校验失败，已拒绝执行更新。");
            }

            var sourceScript = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "BotAutoUpdater.ps1");
            if (!File.Exists(sourceScript))
                throw new FileNotFoundException(
                    "自动更新程序 BotAutoUpdater.ps1 缺失。",
                    sourceScript);

            // The current process is about to intentionally exit and hand control to the updater.
            // Persist a provisional skip for this exact target first. If the updater later rolls
            // back to the old build, that build must stay running instead of immediately receiving
            // the same SSE release and entering an update -> rollback -> update loop. A successful
            // install is unaffected because CurrentVersion then equals the skipped target; any later
            // release has a different version and can still auto-install normally.
            QuarantineVersionForUpdaterHandoff(release.Version);
            BotProcessWatchdog.MarkExpectedExit("auto-update:" + release.Version);

            var tempScript = Path.Combine(
                Path.GetTempPath(),
                "QianniuAiBotUpdater-"
                + Guid.NewGuid().ToString("N")
                + ".ps1");
            File.Copy(sourceScript, tempScript, true);
            var installRoot = GetInstallRoot();
            var arguments =
                "-NoProfile -ExecutionPolicy Bypass -File "
                + QuoteArgument(tempScript)
                + " -PackagePath " + QuoteArgument(packagePath)
                + " -InstallDir " + QuoteArgument(installRoot)
                + " -ExpectedSha256 " + QuoteArgument(release.Sha256)
                + " -ExpectedVersion " + QuoteArgument(release.Version)
                + " -CurrentPid " + Process.GetCurrentProcess().Id;
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = arguments,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(tempScript),
                WindowStyle = ProcessWindowStyle.Normal
            });
            if (process == null)
                throw new Exception("无法启动自动更新程序。");
            Log.Info(
                "已启动Bot自动更新程序: version=" + release.Version
                + ", package=" + packagePath
                + "；目标版本已预先隔离，若回滚不会再次自动循环安装。");
            if (Application.Current != null)
            {
                Application.Current.Dispatcher.BeginInvoke(
                    new Action(delegate
                    {
                        Application.Current.Shutdown();
                    }));
            }
        }

        private static void QuarantineVersionForUpdaterHandoff(string version)
        {
            version = NormalizeVersion(version);
            if (string.IsNullOrWhiteSpace(version)) return;
            try
            {
                lock (SettingsSync)
                {
                    var settings = _settings == null
                        ? LoadSettingsInternal()
                        : CloneSettings(_settings);
                    settings.SkippedVersion = version;
                    _settings = CloneSettings(settings);
                    SaveSettingsInternal(_settings);
                }
                Log.Info("自动更新交接保护已记录目标版本隔离: version=" + version);
            }
            catch (Exception ex)
            {
                // Do not block an otherwise valid manual update solely because the anti-loop marker
                // could not be persisted; the external updater also writes the same quarantine on
                // failure as a second line of defense.
                Log.Info("记录自动更新交接保护失败，将依赖更新器失败隔离: version="
                    + version + ", error=" + Short(ex.Message, 180));
            }
        }

        private static void MaybeShowBackgroundPrompt(BotReleaseInfo release)
        {
            var settings = GetSettings();
            if (!settings.NotifyPopup) return;
            if (string.Equals(
                settings.SkippedVersion,
                release.Version,
                StringComparison.OrdinalIgnoreCase)) return;
            DateTime lastAt;
            if (string.Equals(
                    settings.LastNotifiedVersion,
                    release.Version,
                    StringComparison.OrdinalIgnoreCase)
                && DateTime.TryParse(settings.LastNotifiedAt, out lastAt)
                && lastAt >= DateTime.Now.AddHours(-24))
            {
                return;
            }

            settings.LastNotifiedVersion = release.Version;
            settings.LastNotifiedAt = DateTime.Now.ToString("o");
            SaveSettings(settings);
            if (Application.Current == null) return;
            Application.Current.Dispatcher.BeginInvoke(
                new Action(delegate
                {
                    Window owner = null;
                    try { owner = Application.Current.MainWindow; } catch { }
                    ShowUpdatePrompt(release, owner);
                }));
        }
    }
}
