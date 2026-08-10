using Bot.ShopScope;
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
            var serverUrls = GetConfiguredControlPlaneUrls();
            foreach (var serverUrl in serverUrls)
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
                        "服务端更新缓存不可用，继续尝试其他来源: url="
                        + serverUrl
                        + ", error=" + Short(ex.Message, 240));
                }
            }

            var githubRelease = await FetchLatestFromGitHubAsync();
            if (!IsSha256(githubRelease.Sha256))
            {
                await TryBackfillShaFromControlPlaneAsync(
                    githubRelease,
                    serverUrls);
            }
            return githubRelease;
        }

        private static async Task TryBackfillShaFromControlPlaneAsync(
            BotReleaseInfo release,
            IList<string> serverUrls)
        {
            if (release == null || IsSha256(release.Sha256)) return;
            foreach (var serverUrl in serverUrls ?? new List<string>())
            {
                try
                {
                    var cached = await FetchLatestFromControlPlaneAsync(serverUrl);
                    if (cached == null
                        || !string.Equals(
                            cached.Version,
                            release.Version,
                            StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(
                            cached.Tag,
                            release.Tag,
                            StringComparison.OrdinalIgnoreCase)
                        || !IsSha256(cached.Sha256))
                    {
                        continue;
                    }

                    release.Sha256 = cached.Sha256;
                    release.MirrorUrl = cached.MirrorUrl;
                    if (release.PackageSize <= 0)
                        release.PackageSize = cached.PackageSize;
                    if (string.IsNullOrWhiteSpace(release.Commit))
                        release.Commit = cached.Commit;
                    Log.Info(
                        "GitHub清单暂不可用，已从服务端缓存补回SHA-256: version="
                        + release.Version);
                    return;
                }
                catch (Exception ex)
                {
                    Log.Info(
                        "从服务端缓存补充SHA-256失败: url="
                        + serverUrl
                        + ", error=" + Short(ex.Message, 180));
                }
            }
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

            var notes = json.Value<string>("body") ?? string.Empty;
            var release = new BotReleaseInfo
            {
                Version = NormalizeVersion(tag),
                Tag = tag,
                Name = json.Value<string>("name") ?? tag,
                Notes = notes,
                HtmlUrl = json.Value<string>("html_url") ?? ReleasesPage,
                PackageUrl =
                    package.Value<string>("browser_download_url")
                    ?? string.Empty,
                PackageSize = package.Value<long?>("size") ?? 0,
                ManifestUrl = manifest == null
                    ? string.Empty
                    : (manifest.Value<string>("browser_download_url")
                        ?? string.Empty),
                Sha256 = NormalizeGitHubAssetDigest(
                    package.Value<string>("digest")),
                PublishedAt = json.Value<DateTime?>("published_at")
                    ?? DateTime.MinValue,
                Commit = json.Value<string>("target_commitish")
                    ?? string.Empty,
                Source = "github-latest"
            };

            if (!string.IsNullOrWhiteSpace(release.ManifestUrl))
                await FillManifestAsync(release);

            if (!IsSha256(release.Sha256))
            {
                release.Sha256 = ExtractSha256FromReleaseNotes(notes);
                if (IsSha256(release.Sha256))
                {
                    Log.Info(
                        "update.json暂不可用，已从受信任Release说明补回SHA-256: version="
                        + release.Version);
                }
            }

            ValidateRelease(release, false);
            return release;
        }

        private static async Task FillManifestAsync(BotReleaseInfo release)
        {
            var fallbackSha = release == null
                ? string.Empty
                : (release.Sha256 ?? string.Empty).Trim().ToLowerInvariant();
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
                    throw new InvalidDataException(
                        "更新清单版本与 Release 标签不一致。");
                }

                var manifestSha =
                    (json.Value<string>("sha256") ?? string.Empty)
                    .Trim()
                    .ToLowerInvariant();
                if (!IsSha256(manifestSha))
                    throw new Exception("update.json 缺少有效 SHA-256。");
                if (IsSha256(fallbackSha)
                    && !string.Equals(
                        fallbackSha,
                        manifestSha,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "GitHub安装包摘要与 update.json SHA-256 不一致。");
                }

                release.Sha256 = manifestSha;
                release.Commit =
                    (json.Value<string>("commit")
                        ?? release.Commit
                        ?? string.Empty)
                    .Trim();
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Do not erase an already-valid GitHub asset digest. A transient request to
                // update.json must not turn a verifiable release into a manual-only update.
                release.Sha256 = IsSha256(fallbackSha)
                    ? fallbackSha
                    : string.Empty;
                Log.Info(
                    "读取update.json失败，保留其他可信SHA来源并继续: version="
                    + release.Version
                    + ", error=" + Short(ex.Message, 220));
            }
        }

        private static string NormalizeGitHubAssetDigest(string digest)
        {
            digest = (digest ?? string.Empty).Trim();
            const string prefix = "sha256:";
            if (digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                digest = digest.Substring(prefix.Length).Trim();
            return IsSha256(digest)
                ? digest.ToLowerInvariant()
                : string.Empty;
        }

        private static string ExtractSha256FromReleaseNotes(string notes)
        {
            notes = notes ?? string.Empty;
            var marker = notes.IndexOf(
                "安装包 SHA-256",
                StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
                marker = notes.IndexOf("SHA-256", StringComparison.OrdinalIgnoreCase);
            if (marker < 0) return string.Empty;

            for (var i = marker; i <= notes.Length - 64; i++)
            {
                var candidate = notes.Substring(i, 64);
                if (!IsSha256(candidate)) continue;
                var beforeIsHex = i > 0 && IsHex(notes[i - 1]);
                var after = i + 64;
                var afterIsHex = after < notes.Length && IsHex(notes[after]);
                if (!beforeIsHex && !afterIsHex)
                    return candidate.ToLowerInvariant();
            }
            return string.Empty;
        }

        private static bool IsHex(char ch)
        {
            return (ch >= '0' && ch <= '9')
                || (ch >= 'a' && ch <= 'f')
                || (ch >= 'A' && ch <= 'F');
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
            return value.All(IsHex);
        }

        private static DateTime ParseDateTime(string value)
        {
            DateTime parsed;
            return DateTime.TryParse(value, out parsed)
                ? parsed
                : DateTime.MinValue;
        }

        private static IList<string> GetConfiguredControlPlaneUrls()
        {
            var urls = new List<string>();
            Action<string> add = value =>
            {
                var normalized = ShopControlPlaneConnectionStore.NormalizeUrl(value);
                if (!Uri.IsWellFormedUriString(normalized, UriKind.Absolute)) return;
                if (!urls.Any(x => string.Equals(
                    x,
                    normalized,
                    StringComparison.OrdinalIgnoreCase)))
                {
                    urls.Add(normalized);
                }
            };

            try
            {
                var paths = new ShopScopedPathProvider();
                var current = ShopSettingsScope.Current;
                if (current != null)
                {
                    add(new ShopControlPlaneConnectionStore(current, paths).GetServerUrl());
                }

                foreach (var profile in new ShopProfileStore(paths)
                    .GetAll()
                    .Where(x => x != null)
                    .OrderByDescending(x => x.LastSeenAtUtc))
                {
                    try
                    {
                        add(new ShopControlPlaneConnectionStore(
                            profile.ToContext(),
                            paths).GetServerUrl());
                    }
                    catch (Exception ex)
                    {
                        Log.Info(
                            "读取店铺更新服务地址失败: shop="
                            + (profile.ShopKey ?? string.Empty)
                            + ", error=" + Short(ex.Message, 160));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Info(
                    "读取ShopKey更新服务地址失败，将继续检查旧全局地址: "
                    + Short(ex.Message, 180));
            }

            try
            {
                add(ShopControlPlaneConnectionStore.GetLegacyGlobalServerUrl());
            }
            catch (Exception ex)
            {
                Log.Info(
                    "读取旧全局控制面地址失败: "
                    + Short(ex.Message, 160));
            }
            return urls;
        }
    }
}
