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
        private const string GitHubLatestReleaseApi =
            "https://api.github.com/repos/b8vipvip/qianniu-ai-bot/releases/latest";
        private const string ReleasesPage =
            "https://github.com/b8vipvip/qianniu-ai-bot/releases";
        private const string PackageAssetName = "qianniu-bot-x64.zip";
        private const string ManifestAssetName = "update.json";
        private const string ControlPlaneScope = "ai-control-plane";
        private const string ControlPlaneUrlKey = "ControlPlaneUrl";
        private const string ServiceLatestPath = "/api/public/v1/bot-update/latest";
        private const int ServiceMetadataTimeoutSeconds = 6;
        private const int GitHubMetadataTimeoutSeconds = 12;
        private const int ManifestTimeoutSeconds = 8;
        private const int DownloadConnectTimeoutSeconds = 20;
        private const int DownloadReadTimeoutSeconds = 45;

        private static readonly object SettingsSync = new object();
        private static readonly HttpClient Http = CreateHttpClient();
        private static Timer _timer;
        private static BotUpdateSettings _settings;
        private static int _initialized;
        private static int _checking;
        private static int _promptOpen;

        public static event Action<BotUpdateCheckResult> StatusChanged;

        public static BotUpdateCheckResult LastResult { get; private set; }
        public static BotReleaseInfo LatestRelease { get; private set; }

        public static string CurrentVersion
        {
            get { return ResolveCurrentVersion(); }
        }

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            LoadSettings();
            RestartServerPushListener();
            Log.Info(
                "Bot自动更新服务已启动: version=" + CurrentVersion
                + ", mode=server-push-sse"
                + ", receiveServerPush=" + GetSettings().AutoCheck
                + ", autoInstall=" + GetSettings().AutoInstall
                + ", clientAutoCheck=False");
        }

        public static BotUpdateSettings GetSettings()
        {
            lock (SettingsSync)
            {
                if (_settings == null) _settings = LoadSettingsInternal();
                return CloneSettings(_settings);
            }
        }

        public static void SaveSettings(BotUpdateSettings settings)
        {
            settings = NormalizeSettings(settings ?? new BotUpdateSettings());
            lock (SettingsSync)
            {
                _settings = CloneSettings(settings);
                SaveSettingsInternal(_settings);
            }
            RestartServerPushListener();
        }

        /// <summary>
        /// Explicit manual check only. No background timer invokes this method anymore.
        /// </summary>
        public static async Task<BotUpdateCheckResult> CheckNowAsync(bool interactive)
        {
            if (Interlocked.CompareExchange(ref _checking, 1, 0) != 0)
            {
                return LastResult ?? new BotUpdateCheckResult
                {
                    Success = false,
                    CurrentVersion = CurrentVersion,
                    Message = "正在检查更新，请稍候。"
                };
            }

            try
            {
                var current = CurrentVersion;
                RaiseStatus(new BotUpdateCheckResult
                {
                    Success = true,
                    CurrentVersion = current,
                    Message = "正在手动检查新版本..."
                });

                var release = await FetchLatestReleaseAsync();
                UpdateLastCheckTime();
                if (release == null)
                {
                    var missing = new BotUpdateCheckResult
                    {
                        Success = false,
                        CurrentVersion = current,
                        Message = "未找到可用于自动更新的正式版本。"
                    };
                    RaiseStatus(missing);
                    return missing;
                }

                LatestRelease = release;
                var available = CompareVersions(release.Version, current) > 0;
                var sourceText = string.Equals(
                    release.Source,
                    "control-plane-cache",
                    StringComparison.OrdinalIgnoreCase)
                    ? "服务器"
                    : "GitHub";
                var result = new BotUpdateCheckResult
                {
                    Success = true,
                    CurrentVersion = current,
                    UpdateAvailable = available,
                    Release = release,
                    Message = available
                        ? "手动检查发现新版本 " + release.Version + "（" + sourceText + "）"
                        : "当前已是最新版本 " + current + "（" + sourceText + "）"
                };

                if (available)
                {
                    var settings = GetSettings();
                    var skipped = string.Equals(
                        settings.SkippedVersion,
                        release.Version,
                        StringComparison.OrdinalIgnoreCase);
                    if (settings.AutoInstall
                        && !skipped
                        && !string.IsNullOrWhiteSpace(release.Sha256))
                    {
                        try
                        {
                            result.Message += "，已启用自动更新，正在下载安装包...";
                            RaiseStatus(result);
                            ShowAutomaticProgressWindow(release);
                            var package = await DownloadPackageAsync(
                                release,
                                null,
                                CancellationToken.None);
                            result.InstallStarted = true;
                            result.DownloadChannel = CurrentDownloadChannel;
                            result.DownloadPercent = 100;
                            result.Message = "安装包已下载并校验完成，正在启动更新。";
                            RaiseStatus(result);
                            LaunchInstaller(package, release);
                            return result;
                        }
                        catch (Exception ex)
                        {
                            result.InstallStarted = false;
                            result.Message += "，自动更新失败：" + Short(ex.Message, 180);
                            Log.Info(
                                "Bot自动更新失败，保留人工更新入口: version=" + release.Version
                                + ", error=" + Short(ex.Message, 260));
                        }
                    }
                    else if (settings.AutoDownload && !string.IsNullOrWhiteSpace(release.Sha256))
                    {
                        try
                        {
                            await DownloadPackageAsync(
                                release,
                                null,
                                CancellationToken.None);
                            result.DownloadChannel = CurrentDownloadChannel;
                            result.DownloadPercent = 100;
                            result.Message += "，安装包已自动下载。";
                        }
                        catch (Exception ex)
                        {
                            result.Message += "，自动下载失败：" + Short(ex.Message, 180);
                            Log.Info(
                                "自动下载Bot更新失败: version=" + release.Version
                                + ", error=" + Short(ex.Message, 260));
                        }
                    }
                }

                RaiseStatus(result);
                if (interactive && available && !result.InstallStarted) return result;
                return result;
            }
            catch (Exception ex)
            {
                var failed = new BotUpdateCheckResult
                {
                    Success = false,
                    CurrentVersion = CurrentVersion,
                    Message = "手动检查更新失败：" + Short(ex.Message, 260)
                };
                RaiseStatus(failed);
                Log.Info(failed.Message);
                return failed;
            }
            finally
            {
                Interlocked.Exchange(ref _checking, 0);
            }
        }
    }
}
