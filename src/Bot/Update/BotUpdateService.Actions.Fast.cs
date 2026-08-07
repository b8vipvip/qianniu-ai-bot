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
                + ", package=" + packagePath);
            if (Application.Current != null)
            {
                Application.Current.Dispatcher.BeginInvoke(
                    new Action(delegate
                    {
                        Application.Current.Shutdown();
                    }));
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
