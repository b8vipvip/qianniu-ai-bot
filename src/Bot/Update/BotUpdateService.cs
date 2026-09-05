using BotLib;
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
    internal sealed class BotUpdateSettings
    {
        public bool AutoCheck { get; set; }
        public bool NotifyPopup { get; set; }
        public bool AutoDownload { get; set; }
        public int CheckIntervalHours { get; set; }
        public string SkippedVersion { get; set; }
        public string LastNotifiedVersion { get; set; }
        public string LastNotifiedAt { get; set; }
        public string LastCheckAt { get; set; }

        public BotUpdateSettings()
        {
            AutoCheck = true;
            NotifyPopup = true;
            AutoDownload = false;
            CheckIntervalHours = 6;
            SkippedVersion = string.Empty;
            LastNotifiedVersion = string.Empty;
            LastNotifiedAt = string.Empty;
            LastCheckAt = string.Empty;
        }
    }

    internal sealed class BotReleaseInfo
    {
        public string Version { get; set; }
        public string Tag { get; set; }
        public string Name { get; set; }
        public string Notes { get; set; }
        public string HtmlUrl { get; set; }
        public string PackageUrl { get; set; }
        public string ManifestUrl { get; set; }
        public string Sha256 { get; set; }
        public long PackageSize { get; set; }
        public DateTime PublishedAt { get; set; }
        public string Commit { get; set; }

        public BotReleaseInfo()
        {
            Version = string.Empty;
            Tag = string.Empty;
            Name = string.Empty;
            Notes = string.Empty;
            HtmlUrl = string.Empty;
            PackageUrl = string.Empty;
            ManifestUrl = string.Empty;
            Sha256 = string.Empty;
            Commit = string.Empty;
        }
    }

    internal sealed class BotUpdateCheckResult
    {
        public bool Success { get; set; }
        public bool UpdateAvailable { get; set; }
        public string CurrentVersion { get; set; }
        public string Message { get; set; }
        public BotReleaseInfo Release { get; set; }

        public BotUpdateCheckResult()
        {
            CurrentVersion = string.Empty;
            Message = string.Empty;
        }
    }

    internal static class BotUpdateService
    {
        private const string ReleasesApi = "https://api.github.com/repos/b8vipvip/qnbot/releases?per_page=20";
        private const string ReleasesPage = "https://github.com/b8vipvip/qnbot/releases";
        private const string PackageAssetName = "qianniu-bot-x64.zip";
        private const string ManifestAssetName = "update.json";
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
            RestartTimer();
            Log.Info("Bot自动更新服务已启动: version=" + CurrentVersion
                + ", autoCheck=" + GetSettings().AutoCheck
                + ", intervalHours=" + GetSettings().CheckIntervalHours);
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
            RestartTimer();
        }

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
                    Message = "正在连接 GitHub 检查新版本..."
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
                var result = new BotUpdateCheckResult
                {
                    Success = true,
                    CurrentVersion = current,
                    UpdateAvailable = available,
                    Release = release,
                    Message = available
                        ? "发现新版本 " + release.Version
                        : "当前已是最新版本 " + current
                };

                if (available)
                {
                    var settings = GetSettings();
                    if (settings.AutoDownload && !string.IsNullOrWhiteSpace(release.Sha256))
                    {
                        try
                        {
                            await DownloadPackageAsync(release, null, CancellationToken.None);
                            result.Message += "，安装包已自动下载。";
                        }
                        catch (Exception ex)
                        {
                            result.Message += "，自动下载失败：" + ex.Message;
                            Log.Info("自动下载Bot更新失败: version=" + release.Version + ", error=" + ex.Message);
                        }
                    }
                }

                RaiseStatus(result);
                if (!interactive && available) MaybeShowBackgroundPrompt(release);
                return result;
            }
            catch (Exception ex)
            {
                var failed = new BotUpdateCheckResult
                {
                    Success = false,
                    CurrentVersion = CurrentVersion,
                    Message = "检查更新失败：" + Short(ex.Message, 260)
                };
                RaiseStatus(failed);
                if (interactive) Log.Info(failed.Message);
                else Log.Info("后台检查更新失败，已静默跳过: " + ex.Message);
                return failed;
            }
            finally
            {
                Interlocked.Exchange(ref _checking, 0);
            }
        }

        public static async Task<string> DownloadPackageAsync(
            BotReleaseInfo release,
            IProgress<int> progress,
            CancellationToken cancellationToken)
        {
            if (release == null) throw new ArgumentNullException("release");
            if (string.IsNullOrWhiteSpace(release.PackageUrl)) throw new Exception("发布版本缺少安装包下载地址。 ");
            if (string.IsNullOrWhiteSpace(release.Sha256)) throw new Exception("发布版本缺少 SHA-256 校验信息，已拒绝自动安装。 ");

            var directory = Path.Combine(GetUpdateRoot(), SanitizeFileName(release.Version));
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, PackageAssetName);
            if (File.Exists(target) && HashFile(target).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                if (progress != null) progress.Report(100);
                return target;
            }

            var partial = target + ".partial";
            if (File.Exists(partial)) File.Delete(partial);
            using (var request = new HttpRequestMessage(HttpMethod.Get, release.PackageUrl))
            using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? release.PackageSize;
                using (var input = await response.Content.ReadAsStreamAsync())
                using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                {
                    var buffer = new byte[65536];
                    long copied = 0;
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                        if (read <= 0) break;
                        await output.WriteAsync(buffer, 0, read, cancellationToken);
                        copied += read;
                        if (progress != null && total > 0)
                        {
                            progress.Report((int)Math.Min(99, copied * 100L / total));
                        }
                    }
                }
            }

            var actual = HashFile(partial);
            if (!actual.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(partial); } catch { }
                throw new Exception("安装包校验失败，已删除文件。期望 " + release.Sha256 + "，实际 " + actual + "。 ");
            }
            if (File.Exists(target)) File.Delete(target);
            File.Move(partial, target);
            if (progress != null) progress.Report(100);
            return target;
        }

        public static bool IsPackageReady(BotReleaseInfo release)
        {
            if (release == null || string.IsNullOrWhiteSpace(release.Sha256)) return false;
            try
            {
                var path = Path.Combine(GetUpdateRoot(), SanitizeFileName(release.Version), PackageAssetName);
                return File.Exists(path) && HashFile(path).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase);
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
                window.Closed += (s, e) => Interlocked.Exchange(ref _promptOpen, 0);
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
            OpenUrl(LatestRelease == null || string.IsNullOrWhiteSpace(LatestRelease.HtmlUrl)
                ? ReleasesPage
                : LatestRelease.HtmlUrl);
        }

        public static void LaunchInstaller(string packagePath, BotReleaseInfo release)
        {
            if (release == null) throw new ArgumentNullException("release");
            if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath)) throw new FileNotFoundException("更新安装包不存在。", packagePath);
            if (!HashFile(packagePath).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception("安装前 SHA-256 校验失败，已拒绝执行更新。 ");
            }

            var sourceScript = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BotAutoUpdater.ps1");
            if (!File.Exists(sourceScript)) throw new FileNotFoundException("自动更新程序 BotAutoUpdater.ps1 缺失。", sourceScript);
            var tempScript = Path.Combine(Path.GetTempPath(), "QianniuAiBotUpdater-" + Guid.NewGuid().ToString("N") + ".ps1");
            File.Copy(sourceScript, tempScript, true);
            var installRoot = GetInstallRoot();
            var arguments = "-NoProfile -ExecutionPolicy Bypass -File " + QuoteArgument(tempScript)
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
            if (process == null) throw new Exception("无法启动自动更新程序。 ");
            Log.Info("已启动Bot自动更新程序: version=" + release.Version + ", package=" + packagePath);
            if (Application.Current != null)
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(() => Application.Current.Shutdown()));
            }
        }

        private static void MaybeShowBackgroundPrompt(BotReleaseInfo release)
        {
            var settings = GetSettings();
            if (!settings.NotifyPopup) return;
            if (string.Equals(settings.SkippedVersion, release.Version, StringComparison.OrdinalIgnoreCase)) return;
            DateTime lastAt;
            if (string.Equals(settings.LastNotifiedVersion, release.Version, StringComparison.OrdinalIgnoreCase)
                && DateTime.TryParse(settings.LastNotifiedAt, out lastAt)
                && lastAt >= DateTime.Now.AddHours(-24))
            {
                return;
            }

            settings.LastNotifiedVersion = release.Version;
            settings.LastNotifiedAt = DateTime.Now.ToString("o");
            SaveSettings(settings);
            if (Application.Current == null) return;
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                Window owner = null;
                try { owner = Application.Current.MainWindow; } catch { }
                ShowUpdatePrompt(release, owner);
            }));
        }

        private static async Task<BotReleaseInfo> FetchLatestReleaseAsync()
        {
            string body;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            using (var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApi))
            using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token))
            {
                response.EnsureSuccessStatusCode();
                body = await response.Content.ReadAsStringAsync();
            }

            var releases = JArray.Parse(body);
            var candidates = new List<BotReleaseInfo>();
            foreach (var token in releases.OfType<JObject>())
            {
                if (token.Value<bool?>("draft") == true || token.Value<bool?>("prerelease") == true) continue;
                var tag = (token.Value<string>("tag_name") ?? string.Empty).Trim();
                if (!tag.StartsWith("bot-v", StringComparison.OrdinalIgnoreCase)) continue;
                var version = NormalizeVersion(tag);
                if (!IsValidVersion(version)) continue;
                var assets = token["assets"] as JArray;
                var package = assets == null ? null : assets.OfType<JObject>()
                    .FirstOrDefault(x => string.Equals(x.Value<string>("name"), PackageAssetName, StringComparison.OrdinalIgnoreCase));
                if (package == null) continue;
                var manifest = assets == null ? null : assets.OfType<JObject>()
                    .FirstOrDefault(x => string.Equals(x.Value<string>("name"), ManifestAssetName, StringComparison.OrdinalIgnoreCase));
                var release = new BotReleaseInfo
                {
                    Version = version,
                    Tag = tag,
                    Name = token.Value<string>("name") ?? tag,
                    Notes = token.Value<string>("body") ?? string.Empty,
                    HtmlUrl = token.Value<string>("html_url") ?? ReleasesPage,
                    PackageUrl = package.Value<string>("browser_download_url") ?? string.Empty,
                    PackageSize = package.Value<long?>("size") ?? 0,
                    ManifestUrl = manifest == null ? string.Empty : (manifest.Value<string>("browser_download_url") ?? string.Empty),
                    PublishedAt = token.Value<DateTime?>("published_at") ?? DateTime.MinValue,
                    Commit = token.Value<string>("target_commitish") ?? string.Empty
                };
                if (!string.IsNullOrWhiteSpace(release.ManifestUrl))
                {
                    await FillManifestAsync(release);
                }
                candidates.Add(release);
            }
            return candidates.OrderByDescending(x => ParseVersion(x.Version)).FirstOrDefault();
        }

        private static async Task FillManifestAsync(BotReleaseInfo release)
        {
            try
            {
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                using (var request = new HttpRequestMessage(HttpMethod.Get, release.ManifestUrl))
                using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token))
                {
                    response.EnsureSuccessStatusCode();
                    var json = JObject.Parse(await response.Content.ReadAsStringAsync());
                    var manifestVersion = NormalizeVersion(json.Value<string>("version") ?? string.Empty);
                    if (!string.IsNullOrWhiteSpace(manifestVersion)
                        && !string.Equals(manifestVersion, release.Version, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new Exception("更新清单版本与Release标签不一致。 ");
                    }
                    release.Sha256 = (json.Value<string>("sha256") ?? string.Empty).Trim().ToLowerInvariant();
                    release.Commit = (json.Value<string>("commit") ?? release.Commit ?? string.Empty).Trim();
                }
            }
            catch (Exception ex)
            {
                release.Sha256 = string.Empty;
                Log.Info("读取更新SHA清单失败，仍可提示但禁止自动安装: version=" + release.Version + ", error=" + ex.Message);
            }
        }

        private static void RestartTimer()
        {
            try
            {
                if (_timer != null)
                {
                    _timer.Dispose();
                    _timer = null;
                }
                var settings = GetSettings();
                if (!settings.AutoCheck) return;
                _timer = new Timer(async state => await TimerTickAsync(), null, TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(30));
            }
            catch (Exception ex)
            {
                Log.Info("启动自动更新定时器失败: " + ex.Message);
            }
        }

        private static async Task TimerTickAsync()
        {
            try
            {
                var settings = GetSettings();
                if (!settings.AutoCheck) return;
                DateTime lastCheck;
                if (DateTime.TryParse(settings.LastCheckAt, out lastCheck)
                    && lastCheck.AddHours(settings.CheckIntervalHours) > DateTime.Now)
                {
                    return;
                }
                await CheckNowAsync(false);
            }
            catch (Exception ex)
            {
                Log.Info("自动检查更新任务异常，已忽略: " + ex.Message);
            }
        }

        private static void UpdateLastCheckTime()
        {
            var settings = GetSettings();
            settings.LastCheckAt = DateTime.Now.ToString("o");
            SaveSettings(settings);
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
                if (!File.Exists(path)) return new BotUpdateSettings();
                return NormalizeSettings(JsonConvert.DeserializeObject<BotUpdateSettings>(File.ReadAllText(path, Encoding.UTF8)));
            }
            catch (Exception ex)
            {
                Log.Info("读取Bot更新设置失败，使用默认值: " + ex.Message);
                return new BotUpdateSettings();
            }
        }

        private static void SaveSettingsInternal(BotUpdateSettings settings)
        {
            var path = GetSettingsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(settings, Formatting.Indented), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        private static BotUpdateSettings NormalizeSettings(BotUpdateSettings settings)
        {
            settings = settings ?? new BotUpdateSettings();
            settings.CheckIntervalHours = Math.Max(1, Math.Min(168, settings.CheckIntervalHours <= 0 ? 6 : settings.CheckIntervalHours));
            settings.SkippedVersion = (settings.SkippedVersion ?? string.Empty).Trim();
            settings.LastNotifiedVersion = (settings.LastNotifiedVersion ?? string.Empty).Trim();
            settings.LastNotifiedAt = (settings.LastNotifiedAt ?? string.Empty).Trim();
            settings.LastCheckAt = (settings.LastCheckAt ?? string.Empty).Trim();
            return settings;
        }

        private static BotUpdateSettings CloneSettings(BotUpdateSettings settings)
        {
            settings = settings ?? new BotUpdateSettings();
            return new BotUpdateSettings
            {
                AutoCheck = settings.AutoCheck,
                NotifyPopup = settings.NotifyPopup,
                AutoDownload = settings.AutoDownload,
                CheckIntervalHours = settings.CheckIntervalHours,
                SkippedVersion = settings.SkippedVersion,
                LastNotifiedVersion = settings.LastNotifiedVersion,
                LastNotifiedAt = settings.LastNotifiedAt,
                LastCheckAt = settings.LastCheckAt
            };
        }

        private static string ResolveCurrentVersion()
        {
            try
            {
                var installRoot = GetInstallRoot();
                var candidates = new[]
                {
                    Path.Combine(installRoot, "release-info.json"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "release-info.json")
                };
                foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!File.Exists(path)) continue;
                    var json = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                    var version = NormalizeVersion(json.Value<string>("version") ?? string.Empty);
                    if (IsValidVersion(version)) return version;
                }
            }
            catch
            {
            }
            var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;
            return assemblyVersion == null ? "1.0.0" : assemblyVersion.ToString(3);
        }

        private static string GetInstallRoot()
        {
            var baseDir = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            return string.Equals(Path.GetFileName(baseDir), "Bin", StringComparison.OrdinalIgnoreCase)
                ? Directory.GetParent(baseDir).FullName
                : baseDir;
        }

        private static string GetSettingsPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QianniuAiBot", "data", "bot-update-settings.json");
        }

        private static string GetUpdateRoot()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QianniuAiBot", "updates");
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(5);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("QianniuAiBot-Updater/1.1");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            return client;
        }

        private static int CompareVersions(string left, string right)
        {
            return ParseVersion(left).CompareTo(ParseVersion(right));
        }

        private static Version ParseVersion(string value)
        {
            value = NormalizeVersion(value);
            Version parsed;
            return Version.TryParse(value, out parsed) ? parsed : new Version(0, 0, 0, 0);
        }

        private static bool IsValidVersion(string value)
        {
            Version ignored;
            return Version.TryParse(NormalizeVersion(value), out ignored);
        }

        private static string NormalizeVersion(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.StartsWith("bot-v", StringComparison.OrdinalIgnoreCase)) value = value.Substring(5);
            else if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase)) value = value.Substring(1);
            var dash = value.IndexOf('-');
            if (dash >= 0) value = value.Substring(0, dash);
            return value.Trim();
        }

        private static string HashFile(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static string SanitizeFileName(string value)
        {
            value = (value ?? "unknown").Trim();
            foreach (var ch in Path.GetInvalidFileNameChars()) value = value.Replace(ch, '_');
            return value.Length == 0 ? "unknown" : value;
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Info("打开更新页面失败: " + ex.Message);
            }
        }

        private static string Short(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }

    internal sealed class BotUpdatePromptWindow : Window
    {
        private readonly BotReleaseInfo _release;
        private readonly TextBlock _status;
        private readonly ProgressBar _progress;
        private readonly Button _installButton;
        private readonly Button _laterButton;
        private readonly Button _skipButton;
        private CancellationTokenSource _downloadCts;

        public BotUpdatePromptWindow(BotReleaseInfo release)
        {
            _release = release;
            Title = "发现 Bot 新版本";
            Width = 620;
            Height = 520;
            MinWidth = 520;
            MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;

            var root = new Grid { Margin = new Thickness(18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = root;

            var title = new TextBlock
            {
                Text = "Qianniu AI Bot 有新版本",
                FontSize = 22,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(30, 64, 175))
            };
            Grid.SetRow(title, 0);
            root.Children.Add(title);

            var versions = new TextBlock
            {
                Text = "当前版本：" + BotUpdateService.CurrentVersion + "    最新版本：" + release.Version
                    + (release.PublishedAt == DateTime.MinValue ? string.Empty : "    发布时间：" + release.PublishedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")),
                Margin = new Thickness(0, 8, 0, 10),
                Foreground = Brushes.DimGray
            };
            Grid.SetRow(versions, 1);
            root.Children.Add(versions);

            var notes = new TextBox
            {
                Text = string.IsNullOrWhiteSpace(release.Notes) ? "本版本已通过 GitHub Actions 完整构建和校验。" : release.Notes,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(10),
                Background = new SolidColorBrush(Color.FromRgb(248, 250, 252))
            };
            Grid.SetRow(notes, 2);
            root.Children.Add(notes);

            var statusPanel = new StackPanel { Margin = new Thickness(0, 12, 0, 8) };
            _status = new TextBlock
            {
                Text = BotUpdateService.IsPackageReady(release)
                    ? "安装包已下载并通过 SHA-256 校验，可以立即更新。"
                    : (string.IsNullOrWhiteSpace(release.Sha256)
                        ? "该版本缺少SHA-256清单，只能打开发布页面手动下载。"
                        : "点击“立即更新”后自动下载、校验、备份、安装并重启。"),
                TextWrapping = TextWrapping.Wrap
            };
            statusPanel.Children.Add(_status);
            _progress = new ProgressBar
            {
                Height = 8,
                Minimum = 0,
                Maximum = 100,
                Margin = new Thickness(0, 7, 0, 0),
                Visibility = Visibility.Collapsed
            };
            statusPanel.Children.Add(_progress);
            Grid.SetRow(statusPanel, 3);
            root.Children.Add(statusPanel);

            var buttons = new DockPanel { LastChildFill = false };
            var open = new Button { Content = "查看发布页面", MinWidth = 105, Height = 32, Margin = new Thickness(0, 0, 8, 0) };
            open.Click += (s, e) => BotUpdateService.OpenReleasesPage();
            DockPanel.SetDock(open, Dock.Left);
            buttons.Children.Add(open);

            _installButton = new Button
            {
                Content = BotUpdateService.IsPackageReady(release) ? "立即安装并重启" : "立即更新",
                MinWidth = 120,
                Height = 32,
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = !string.IsNullOrWhiteSpace(release.Sha256),
                Background = new SolidColorBrush(Color.FromRgb(37, 99, 235)),
                Foreground = Brushes.White
            };
            _installButton.Click += async (s, e) => await InstallAsync();
            DockPanel.SetDock(_installButton, Dock.Right);
            buttons.Children.Add(_installButton);

            _laterButton = new Button { Content = "稍后提醒", MinWidth = 90, Height = 32, Margin = new Thickness(8, 0, 0, 0) };
            _laterButton.Click += (s, e) => Close();
            DockPanel.SetDock(_laterButton, Dock.Right);
            buttons.Children.Add(_laterButton);

            _skipButton = new Button { Content = "跳过此版本", MinWidth = 90, Height = 32, Margin = new Thickness(8, 0, 0, 0) };
            _skipButton.Click += (s, e) =>
            {
                BotUpdateService.SkipVersion(_release.Version);
                Close();
            };
            DockPanel.SetDock(_skipButton, Dock.Right);
            buttons.Children.Add(_skipButton);

            Grid.SetRow(buttons, 4);
            root.Children.Add(buttons);
            Closing += (s, e) =>
            {
                if (_downloadCts != null)
                {
                    try { _downloadCts.Cancel(); } catch { }
                }
            };
        }

        private async Task InstallAsync()
        {
            var confirm = MessageBox.Show(
                "更新会关闭当前 Bot，备份现有程序和用户数据，安装成功后自动重启。\n\n请确认当前没有正在发送的消息，然后继续。",
                "确认更新",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            _installButton.IsEnabled = false;
            _laterButton.IsEnabled = false;
            _skipButton.IsEnabled = false;
            _progress.Visibility = Visibility.Visible;
            _downloadCts = new CancellationTokenSource();
            try
            {
                var progress = new Progress<int>(value =>
                {
                    _progress.Value = value;
                    _status.Text = "正在下载并校验安装包：" + value + "%";
                });
                var package = await BotUpdateService.DownloadPackageAsync(_release, progress, _downloadCts.Token);
                _status.Text = "安装包校验成功，正在启动更新程序...";
                BotUpdateService.LaunchInstaller(package, _release);
            }
            catch (OperationCanceledException)
            {
                _status.Text = "下载已取消。";
                ResetButtons();
            }
            catch (Exception ex)
            {
                _status.Text = "更新失败：" + ex.Message;
                MessageBox.Show(_status.Text, "Bot 更新", MessageBoxButton.OK, MessageBoxImage.Error);
                ResetButtons();
            }
            finally
            {
                if (_downloadCts != null)
                {
                    _downloadCts.Dispose();
                    _downloadCts = null;
                }
            }
        }

        private void ResetButtons()
        {
            _installButton.IsEnabled = !string.IsNullOrWhiteSpace(_release.Sha256);
            _laterButton.IsEnabled = true;
            _skipButton.IsEnabled = true;
        }
    }
}
