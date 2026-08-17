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
                                release = ParseServerPushRelease(JObject.Parse(jsonText));
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

        private static BotReleaseInfo ParseServerPushRelease(JObject json)
        {
            var release = new BotReleaseInfo
            {
                Version = NormalizeVersion(json.Value<string>("version") ?? string.Empty),
                Tag = (json.Value<string>("tag") ?? string.Empty).Trim(),
                Name = json.Value<string>("name") ?? string.Empty,
                Notes = json.Value<string>("notes") ?? string.Empty,
                HtmlUrl = json.Value<string>("html_url") ?? ReleasesPage,
                PackageUrl = (json.Value<string>("download_url") ?? string.Empty).Trim(),
                MirrorUrl = (json.Value<string>("mirror_url") ?? string.Empty).Trim(),
                Sha256 = (json.Value<string>("sha256") ?? string.Empty).Trim().ToLowerInvariant(),
                PackageSize = json.Value<long?>("size") ?? 0,
                PublishedAt = ParseDateTime(json.Value<string>("published_at")),
                Commit = (json.Value<string>("commit") ?? string.Empty).Trim(),
                Source = "server-push-sse"
            };
            ValidateRelease(release, true);
            return release;
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
                Message = "服务端主动通知新版本 " + release.Version
                    + "；客户端未执行版本轮询。"
            };
            RaiseStatus(result);

            if (skipped)
            {
                result.Message += " 当前版本已设置为跳过。";
                RaiseStatus(result);
                return;
            }

            if (settings.AutoInstall)
            {
                try
                {
                    result.Message = "服务端主动通知新版本 " + release.Version
                        + "，已启用自动更新，准备下载安装包...";
                    RaiseStatus(result);
                    ShowAutomaticProgressWindow(release);
                    var package = await DownloadPackageAsync(
                        release,
                        null,
                        CancellationToken.None);
                    result.InstallStarted = true;
                    result.DownloadPercent = 100;
                    result.DownloadChannel = CurrentDownloadChannel;
                    result.Message = "安装包已下载并校验完成，正在启动自动安装。";
                    RaiseStatus(result);
                    LaunchInstaller(package, release);
                    return;
                }
                catch (Exception ex)
                {
                    result.Message = "自动更新失败：" + Short(ex.Message, 220);
                    RaiseStatus(result);
                    Log.Info(result.Message);
                }
            }
            else if (settings.AutoDownload)
            {
                try
                {
                    await DownloadPackageAsync(release, null, CancellationToken.None);
                    result.DownloadPercent = 100;
                    result.DownloadChannel = CurrentDownloadChannel;
                    result.Message = "新版本安装包已自动下载并通过校验。";
                    RaiseStatus(result);
                }
                catch (Exception ex)
                {
                    result.Message = "自动下载失败：" + Short(ex.Message, 220);
                    RaiseStatus(result);
                }
            }

            if (settings.NotifyPopup && !result.InstallStarted)
            {
                MaybeShowBackgroundPrompt(release);
            }
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
