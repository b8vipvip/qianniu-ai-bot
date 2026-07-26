using Bot.UpdateNs;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bot.Options
{
    internal sealed class BotUpdateOptionsControl : UserControl, IOptions
    {
        private readonly TextBlock _currentVersion;
        private readonly TextBlock _latestVersion;
        private readonly TextBlock _status;
        private readonly TextBlock _skipped;
        private readonly CheckBox _autoCheck;
        private readonly CheckBox _notifyPopup;
        private readonly CheckBox _autoDownload;
        private readonly ComboBox _interval;
        private readonly Button _checkButton;
        private readonly Button _installButton;
        private bool _subscribed;

        public BotUpdateOptionsControl()
        {
            MinWidth = 650;
            MinHeight = 580;
            var root = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var panel = new StackPanel { Margin = new Thickness(16) };
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
                Text = "自动识别 GitHub Actions 发布的正式版本，下载前校验 SHA-256，安装失败自动回滚。",
                Margin = new Thickness(0, 7, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(hero);

            var versionCard = CreateCard();
            var versionPanel = (StackPanel)versionCard.Child;
            versionPanel.Children.Add(CreateTitle("版本信息"));
            var versionGrid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            versionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            versionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            versionGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            versionGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            versionGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddLabel(versionGrid, 0, "当前版本");
            _currentVersion = AddValue(versionGrid, 0, BotUpdateService.CurrentVersion);
            AddLabel(versionGrid, 1, "最新版本");
            _latestVersion = AddValue(versionGrid, 1, "尚未检查");
            AddLabel(versionGrid, 2, "跳过版本");
            _skipped = AddValue(versionGrid, 2, "无");
            versionPanel.Children.Add(versionGrid);
            panel.Children.Add(versionCard);

            var updateCard = CreateCard();
            var updatePanel = (StackPanel)updateCard.Child;
            updatePanel.Children.Add(CreateTitle("更新方式"));
            _autoCheck = new CheckBox
            {
                Content = "启动后和运行期间自动检查新版本",
                Margin = new Thickness(0, 12, 0, 5)
            };
            updatePanel.Children.Add(_autoCheck);
            _notifyPopup = new CheckBox
            {
                Content = "发现新版本时弹窗通知（同一版本24小时内最多提醒一次）",
                Margin = new Thickness(0, 5, 0, 5)
            };
            updatePanel.Children.Add(_notifyPopup);
            _autoDownload = new CheckBox
            {
                Content = "后台自动下载安装包并校验（安装前仍需人工确认）",
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
            _checkButton = CreateButton("检查更新", true);
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
            var clearSkip = CreateButton("取消跳过版本", false);
            clearSkip.Click += (s, e) =>
            {
                BotUpdateService.ClearSkippedVersion();
                LoadSettings();
                _status.Text = "已取消跳过版本，下次检查时会重新提示。";
            };
            buttons.Children.Add(clearSkip);
            actionPanel.Children.Add(buttons);
            panel.Children.Add(actionCard);

            var note = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(239, 246, 255)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(191, 219, 254)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(14)
            };
            note.Child = new TextBlock
            {
                Text = "更新安全机制：仅接受本仓库 bot-v* 正式 Release；安装包必须通过 update.json 中的 SHA-256 校验；更新前备份程序和永久用户数据；新程序无法启动时自动回滚。自动下载不等于自动安装，安装会关闭并重启 Bot。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(30, 64, 175))
            };
            panel.Children.Add(note);

            Loaded += (s, e) =>
            {
                LoadSettings();
                Subscribe();
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
            settings.NotifyPopup = _notifyPopup.IsChecked == true;
            settings.AutoDownload = _autoDownload.IsChecked == true;
            var item = _interval.SelectedItem as ComboBoxItem;
            settings.CheckIntervalHours = item == null ? 6 : Convert.ToInt32(item.Tag);
            BotUpdateService.SaveSettings(settings);
        }

        public void RestoreDefault()
        {
            _autoCheck.IsChecked = true;
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
        }

        private async Task CheckAsync()
        {
            Save(string.Empty);
            _checkButton.IsEnabled = false;
            _status.Text = "正在连接 GitHub 检查新版本...";
            try
            {
                var result = await BotUpdateService.CheckNowAsync(true);
                ApplyResult(result);
                if (result.Success && result.UpdateAvailable && result.Release != null)
                {
                    BotUpdateService.ShowUpdatePrompt(result.Release, Window.GetWindow(this));
                }
                else if (result.Success)
                {
                    MessageBox.Show(result.Message, "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(result.Message, "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            _latestVersion.Text = result.Release == null ? "未找到" : result.Release.Version;
            _installButton.IsEnabled = result.UpdateAvailable && result.Release != null;
            LoadSkippedVersionOnly();
        }

        private void LoadSettings()
        {
            var settings = BotUpdateService.GetSettings();
            _autoCheck.IsChecked = settings.AutoCheck;
            _notifyPopup.IsChecked = settings.NotifyPopup;
            _autoDownload.IsChecked = settings.AutoDownload;
            SelectInterval(settings.CheckIntervalHours);
            LoadSkippedVersionOnly();
        }

        private void LoadSkippedVersionOnly()
        {
            var settings = BotUpdateService.GetSettings();
            _skipped.Text = string.IsNullOrWhiteSpace(settings.SkippedVersion) ? "无" : settings.SkippedVersion;
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
            return new Button
            {
                Content = text,
                MinWidth = 100,
                Height = 32,
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 3, 8, 3),
                Background = primary ? new SolidColorBrush(Color.FromRgb(37, 99, 235)) : null,
                Foreground = primary ? Brushes.White : null,
                BorderBrush = primary ? new SolidColorBrush(Color.FromRgb(37, 99, 235)) : null
            };
        }
    }
}
