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
                Log.Info(
                    "已有Bot更新下载任务正在进行，当前请求等待复用结果: version="
                    + release.Version);
            }

            await DownloadGate.WaitAsync(cancellationToken);
            try
            {
                var sources = new List<KeyValuePair<string, string>>();
                AddDownloadSource(sources, "腾讯云控制台服务器", release.MirrorUrl);
                AddDownloadSource(sources, "GitHub", release.PackageUrl);
                if (sources.Count == 0)
                    throw new Exception("发布版本缺少安装包下载地址。");

                var directory = Path.Combine(
                    GetUpdateRoot(),
                    SanitizeFileName(release.Version));
                Directory.CreateDirectory(directory);
                var target = Path.Combine(directory, PackageAssetName);
                if (File.Exists(target)
                    && HashFile(target).Equals(
                        release.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (progress != null) progress.Report(100);
                    if (waitingForExistingDownload)
                    {
                        Log.Info(
                            "Bot更新下载任务已复用已完成安装包: version="
                            + release.Version);
                    }
                    return target;
                }

                var errors = new List<string>();
                foreach (var source in sources)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var partial = target + ".partial";
                    try
                    {
                        if (File.Exists(partial)) File.Delete(partial);
                        if (progress != null) progress.Report(0);
                        await DownloadFromUrlAsync(
                            source.Value,
                            partial,
                            release.PackageSize,
                            progress,
                            cancellationToken);

                        var actual = HashFile(partial);
                        if (!actual.Equals(
                            release.Sha256,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            throw new Exception(
                                "安装包 SHA-256 不一致，期望 "
                                + release.Sha256 + "，实际 " + actual);
                        }

                        if (File.Exists(target)) File.Delete(target);
                        File.Move(partial, target);
                        if (progress != null) progress.Report(100);
                        Log.Info(
                            "Bot更新安装包下载成功: version=" + release.Version
                            + ", source=" + source.Key);
                        return target;
                    }
                    catch (OperationCanceledException ex)
                    {
                        try { if (File.Exists(partial)) File.Delete(partial); } catch { }
                        if (cancellationToken.IsCancellationRequested)
                        {
                            Log.Info(
                                "Bot更新下载被用户取消: source=" + source.Key
                                + ", version=" + release.Version);
                            throw;
                        }

                        var message = "下载连接被远端或网络中断";
                        errors.Add(source.Key + "：" + message);
                        Log.Info(
                            "Bot更新下载源发生非用户取消，自动切换下一来源: source=" + source.Key
                            + ", version=" + release.Version
                            + ", error=" + Short(ex.Message, 240));
                    }
                    catch (Exception ex)
                    {
                        try { if (File.Exists(partial)) File.Delete(partial); } catch { }
                        errors.Add(source.Key + "：" + Short(ex.Message, 140));
                        Log.Info(
                            "Bot更新下载源失败，准备尝试下一来源: source=" + source.Key
                            + ", version=" + release.Version
                            + ", error=" + Short(ex.Message, 240));
                    }
                }

                throw new Exception(
                    "腾讯云控制台服务器与 GitHub 均下载失败。"
                    + string.Join("；", errors.ToArray()));
            }
            finally
            {
                DownloadGate.Release();
            }
        }

        private static void AddDownloadSource(
            IList<KeyValuePair<string, string>> sources,
            string name,
            string url)
        {
            url = (url ?? string.Empty).Trim();
            if (url.Length == 0) return;
            if (!Uri.IsWellFormedUriString(url, UriKind.Absolute)) return;
            if (sources.Any(x => string.Equals(
                x.Value,
                url,
                StringComparison.OrdinalIgnoreCase))) return;
            sources.Add(new KeyValuePair<string, string>(name, url));
        }

        private static async Task DownloadFromUrlAsync(
            string url,
            string partialPath,
            long expectedSize,
            IProgress<int> progress,
            CancellationToken cancellationToken)
        {
            HttpResponseMessage response;
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            using (var connectTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                request.Headers.TryAddWithoutValidation(
                    "Accept",
                    "application/octet-stream");
                connectTimeout.CancelAfter(
                    TimeSpan.FromSeconds(DownloadConnectTimeoutSeconds));
                try
                {
                    response = await Http.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        connectTimeout.Token);
                }
                catch (OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested) throw;
                    throw new TimeoutException(
                        "连接下载源超过 "
                        + DownloadConnectTimeoutSeconds
                        + " 秒，已自动切换备用下载源。");
                }
            }

            using (response)
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? expectedSize;
                using (var input = await response.Content.ReadAsStreamAsync())
                using (var output = new FileStream(
                    partialPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    65536,
                    true))
                {
                    var buffer = new byte[65536];
                    long copied = 0;
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var read = await ReadWithTimeoutAsync(
                            input,
                            buffer,
                            cancellationToken);
                        if (read <= 0) break;
                        await output.WriteAsync(
                            buffer,
                            0,
                            read,
                            cancellationToken);
                        copied += read;
                        if (progress != null && total > 0)
                        {
                            progress.Report(
                                (int)Math.Min(99, copied * 100L / total));
                        }
                    }
                }
            }
        }

        private static async Task<int> ReadWithTimeoutAsync(
            Stream input,
            byte[] buffer,
            CancellationToken cancellationToken)
        {
            var readTask = input.ReadAsync(
                buffer,
                0,
                buffer.Length,
                cancellationToken);
            var timeoutTask = Task.Delay(
                TimeSpan.FromSeconds(DownloadReadTimeoutSeconds),
                cancellationToken);
            var completed = await Task.WhenAny(readTask, timeoutTask);
            if (!ReferenceEquals(completed, readTask))
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException(
                    "下载连接连续 "
                    + DownloadReadTimeoutSeconds
                    + " 秒没有收到数据。");
            }
            return await readTask;
        }
    }
}
