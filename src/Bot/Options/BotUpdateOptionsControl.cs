using Bot.ChromeNs;
using Bot.UpdateNs;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bot.Options
{
    internal sealed class InstalledBotBuildInfo
    {
        public string Version { get; set; }
        public string Tag { get; set; }
        public string Commit { get; set; }
        public string PublishedAt { get; set; }
        public string Channel { get; set; }
        public string SourceRunId { get; set; }
        public string InstallDirectory { get; set; }
        public string BuildSource { get; set; }
    }

    internal sealed class BotUpdateOptionsControl : UserControl, IOptions
    {
        private readonly TextBlock _currentVersion;
        private readonly TextBlock _latestVersion;
        private readonly TextBlock _status;
        private readonly TextBlock _skipped;
        private readonly TextBlock _failedInstall;
        private readonly TextBlock _buildCommit;
        private readonly TextBlock _buildTime;
        private readonly TextBlock _buildChannel;
        private readonly TextBlock _buildRun;
        private readonly TextBlock _installDirectory;
        private readonly CheckBox _autoCheck;
        private readonly CheckBox _autoUpdate;
        private readonly CheckBox _notifyPopup;
        private readonly CheckBox _autoDownload;
        private readonly ComboBox _interval;
        private readonly Button _checkButton;
        private readonly Button _installButton;
        private bool _subscribed;

        public BotUpdateOptionsControl()
        {
            MinWidth = 0;
            MinHeight = 0;
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Stretch;

            var root = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                CanContentScroll = false,
                PanningMode = PanningMode.VerticalOnly
            };
            var panel = new StackPanel { Margin = new Thickness(12, 12, 16, 18) };
            root.Content = panel;
            Content = root;

            var hero = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(23, 32, 51)),
                CornerRadius = new CornerRadius(11),
                Padding = new Thickness(18),
                Margin = new Thickness(0, 0, 0, 12)
            };
            var heroPanel = new StackPanel();
            hero.Child = heroPanel;
            heroPanel.Children.Add(new TextBlock
            {
                Text = "关于与版本更新",
                FontSize = 22,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White
            });
            heroPanel.Children.Add(new TextBlock
            {
                Text = "查看当前构建信息，手动检查正式版本，并安全下载、校验、安装或自动回滚。",
                Margin = new Thickness(0, 7, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(hero);

            var build = ReadInstalledBuildInfo();
            var versionCard = CreateCard();
            var versionPanel = (StackPanel)versionCard.Child;
            versionPanel.Children.Add(CreateTitle("当前程序与构建信息"));
            var versionGrid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            versionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            versionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var i = 0; i < 9; i++) versionGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            AddLabel(versionGrid, 0, "当前版本");
            _currentVersion = AddValue(versionGrid, 0, BotUpdateService.CurrentVersion);
            AddLabel(versionGrid, 1, "构建提交");
            _buildCommit = AddValue(versionGrid, 1, DisplayCommit(build.Commit));
            AddLabel(versionGrid, 2, "发布时间/构建时间");
            _buildTime = AddValue(versionGrid, 2, build.PublishedAt);
            AddLabel(versionGrid, 3, "更新通道");
            _buildChannel = AddValue(versionGrid, 3, build.Channel);
            AddLabel(versionGrid, 4, "构建任务");
            _buildRun = AddValue(versionGrid, 4, build.SourceRunId);
            AddLabel(versionGrid, 5, "安装目录");
            _installDirectory = AddValue(versionGrid, 5, build.InstallDirectory);
            _installDirectory.TextWrapping = TextWrapping.Wrap;
            AddLabel(versionGrid, 6, "最新正式版本");
            _latestVersion = AddValue(versionGrid, 6, "尚未检查");
            AddLabel(versionGrid, 7, "用户跳过版本");
            _skipped = AddValue(versionGrid, 7, "无");
            AddLabel(versionGrid, 8, "安装失败隔离");
            _failedInstall = AddValue(versionGrid, 8, "无");
            versionPanel.Children.Add(versionGrid);

            versionPanel.Children.Add(new TextBlock
            {
                Text = build.BuildSource,
                Margin = new Thickness(0, 10, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(versionCard);

            // Put the primary action card before less important auto-check preferences so
            // remote desktop scaling and small settings windows never hide Check/Install.
            var actionCard = CreateCard();
            var actionPanel = (StackPanel)actionCard.Child;
            actionPanel.Children.Add(CreateTitle("检查与安装"));
            _status = new TextBlock
            {
                Text = "尚未检查更新。",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 10),
                Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105))
            };
            actionPanel.Children.Add(_status);
            var buttons = new WrapPanel();
            _checkButton = CreateButton("手动检查更新", true);
            _checkButton.Click += async (s, e) => await CheckAsync();
            buttons.Children.Add(_checkButton);
            _installButton = CreateButton("下载并安装", false);
            _installButton.IsEnabled = false;
            _installButton.Click += (s, e) =>
            {
                var release = BotUpdateService.LatestRelease;
                if (release != null) BotUpdateService.ShowUpdatePrompt(release, Window.GetWindow(this));
            };
            buttons.Children.Add(_installButton);
            var releaseButton = CreateButton("查看发布页面", false);
            releaseButton.Click += (s, e) => BotUpdateService.OpenReleasesPage();
            buttons.Children.Add(releaseButton);
            var openInstall = CreateButton("打开安装目录", false);
            openInstall.Click += (s, e) => OpenDirectory(ReadInstalledBuildInfo().InstallDirectory);
            buttons.Children.Add(openInstall);
            var clearSkip = CreateButton("清除跳过/失败隔离", false);
            clearSkip.Click += (s, e) =>
            {
                BotUpdateService.ClearSkippedVersion();
                LoadSettings();
                _status.Text = "已清除用户跳过和安装失败隔离，并重新连接服务端版本通知。";
            };
            buttons.Children.Add(clearSkip);
            actionPanel.Children.Add(buttons);
            panel.Children.Add(actionCard);

            var updateCard = CreateCard();
            var updatePanel = (StackPanel)updateCard.Child;
            updatePanel.Children.Add(CreateTitle("自动检查与更新设置"));
            _autoCheck = new CheckBox
            {
                Content = "启动后和运行期间自动检查新版本",
                Margin = new Thickness(0, 12, 0, 5)
            };
            updatePanel.Children.Add(_autoCheck);
            _autoUpdate = new CheckBox
            {
                Content = "自动更新（发现新版本后自动下载安装并重启，无需确认）",
                Margin = new Thickness(0, 5, 0, 5)
            };
            _autoUpdate.Checked += (s, e) => _autoCheck.IsChecked = true;
            updatePanel.Children.Add(_autoUpdate);
            _notifyPopup = new CheckBox
            {
                Content = "发现新版本时弹窗通知（同一版本24小时内最多提醒一次）",
                Margin = new Thickness(0, 5, 0, 5)
            };
            updatePanel.Children.Add(_notifyPopup);
            _autoDownload = new CheckBox
            {
                Content = "后台自动下载安装包并校验（不自动重启）",
                Margin = new Thickness(0, 5, 0, 10)
            };
            updatePanel.Children.Add(_autoDownload);

            var intervalRow = new StackPanel { Orientation = Orientation.Horizontal };
            intervalRow.Children.Add(new TextBlock
            {
                Text = "自动检查间隔",
                Width = 130,
                VerticalAlignment = VerticalAlignment.Center
            });
            _interval = new ComboBox { Width = 180, Height = 30 };
            AddInterval("每1小时", 1);
            AddInterval("每3小时", 3);
            AddInterval("每6小时（推荐）", 6);
            AddInterval("每12小时", 12);
            AddInterval("每天", 24);
            AddInterval("每3天", 72);
            intervalRow.Children.Add(_interval);
            updatePanel.Children.Add(intervalRow);
            panel.Children.Add(updateCard);

            var note = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(239, 246, 255)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(191, 219, 254)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 0, 0, 4)
            };
            note.Child = new TextBlock
            {
                Text = "安全机制：只接受本仓库 bot-v* 正式 Release；安装包必须通过 update.json 的 SHA-256 校验；更新前备份程序和永久用户数据；新版本无法启动时自动回滚。手动点击“立即更新”不再弹出二次确认；开启“自动更新”后，发现新版本会自动下载安装并重启 Bot。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(30, 64, 175))
            };
            panel.Children.Add(note);

            Loaded += (s, e) =>
            {
                LoadSettings();
                Subscribe();
                RefreshBuildInfo();
                ApplyResult(BotUpdateService.LastResult);
            };
            Unloaded += (s, e) => Unsubscribe();
        }

        public OptionEnum OptionType
        {
            get { return OptionEnum.AboutUpdate; }
        }

        public void Save(string seller)
        {
            var settings = BotUpdateService.GetSettings();
            settings.AutoCheck = _autoCheck.IsChecked == true;
            settings.AutoInstall = _autoUpdate.IsChecked == true;
            if (settings.AutoInstall) settings.AutoCheck = true;
            settings.NotifyPopup = _notifyPopup.IsChecked == true;
            settings.AutoDownload = _autoDownload.IsChecked == true;
            var item = _interval.SelectedItem as ComboBoxItem;
            settings.CheckIntervalHours = item == null ? 6 : Convert.ToInt32(item.Tag);
            BotUpdateService.SaveSettings(settings);
        }

        public void RestoreDefault()
        {
            _autoCheck.IsChecked = true;
            _autoUpdate.IsChecked = false;
            _notifyPopup.IsChecked = true;
            _autoDownload.IsChecked = false;
            SelectInterval(6);
        }

        public void NavHelp()
        {
            BotUpdateService.OpenReleasesPage();
        }

        public void InitUI(string seller)
        {
            LoadSettings();
            RefreshBuildInfo();
        }

        private async Task CheckAsync()
        {
            Save(string.Empty);
            _checkButton.IsEnabled = false;
            _status.Text = "正在检查新版本...";
            try
            {
                var result = await BotUpdateService.CheckNowAsync(true);
                ApplyResult(result);
                if (result.Success
                    && result.UpdateAvailable
                    && result.Release != null
                    && !result.InstallStarted)
                {
                    BotUpdateService.ShowUpdatePrompt(result.Release, Window.GetWindow(this));
                }
                else if (!result.InstallStarted)
                {
                    MessageBox.Show(result.Message, "检查更新", MessageBoxButton.OK,
                        result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
                }
            }
            finally
            {
                _checkButton.IsEnabled = true;
            }
        }

        private void Subscribe()
        {
            if (_subscribed) return;
            BotUpdateService.StatusChanged += OnStatusChanged;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_subscribed) return;
            BotUpdateService.StatusChanged -= OnStatusChanged;
            _subscribed = false;
        }

        private void OnStatusChanged(BotUpdateCheckResult result)
        {
            Dispatcher.BeginInvoke(new Action(() => ApplyResult(result)));
        }

        private void ApplyResult(BotUpdateCheckResult result)
        {
            _currentVersion.Text = BotUpdateService.CurrentVersion;
            if (result == null)
            {
                _latestVersion.Text = BotUpdateService.LatestRelease == null
                    ? "尚未检查"
                    : BotUpdateService.LatestRelease.Version;
                _installButton.IsEnabled = BotUpdateService.LatestRelease != null;
                return;
            }
            _status.Text = result.Message;
            _latestVersion.Text = result.Release == null ? "未找到正式版本" : result.Release.Version;
            _installButton.IsEnabled = result.UpdateAvailable && result.Release != null && !result.InstallStarted;
            LoadSkippedVersionOnly();
        }

        private void LoadSettings()
        {
            var settings = BotUpdateService.GetSettings();
            _autoCheck.IsChecked = settings.AutoCheck || settings.AutoInstall;
            _autoUpdate.IsChecked = settings.AutoInstall;
            _notifyPopup.IsChecked = settings.NotifyPopup;
            _autoDownload.IsChecked = settings.AutoDownload;
            SelectInterval(settings.CheckIntervalHours);
            LoadSkippedVersionOnly();
        }

        private void LoadSkippedVersionOnly()
        {
            var settings = BotUpdateService.GetSettings();
            _skipped.Text = string.IsNullOrWhiteSpace(settings.UserSkippedVersion)
                ? "无"
                : settings.UserSkippedVersion;
            _failedInstall.Text = string.IsNullOrWhiteSpace(settings.FailedInstallVersion)
                ? "无"
                : settings.FailedInstallVersion;
        }

        private void RefreshBuildInfo()
        {
            var build = ReadInstalledBuildInfo();
            _currentVersion.Text = BotUpdateService.CurrentVersion;
            _buildCommit.Text = DisplayCommit(build.Commit);
            _buildTime.Text = build.PublishedAt;
            _buildChannel.Text = build.Channel;
            _buildRun.Text = build.SourceRunId;
            _installDirectory.Text = build.InstallDirectory;
        }

        private void AddInterval(string text, int hours)
        {
            _interval.Items.Add(new ComboBoxItem { Content = text, Tag = hours });
        }

        private void SelectInterval(int hours)
        {
            var exact = _interval.Items.OfType<ComboBoxItem>().FirstOrDefault(x => Convert.ToInt32(x.Tag) == hours);
            if (exact == null)
            {
                exact = _interval.Items.OfType<ComboBoxItem>()
                    .OrderBy(x => Math.Abs(Convert.ToInt32(x.Tag) - hours))
                    .FirstOrDefault();
            }
            _interval.SelectedItem = exact;
        }

        internal static InstalledBotBuildInfo ReadInstalledBuildInfo()
        {
            var baseDirectory = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            var installDirectory = string.Equals(Path.GetFileName(baseDirectory), "Bin", StringComparison.OrdinalIgnoreCase)
                ? Directory.GetParent(baseDirectory).FullName
                : baseDirectory;
            var result = new InstalledBotBuildInfo
            {
                Version = BotUpdateService.CurrentVersion,
                Tag = string.Empty,
                Commit = string.Empty,
                PublishedAt = string.Empty,
                Channel = "本地/兼容版本",
                SourceRunId = "无",
                InstallDirectory = installDirectory,
                BuildSource = "当前安装包没有 release-info.json，显示程序集版本和 Bot.exe 文件时间。"
            };

            foreach (var path in new[]
            {
                Path.Combine(installDirectory, "release-info.json"),
                Path.Combine(baseDirectory, "release-info.json")
            }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var json = JObject.Parse(File.ReadAllText(path));
                    result.Version = Convert.ToString(json["version"] ?? result.Version);
                    result.Tag = Convert.ToString(json["tag"] ?? string.Empty);
                    result.Commit = Convert.ToString(json["commit"] ?? string.Empty);
                    result.Channel = Convert.ToString(json["channel"] ?? "stable");
                    result.SourceRunId = Convert.ToString(json["source_run_id"] ?? "无");
                    var published = Convert.ToString(json["published_at"] ?? string.Empty);
                    DateTime parsed;
                    result.PublishedAt = DateTime.TryParse(published, out parsed)
                        ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                        : published;
                    result.BuildSource = "构建信息来自已校验安装包中的 release-info.json。";
                    return NormalizeBuildInfo(result);
                }
                catch
                {
                }
            }

            try
            {
                var exe = Assembly.GetExecutingAssembly().Location;
                if (File.Exists(exe)) result.PublishedAt = File.GetLastWriteTime(exe).ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch
            {
            }
            return NormalizeBuildInfo(result);
        }

        private static InstalledBotBuildInfo NormalizeBuildInfo(InstalledBotBuildInfo info)
        {
            info.Version = string.IsNullOrWhiteSpace(info.Version) ? BotUpdateService.CurrentVersion : info.Version;
            info.Commit = string.IsNullOrWhiteSpace(info.Commit) ? "未记录" : info.Commit;
            info.PublishedAt = string.IsNullOrWhiteSpace(info.PublishedAt) ? "未记录" : info.PublishedAt;
            info.Channel = string.IsNullOrWhiteSpace(info.Channel) ? "stable" : info.Channel;
            info.SourceRunId = string.IsNullOrWhiteSpace(info.SourceRunId) ? "未记录" : info.SourceRunId;
            info.InstallDirectory = string.IsNullOrWhiteSpace(info.InstallDirectory) ? AppDomain.CurrentDomain.BaseDirectory : info.InstallDirectory;
            return info;
        }

        private static string DisplayCommit(string commit)
        {
            commit = (commit ?? string.Empty).Trim();
            if (commit.Length == 0 || commit == "未记录") return "未记录";
            return commit.Length <= 12 ? commit : commit.Substring(0, 12) + "（完整提交已记录在发布信息中）";
        }

        private static void OpenDirectory(string directory)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                {
                    Process.Start("explorer.exe", directory);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开安装目录失败：" + ex.Message, "关于与版本更新", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static Border CreateCard()
        {
            return new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(227, 232, 240)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16),
                Margin = new Thickness(0, 0, 0, 12),
                Child = new StackPanel()
            };
        }

        private static TextBlock CreateTitle(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55))
            };
        }

        private static void AddLabel(Grid grid, int row, string text)
        {
            var label = new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 8, 4),
                Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105))
            };
            Grid.SetRow(label, row);
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);
        }

        private static TextBlock AddValue(Grid grid, int row, string text)
        {
            var value = new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 4),
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 42))
            };
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);
            return value;
        }

        private static Button CreateButton(string text, bool primary)
        {
            var background = primary
                ? new SolidColorBrush(Color.FromRgb(37, 99, 235))
                : new SolidColorBrush(Color.FromRgb(248, 250, 252));
            var foreground = primary
                ? Brushes.White
                : new SolidColorBrush(Color.FromRgb(15, 23, 42));
            var border = primary
                ? new SolidColorBrush(Color.FromRgb(37, 99, 235))
                : new SolidColorBrush(Color.FromRgb(148, 163, 184));

            var button = new Button
            {
                Content = text,
                MinWidth = 100,
                Height = 32,
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 3, 8, 3),
                Background = background,
                Foreground = foreground,
                BorderBrush = border,
                BorderThickness = new Thickness(1),
                Opacity = 1.0
            };
            button.IsEnabledChanged += (sender, args) =>
            {
                button.Opacity = button.IsEnabled ? 1.0 : 0.62;
            };
            return button;
        }
    }

    internal static class BotAboutUpdateLauncher
    {
        private static Window _standalone;

        public static void Show()
        {
            try
            {
                var seller = QN.CurQN == null || QN.CurQN.Seller == null
                    ? string.Empty
                    : (QN.CurQN.Seller.Nick ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(seller))
                {
                    WndOption.MyShow(seller, null, OptionEnum.AboutUpdate);
                    return;
                }

                if (_standalone != null && _standalone.IsVisible)
                {
                    _standalone.Activate();
                    return;
                }
                _standalone = new Window
                {
                    Title = "关于与版本更新",
                    Width = 760,
                    Height = 720,
                    MinWidth = 680,
                    MinHeight = 520,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Content = new BotUpdateOptionsControl()
                };
                _standalone.Closed += (s, e) => _standalone = null;
                _standalone.Show();
                _standalone.Activate();
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开关于与版本更新失败：" + ex.Message,
                    "关于与版本更新", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
