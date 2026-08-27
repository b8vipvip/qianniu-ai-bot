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
        private static readonly SemaphoreSlim DownloadGate = new SemaphoreSlim(1, 1);
        private static int _lastReportedDownloadPercent = -100;

        public static string CurrentDownloadChannel { get; private set; } = string.Empty;
        public static int CurrentDownloadPercent { get; private set; } = -1;
        public static long CurrentDownloadedBytes { get; private set; }
        public static long CurrentDownloadTotalBytes { get; private set; }

        public static async Task<string> DownloadPackageAsync(
            BotReleaseInfo release,
            IProgress<int> progress,
            CancellationToken cancellationToken)
        {
            if (release == null) throw new ArgumentNullException("release");
            if (string.IsNullOrWhiteSpace(release.Sha256))
                throw new Exception("发布版本缺少 SHA-256 校验信息，已拒绝自动安装。");

            var waitingForExistingDownload = DownloadGate.CurrentCount == 0;
            if (waitingForExistingDownload)
            {
                Log.Info("已有Bot更新下载任务正在进行，当前请求等待复用结果: version=" + release.Version);
            }

            await DownloadGate.WaitAsync(cancellationToken);
            try
            {
                var directory = Path.Combine(GetUpdateRoot(), SanitizeFileName(release.Version));
                Directory.CreateDirectory(directory);
                var target = Path.Combine(directory, PackageAssetName);
                if (File.Exists(target)
                    && HashFile(target).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    CurrentDownloadChannel = "本地缓存";
                    CurrentDownloadPercent = 100;
                    CurrentDownloadedBytes = new FileInfo(target).Length;
                    CurrentDownloadTotalBytes = CurrentDownloadedBytes;
                    if (progress != null) progress.Report(100);
                    RaiseDownloadStatus(release, "本地缓存", 100, CurrentDownloadedBytes, CurrentDownloadTotalBytes, true);
                    if (waitingForExistingDownload)
                        Log.Info("Bot更新下载任务已复用已完成安装包: version=" + release.Version);
                    return target;
                }

                // Version discovery may still fall back to GitHub metadata, but package bytes are
                // deliberately server-only. The client first asks the control plane to prepare the
                // exact release. If the server is already downloading it, the same single-flight
                // job is reused and the client waits for readiness before opening the mirror URL.
                var mirrorUrl = await EnsureServerPackageReadyAsync(release, cancellationToken);
                if (string.IsNullOrWhiteSpace(mirrorUrl))
                    throw new Exception("服务端未返回安装包镜像地址；客户端已禁止直接从 GitHub 下载安装包。");
                release.MirrorUrl = mirrorUrl;

                var partial = target + ".partial";
                try
                {
                    if (File.Exists(partial)) File.Delete(partial);
                    CurrentDownloadChannel = "服务器";
                    CurrentDownloadPercent = -1;
                    CurrentDownloadedBytes = 0;
                    CurrentDownloadTotalBytes = Math.Max(0, release.PackageSize);
                    _lastReportedDownloadPercent = -100;
                    RaiseDownloadStatus(release, "服务器", -1, 0, CurrentDownloadTotalBytes, false);
                    Log.Info("Bot更新开始连接下载通道: version=" + release.Version + ", channel=服务器");

                    await DownloadFromUrlAsync(
                        mirrorUrl,
                        partial,
                        release.PackageSize,
                        "服务器",
                        release,
                        progress,
                        cancellationToken);

                    var actual = HashFile(partial);
                    if (!actual.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new Exception("安装包 SHA-256 不一致，期望 " + release.Sha256 + "，实际 " + actual);

                    if (File.Exists(target)) File.Delete(target);
                    File.Move(partial, target);
                    var length = new FileInfo(target).Length;
                    CurrentDownloadChannel = "服务器";
                    CurrentDownloadPercent = 100;
                    CurrentDownloadedBytes = length;
                    CurrentDownloadTotalBytes = release.PackageSize > 0 ? release.PackageSize : length;
                    if (progress != null) progress.Report(100);
                    RaiseDownloadStatus(release, "服务器", 100, CurrentDownloadedBytes, CurrentDownloadTotalBytes, true);
                    Log.Info("Bot更新安装包下载成功: version=" + release.Version + ", source=服务器");
                    return target;
                }
                catch (OperationCanceledException)
                {
                    try { if (File.Exists(partial)) File.Delete(partial); } catch { }
                    Log.Info("Bot更新下载被用户取消: source=服务器, version=" + release.Version);
                    throw;
                }
                catch (Exception ex)
                {
                    try { if (File.Exists(partial)) File.Delete(partial); } catch { }
                    throw new Exception("服务端安装包下载失败。客户端不会回退到 GitHub：" + Short(ex.Message, 240), ex);
                }
            }
            finally
            {
                DownloadGate.Release();
            }
        }

        private static async Task<string> EnsureServerPackageReadyAsync(
            BotReleaseInfo release,
            CancellationToken cancellationToken)
        {
            var tag = (release.Tag ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(tag)) tag = "bot-v" + NormalizeVersion(release.Version);
            var encodedTag = Uri.EscapeDataString(tag);
            var bases = GetConfiguredControlPlaneUrls();
            if (bases == null || bases.Count == 0)
                throw new Exception("未配置可用的更新服务端地址，无法下载安装包。");

            var errors = new List<string>();
            foreach (var baseUrl in bases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var root = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
                if (root.Length == 0) continue;
                var ensureUrl = root + "/api/public/v1/bot-update/ensure/" + encodedTag;
                var statusUrl = root + "/api/public/v1/bot-update/status/" + encodedTag;
                try
                {
                    JObject state;
                    using (var request = new HttpRequestMessage(HttpMethod.Post, ensureUrl))
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, DownloadConnectTimeoutSeconds)));
                        using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token))
                        {
                            var body = await response.Content.ReadAsStringAsync();
                            if (!response.IsSuccessStatusCode)
                                throw new Exception("prepare HTTP " + (int)response.StatusCode + " " + Short(body, 180));
                            state = JObject.Parse(body);
                        }
                    }

                    var deadline = DateTime.UtcNow.AddMinutes(12);
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var ready = state.Value<bool?>("ready") == true;
                        var error = (state.Value<string>("error") ?? string.Empty).Trim();
                        var mirror = (state.Value<string>("mirror_url") ?? string.Empty).Trim();
                        if (ready)
                        {
                            if (string.IsNullOrWhiteSpace(mirror))
                                mirror = root + "/api/public/v1/bot-update/download/" + encodedTag;
                            Log.Info("服务端Bot更新安装包已就绪: version=" + release.Version + ", server=" + root);
                            return mirror;
                        }
                        if (!string.IsNullOrWhiteSpace(error))
                            throw new Exception("服务端准备安装包失败：" + Short(error, 200));
                        if (DateTime.UtcNow >= deadline)
                            throw new TimeoutException("等待服务端从 GitHub 准备安装包超过 12 分钟。");

                        CurrentDownloadChannel = "服务器准备中";
                        CurrentDownloadPercent = -1;
                        RaiseDownloadStatus(release, "服务器准备中", -1, 0, Math.Max(0, release.PackageSize), false);
                        await Task.Delay(1000, cancellationToken);

                        using (var request = new HttpRequestMessage(HttpMethod.Get, statusUrl))
                        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                        {
                            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, DownloadConnectTimeoutSeconds)));
                            using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token))
                            {
                                var body = await response.Content.ReadAsStringAsync();
                                if (!response.IsSuccessStatusCode)
                                    throw new Exception("status HTTP " + (int)response.StatusCode + " " + Short(body, 180));
                                state = JObject.Parse(body);
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    errors.Add(root + "：连接或等待超时");
                }
                catch (Exception ex)
                {
                    errors.Add(root + "：" + Short(ex.Message, 180));
                    Log.Info("服务端Bot更新安装包准备失败，尝试下一服务端: server=" + root + ", error=" + Short(ex.Message, 220));
                }
            }
            throw new Exception("所有更新服务端均无法准备安装包。" + string.Join("；", errors.ToArray()));
        }

        private static async Task DownloadFromUrlAsync(
            string url,
            string partialPath,
            long expectedSize,
            string channel,
            BotReleaseInfo release,
            IProgress<int> progress,
            CancellationToken cancellationToken)
        {
            HttpResponseMessage response;
            var connectTimeoutSeconds = DownloadConnectTimeoutSeconds;
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                request.Headers.TryAddWithoutValidation("Accept", "application/octet-stream");
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(connectTimeoutSeconds));
                try
                {
                    response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connectTimeout.Token);
                }
                catch (OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested) throw;
                    throw new TimeoutException("连接" + channel + "下载源超过 " + connectTimeoutSeconds + " 秒，已结束当前通道。");
                }
            }

            using (response)
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? expectedSize;
                CurrentDownloadChannel = channel;
                CurrentDownloadPercent = 0;
                CurrentDownloadedBytes = 0;
                CurrentDownloadTotalBytes = Math.Max(0, total);
                if (progress != null && total > 0) progress.Report(0);
                RaiseDownloadStatus(release, channel, 0, 0, CurrentDownloadTotalBytes, false);

                using (var input = await response.Content.ReadAsStreamAsync())
                using (var output = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                {
                    var buffer = new byte[65536];
                    long copied = 0;
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var read = await ReadWithTimeoutAsync(input, buffer, cancellationToken);
                        if (read <= 0) break;
                        await output.WriteAsync(buffer, 0, read, cancellationToken);
                        copied += read;
                        var percent = total > 0 ? (int)Math.Min(99, copied * 100L / total) : 0;
                        CurrentDownloadChannel = channel;
                        CurrentDownloadPercent = percent;
                        CurrentDownloadedBytes = copied;
                        CurrentDownloadTotalBytes = Math.Max(0, total);
                        if (progress != null && total > 0) progress.Report(percent);
                        if (percent == 0 || percent >= _lastReportedDownloadPercent + 2 || copied == total)
                        {
                            _lastReportedDownloadPercent = percent;
                            RaiseDownloadStatus(release, channel, percent, copied, Math.Max(0, total), false);
                        }
                    }
                }
            }
        }

        private static void RaiseDownloadStatus(BotReleaseInfo release, string channel, int percent, long copied, long total, bool complete)
        {
            var normalizedPercent = percent < 0 ? -1 : Math.Max(0, Math.Min(100, percent));
            var result = new BotUpdateCheckResult
            {
                Success = true, CurrentVersion = CurrentVersion,
                UpdateAvailable = release != null && CompareVersions(release.Version, CurrentVersion) > 0,
                Release = release, DownloadChannel = channel ?? string.Empty,
                DownloadPercent = normalizedPercent, DownloadedBytes = Math.Max(0, copied), TotalBytes = Math.Max(0, total)
            };
            result.Message = complete
                ? "安装包下载并校验完成｜通道：" + result.DownloadChannel + "｜100%"
                : (normalizedPercent < 0
                    ? "正在连接下载通道：" + result.DownloadChannel + "；收到首批安装包数据后显示实际百分比。"
                    : "正在下载更新｜通道：" + result.DownloadChannel + "｜" + result.DownloadPercent + "%" + FormatBytesProgress(result.DownloadedBytes, result.TotalBytes));
            RaiseStatus(result);
        }

        private static string FormatBytesProgress(long copied, long total)
        {
            if (copied <= 0 && total <= 0) return string.Empty;
            Func<long, string> fmt = value => value >= 1024L * 1024L
                ? (value / 1024d / 1024d).ToString("0.0") + " MB"
                : (value / 1024d).ToString("0") + " KB";
            return total > 0 ? "｜" + fmt(copied) + "/" + fmt(total) : "｜" + fmt(copied);
        }

        private static async Task<int> ReadWithTimeoutAsync(Stream input, byte[] buffer, CancellationToken cancellationToken)
        {
            var readTask = input.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(DownloadReadTimeoutSeconds), cancellationToken);
            var completed = await Task.WhenAny(readTask, timeoutTask);
            if (!ReferenceEquals(completed, readTask))
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException("下载连接连续 " + DownloadReadTimeoutSeconds + " 秒没有收到数据。");
            }
            return await readTask;
        }
    }
}
