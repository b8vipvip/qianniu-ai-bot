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
        private static async Task<BotReleaseInfo> FetchLatestReleaseAsync()
        {
            var serverUrl = GetConfiguredControlPlaneUrl();
            if (!string.IsNullOrWhiteSpace(serverUrl))
            {
                try
                {
                    var serviceRelease =
                        await FetchLatestFromControlPlaneAsync(serverUrl);
                    if (serviceRelease != null) return serviceRelease;
                }
                catch (Exception ex)
                {
                    Log.Info(
                        "服务端更新缓存不可用，切换 GitHub latest: "
                        + Short(ex.Message, 240));
                }
            }

            return await FetchLatestFromGitHubAsync();
        }

        private static async Task<BotReleaseInfo>
            FetchLatestFromControlPlaneAsync(string serverUrl)
        {
            var url = serverUrl.TrimEnd('/') + ServiceLatestPath;
            var json = JObject.Parse(
                await GetTextAsync(
                    url,
                    ServiceMetadataTimeoutSeconds,
                    false));
            var release = new BotReleaseInfo
            {
                Version = NormalizeVersion(
                    json.Value<string>("version") ?? string.Empty),
                Tag = (json.Value<string>("tag") ?? string.Empty).Trim(),
                Name = json.Value<string>("name")
                    ?? json.Value<string>("tag")
                    ?? string.Empty,
                Notes = json.Value<string>("notes") ?? string.Empty,
                HtmlUrl = json.Value<string>("html_url") ?? ReleasesPage,
                PackageUrl =
                    (json.Value<string>("download_url") ?? string.Empty).Trim(),
                MirrorUrl =
                    (json.Value<string>("mirror_url") ?? string.Empty).Trim(),
                Sha256 =
                    (json.Value<string>("sha256") ?? string.Empty)
                    .Trim()
                    .ToLowerInvariant(),
                PackageSize = json.Value<long?>("size") ?? 0,
                PublishedAt = ParseDateTime(
                    json.Value<string>("published_at")),
                Commit =
                    (json.Value<string>("commit") ?? string.Empty).Trim(),
                Source = "control-plane-cache"
            };
            ValidateRelease(release, true);
            return release;
        }

        private static async Task<BotReleaseInfo>
            FetchLatestFromGitHubAsync()
        {
            var json = JObject.Parse(
                await GetTextAsync(
                    GitHubLatestReleaseApi,
                    GitHubMetadataTimeoutSeconds,
                    true));
            if (json.Value<bool?>("draft") == true
                || json.Value<bool?>("prerelease") == true)
            {
                throw new Exception("GitHub latest Release 不是稳定版本。");
            }

            var tag = (json.Value<string>("tag_name") ?? string.Empty).Trim();
            if (!tag.StartsWith(
                "bot-v",
                StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception("GitHub latest Release 不是 bot-v* 正式版本。");
            }

            var assets = json["assets"] as JArray;
            var package = assets == null
                ? null
                : assets.OfType<JObject>().FirstOrDefault(
                    x => string.Equals(
                        x.Value<string>("name"),
                        PackageAssetName,
                        StringComparison.OrdinalIgnoreCase));
            var manifest = assets == null
                ? null
                : assets.OfType<JObject>().FirstOrDefault(
                    x => string.Equals(
                        x.Value<string>("name"),
                        ManifestAssetName,
                        StringComparison.OrdinalIgnoreCase));
            if (package == null)
                throw new Exception("GitHub 正式版本缺少安装包。");

            var release = new BotReleaseInfo
            {
                Version = NormalizeVersion(tag),
                Tag = tag,
                Name = json.Value<string>("name") ?? tag,
                Notes = json.Value<string>("body") ?? string.Empty,
                HtmlUrl = json.Value<string>("html_url") ?? ReleasesPage,
                PackageUrl =
                    package.Value<string>("browser_download_url")
                    ?? string.Empty,
                PackageSize = package.Value<long?>("size") ?? 0,
                ManifestUrl = manifest == null
                    ? string.Empty
                    : (manifest.Value<string>("browser_download_url")
                        ?? string.Empty),
                PublishedAt = json.Value<DateTime?>("published_at")
                    ?? DateTime.MinValue,
                Commit = json.Value<string>("target_commitish")
                    ?? string.Empty,
                Source = "github-latest"
            };
            if (!string.IsNullOrWhiteSpace(release.ManifestUrl))
                await FillManifestAsync(release);
            ValidateRelease(release, false);
            return release;
        }

        private static async Task FillManifestAsync(BotReleaseInfo release)
        {
            try
            {
                var json = JObject.Parse(
                    await GetTextAsync(
                        release.ManifestUrl,
                        ManifestTimeoutSeconds,
                        false));
                var manifestVersion = NormalizeVersion(
                    json.Value<string>("version") ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(manifestVersion)
                    && !string.Equals(
                        manifestVersion,
                        release.Version,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception(
                        "更新清单版本与 Release 标签不一致。");
                }
                release.Sha256 =
                    (json.Value<string>("sha256") ?? string.Empty)
                    .Trim()
                    .ToLowerInvariant();
                release.Commit =
                    (json.Value<string>("commit")
                        ?? release.Commit
                        ?? string.Empty)
                    .Trim();
            }
            catch (Exception ex)
            {
                release.Sha256 = string.Empty;
                Log.Info(
                    "读取更新SHA清单失败，仍可提示但禁止自动安装: version="
                    + release.Version
                    + ", error=" + Short(ex.Message, 220));
            }
        }

        private static async Task<string> GetTextAsync(
            string url,
            int timeoutSeconds,
            bool githubApi)
        {
            using (var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(timeoutSeconds)))
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.TryAddWithoutValidation(
                    "Accept",
                    githubApi
                        ? "application/vnd.github+json"
                        : "application/json");
                using (var response = await Http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseContentRead,
                    timeout.Token))
                {
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsStringAsync();
                }
            }
        }

        private static void ValidateRelease(
            BotReleaseInfo release,
            bool requireSha)
        {
            if (release == null)
                throw new Exception("更新服务没有返回版本信息。");
            if (string.IsNullOrWhiteSpace(release.Version)
                || !IsValidVersion(release.Version))
                throw new Exception("更新版本号无效。");
            if (string.IsNullOrWhiteSpace(release.Tag)
                || !release.Tag.StartsWith(
                    "bot-v",
                    StringComparison.OrdinalIgnoreCase))
                throw new Exception("更新标签不是 bot-v* 正式版本。");
            if (string.IsNullOrWhiteSpace(release.PackageUrl)
                || !Uri.IsWellFormedUriString(
                    release.PackageUrl,
                    UriKind.Absolute))
                throw new Exception("安装包下载地址无效。");
            if (requireSha
                && !IsSha256(release.Sha256))
                throw new Exception("服务端更新缓存缺少有效 SHA-256。");
            if (!string.IsNullOrWhiteSpace(release.Sha256)
                && !IsSha256(release.Sha256))
                throw new Exception("更新 SHA-256 格式无效。");
        }

        private static bool IsSha256(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length != 64) return false;
            return value.All(
                ch => (ch >= '0' && ch <= '9')
                    || (ch >= 'a' && ch <= 'f')
                    || (ch >= 'A' && ch <= 'F'));
        }

        private static DateTime ParseDateTime(string value)
        {
            DateTime parsed;
            return DateTime.TryParse(value, out parsed)
                ? parsed
                : DateTime.MinValue;
        }

        private static string GetConfiguredControlPlaneUrl()
        {
            try
            {
                var url = PersistentParams.GetParam2Key(
                    ControlPlaneUrlKey,
                    ControlPlaneScope,
                    string.Empty);
                url = (url ?? string.Empty).Trim().TrimEnd('/');
                if (url.EndsWith(
                    "/v1",
                    StringComparison.OrdinalIgnoreCase))
                {
                    url = url.Substring(0, url.Length - 3).TrimEnd('/');
                }
                return Uri.IsWellFormedUriString(url, UriKind.Absolute)
                    ? url
                    : string.Empty;
            }
            catch (Exception ex)
            {
                Log.Info(
                    "读取控制面地址失败，更新检查将直连 GitHub: "
                    + Short(ex.Message, 180));
                return string.Empty;
            }
        }
    }
}
