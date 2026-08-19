using BotLib;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Bot.UpdateNs
{
    internal static partial class BotUpdateService
    {
        private const string ServerPushEventsPath = "/api/public/v1/bot-update/events";
        private static readonly object ServerPushSync = new object();
        private static CancellationTokenSource _serverPushCts;
        private static Task _serverPushTask;

        private static readonly object AutoInstallRetrySync = new object();
        private static string _autoInstallRetryVersion = string.Empty;
        private static bool _autoInstallInFlight;
        private static int _autoInstallFailureCount;
        private static DateTime _autoInstallRetryAfterUtc = DateTime.MinValue;
        private static int _autoInstallRetryGeneration;

        internal static void RestartServerPushListener()
        {
            lock (ServerPushSync)
            {
                if (_serverPushCts != null)
                {
                    try { _serverPushCts.Cancel(); } catch { }
                    try { _serverPushCts.Dispose(); } catch { }
                    _serverPushCts = null;
                }
                _serverPushTask = null;

                var settings = GetSettings();
                if (!settings.AutoCheck)
                {
                    Log.Info("Bot版本服务端推送已关闭；客户端不会后台检查版本。" );
                    return;
                }

                _serverPushCts = new CancellationTokenSource();
                var token = _serverPushCts.Token;
                _serverPushTask = Task.Run(() => ServerPushLoopAsync(token), token);
            }
        }

        private static async Task ServerPushLoopAsync(CancellationToken token)
        {
            Log.Info("Bot版本通知已切换为服务端SSE主动下发：客户端不再定时检查GitHub或版本接口。" );
            var retrySeconds = 2;
            while (!token.IsCancellationRequested)
            {
                var connected = false;
                var urls = GetConfiguredControlPlaneUrls();
                if (urls == null || urls.Count == 0)
                {
                    await DelaySafeAsync(TimeSpan.FromSeconds(5), token);
                    continue;
                }

                foreach (var baseUrl in urls)
                {
                    if (token.IsCancellationRequested) return;
                    try
                    {
                        connected = true;
                        await ConsumeServerPushAsync(baseUrl, token);
                        retrySeconds = 2;
                    }
                    catch (OperationCanceledException)
                    {
                        if (token.IsCancellationRequested) return;
                    }
                    catch (Exception ex)
                    {
                        Log.ErrorWithMaxCount(
                            "Bot版本推送连接中断，将自动重连: server=" + baseUrl
                            + ", error=" + Short(ex.Message, 220),
                            20);
                    }
                }

                await DelaySafeAsync(
                    TimeSpan.FromSeconds(connected ? retrySeconds : 5),
                    token);
                retrySeconds = Math.Min(30, retrySeconds * 2);
            }
        }

        private static async Task ConsumeServerPushAsync(
            string baseUrl,
            CancellationToken token)
        {
            var url = baseUrl.TrimEnd('/') + ServerPushEventsPath
                + "?current_version=" + Uri.EscapeDataString(CurrentVersion);
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
                using (var response = await Http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    token))
                {
                    response.EnsureSuccessStatusCode();
                    Log.Info("Bot版本服务端推送通道已连接: server=" + baseUrl);
                    using (var stream = await response.Content.ReadAsStreamAsync())
                    using (var reader = new StreamReader(stream))
                    {
                        while (!token.IsCancellationRequested)
                        {
                            var line = await ReadLineWithCancellationAsync(reader, token);
                            if (line == null) throw new IOException("版本推送SSE连接已关闭");
                            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                            var jsonText = line.Substring(5).Trim();
                            if (jsonText.Length == 0) continue;
                            BotReleaseInfo release;
                            try
                            {
                                release = ParseServerPushRelease(
                                    JObject.Parse(jsonText),
                                    baseUrl);
                            }
                            catch (Exception ex)
                            {
                                Log.Info("忽略无效Bot版本推送: " + Short(ex.Message, 180));
                                continue;
                            }
                            await HandleServerPushedReleaseAsync(release);
                        }
                    }
                }
            }
        }

        private static BotReleaseInfo ParseServerPushRelease(
            JObject json,
            string serverBaseUrl)
        {
            if (json == null) throw new ArgumentNullException("json");
            if (json.Value<bool?>("mirror_ready") != true
                || json.Value<bool?>("package_verified_on_server") != true)
            {
                throw new InvalidDataException(
                    "服务端安装包尚未完整下载并校验，拒绝版本通知和自动更新。");
            }

            var mirrorUrl = (json.Value<string>("mirror_url") ?? string.Empty).Trim();
            if (!Uri.IsWellFormedUriString(mirrorUrl, UriKind.Absolute))
            {
                throw new InvalidDataException(
                    "服务端版本通知缺少有效的服务器安装包地址。");
            }
            if (!IsSameServerOrigin(serverBaseUrl, mirrorUrl))
            {
                throw new InvalidDataException(
                    "服务端版本通知的安装包地址不属于当前服务器，已拒绝自动更新。"
                );
            }

            var release = new BotReleaseInfo
            {
                Version = NormalizeVersion(json.Value<string>("version") ?? string.Empty),
                Tag = (json.Value<string>("tag") ?? string.Empty).Trim(),
                Name = json.Value<string>("name") ?? string.Empty,
                Notes = json.Value<string>("notes") ?? string.Empty,
                HtmlUrl = json.Value<string>("html_url") ?? ReleasesPage,
                // A server-pushed release is server-owned end to end. Keep PackageUrl equal to
                // MirrorUrl so DownloadPackageAsync deduplicates the two entries and cannot fall
                // through to the GitHub asset URL after the server has announced the version.
                PackageUrl = mirrorUrl,
                MirrorUrl = mirrorUrl,
                Sha256 = (json.Value<string>("sha256") ?? string.Empty).Trim().ToLowerInvariant(),
                PackageSize = json.Value<long?>("size") ?? 0,
                PublishedAt = ParseDateTime(json.Value<string>("published_at")),
                Commit = (json.Value<string>("commit") ?? string.Empty).Trim(),
                Source = "server-push-sse"
            };
            ValidateRelease(release, true);
            return release;
        }

        private static bool IsSameServerOrigin(string serverBaseUrl, string packageUrl)
        {
            Uri server;
            Uri package;
            if (!Uri.TryCreate(serverBaseUrl, UriKind.Absolute, out server)
                || !Uri.TryCreate(packageUrl, UriKind.Absolute, out package))
            {
                return false;
            }

            return string.Equals(server.Scheme, package.Scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(server.Host, package.Host, StringComparison.OrdinalIgnoreCase)
                && server.Port == package.Port;
        }

        private static async Task HandleServerPushedReleaseAsync(BotReleaseInfo release)
        {
            if (release == null || CompareVersions(release.Version, CurrentVersion) <= 0) return;
            LatestRelease = release;
            var settings = GetSettings();
            var skipped = string.Equals(
                settings.SkippedVersion,
                release.Version,
                StringComparison.OrdinalIgnoreCase);
            var result = new BotUpdateCheckResult
            {
                Success = true,
                CurrentVersion = CurrentVersion,
                UpdateAvailable = true,
                Release = release,
                DownloadPercent = -1,
                Message = "服务端已完整缓存并校验新版本 " + release.Version
                    + "；客户端未执行版本轮询。"
            };
            RaiseStatus(result);

            if (skipped)
            {
                var failedInstallBlocked = string.Equals(
                    settings.FailedInstallVersion,
                    release.Version,
                    StringComparison.OrdinalIgnoreCase);
                result.Message += failedInstallBlocked
                    ? " 当前版本因上次安装失败处于隔离状态；不会自动循环重装，清除失败隔离后才会重试。"
                    : " 当前版本已由用户设置为跳过。";
                RaiseStatus(result);
                return;
            }

            if (settings.AutoInstall)
            {
                string blockedReason;
                if (!TryBeginAutoInstallAttempt(release.Version, out blockedReason))
                {
                    result.Message = blockedReason;
                    RaiseStatus(result);
                    return;
                }

                try
                {
                    result.Message = "服务端已准备好新版本 " + release.Version
                        + "，已启用自动更新，正在从服务器下载安装包...";
                    RaiseStatus(result);
                    ShowAutomaticProgressWindow(release);
                    var package = await DownloadPackageAsync(
                        release,
                        null,
                        CancellationToken.None);
                    CompleteAutoInstallAttempt(release.Version, true);
                    result.InstallStarted = true;
                    result.DownloadPercent = 100;
                    result.DownloadChannel = CurrentDownloadChannel;
                    result.Message = "服务器安装包已下载并校验完成，正在启动自动安装。";
                    RaiseStatus(result);
                    LaunchInstaller(package, release);
                    return;
                }
                catch (Exception ex)
                {
                    var delay = CompleteAutoInstallAttempt(release.Version, false);
                    result.Message = "自动更新失败：" + Short(ex.Message, 220)
                        + "；不会绕过服务器切换到GitHub，将在 "
                        + FormatRetryDelay(delay) + " 后自动重试服务器通道。";
                    result.DownloadChannel = CurrentDownloadChannel;
                    result.DownloadPercent = CurrentDownloadPercent;
                    RaiseStatus(result);
                    Log.Info(result.Message);
                    ScheduleAutoInstallRetry(release, delay);
                    // Auto-install owns this version. Do not open a second manual prompt on top of
                    // the progress window after each failed source attempt.
                    return;
                }
            }
            else if (settings.AutoDownload)
            {
                try
                {
                    await DownloadPackageAsync(release, null, CancellationToken.None);
                    result.DownloadPercent = 100;
                    result.DownloadChannel = CurrentDownloadChannel;
                    result.Message = "新版本安装包已从服务器自动下载并通过校验。";
                    RaiseStatus(result);
                }
                catch (Exception ex)
                {
                    result.Message = "服务器自动下载失败：" + Short(ex.Message, 220);
                    RaiseStatus(result);
                }
            }

            if (settings.NotifyPopup && !result.InstallStarted)
            {
                MaybeShowBackgroundPrompt(release);
            }
        }

        private static bool TryBeginAutoInstallAttempt(string version, out string blockedReason)
        {
            blockedReason = string.Empty;
            version = NormalizeVersion(version);
            lock (AutoInstallRetrySync)
            {
                if (!string.Equals(_autoInstallRetryVersion, version, StringComparison.OrdinalIgnoreCase))
                {
                    _autoInstallRetryVersion = version;
                    _autoInstallInFlight = false;
                    _autoInstallFailureCount = 0;
                    _autoInstallRetryAfterUtc = DateTime.MinValue;
                    _autoInstallRetryGeneration++;
                }

                if (_autoInstallInFlight)
                {
                    blockedReason = "版本 " + version
                        + " 的自动更新任务已经在运行，已忽略重复的服务端版本通知。";
                    return false;
                }

                var now = DateTime.UtcNow;
                if (_autoInstallRetryAfterUtc > now)
                {
                    blockedReason = "版本 " + version
                        + " 上一次服务器自动下载失败，正在退避等待；"
                        + "不会绕过服务器切换到GitHub。剩余约 "
                        + FormatRetryDelay(_autoInstallRetryAfterUtc - now) + "。";
                    return false;
                }

                _autoInstallInFlight = true;
                return true;
            }
        }

        private static TimeSpan CompleteAutoInstallAttempt(string version, bool success)
        {
            version = NormalizeVersion(version);
            lock (AutoInstallRetrySync)
            {
                if (!string.Equals(_autoInstallRetryVersion, version, StringComparison.OrdinalIgnoreCase))
                    return TimeSpan.Zero;

                _autoInstallInFlight = false;
                _autoInstallRetryGeneration++;
                if (success)
                {
                    _autoInstallFailureCount = 0;
                    _autoInstallRetryAfterUtc = DateTime.MinValue;
                    return TimeSpan.Zero;
                }

                _autoInstallFailureCount++;
                var seconds = _autoInstallFailureCount <= 1
                    ? 60
                    : (_autoInstallFailureCount == 2
                        ? 180
                        : (_autoInstallFailureCount == 3 ? 600 : 1800));
                var delay = TimeSpan.FromSeconds(seconds);
                _autoInstallRetryAfterUtc = DateTime.UtcNow.Add(delay);
                return delay;
            }
        }

        private static void ScheduleAutoInstallRetry(BotReleaseInfo release, TimeSpan delay)
        {
            if (release == null || delay <= TimeSpan.Zero) return;
            int generation;
            lock (AutoInstallRetrySync)
            {
                generation = _autoInstallRetryGeneration;
            }

            Task.Run(async () =>
            {
                await Task.Delay(delay);
                lock (AutoInstallRetrySync)
                {
                    if (generation != _autoInstallRetryGeneration) return;
                    if (_autoInstallInFlight) return;
                    if (!string.Equals(
                        _autoInstallRetryVersion,
                        release.Version,
                        StringComparison.OrdinalIgnoreCase)) return;
                }
                if (CompareVersions(release.Version, CurrentVersion) <= 0) return;
                var settings = GetSettings();
                if (!settings.AutoInstall) return;
                await HandleServerPushedReleaseAsync(release);
            });
        }

        private static string FormatRetryDelay(TimeSpan delay)
        {
            if (delay <= TimeSpan.Zero) return "0 秒";
            if (delay.TotalMinutes >= 1)
            {
                var minutes = (int)Math.Ceiling(delay.TotalMinutes);
                return minutes + " 分钟";
            }
            return Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds)) + " 秒";
        }

        private static void ShowAutomaticProgressWindow(BotReleaseInfo release)
        {
            if (Application.Current == null) return;
            Application.Current.Dispatcher.BeginInvoke(new Action(delegate
            {
                BotUpdateAutoProgressWindow.ShowFor(release);
            }));
        }

        private static async Task<string> ReadLineWithCancellationAsync(
            StreamReader reader,
            CancellationToken token)
        {
            var readTask = reader.ReadLineAsync();
            var cancelTask = Task.Delay(Timeout.Infinite, token);
            var completed = await Task.WhenAny(readTask, cancelTask);
            token.ThrowIfCancellationRequested();
            return await readTask;
        }

        private static async Task DelaySafeAsync(TimeSpan delay, CancellationToken token)
        {
            try { await Task.Delay(delay, token); }
            catch (OperationCanceledException) { }
        }
    }
}
