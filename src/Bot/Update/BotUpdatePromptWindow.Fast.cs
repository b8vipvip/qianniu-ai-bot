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
    internal sealed class BotUpdatePromptWindow : Window
    {
        private readonly BotReleaseInfo _release;
        private readonly TextBlock _status;
        private readonly TextBlock _channel;
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
            Height = 545;
            MinWidth = 520;
            MinHeight = 440;
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
                Text = "当前版本：" + BotUpdateService.CurrentVersion
                    + "    最新版本：" + release.Version
                    + (release.PublishedAt == DateTime.MinValue
                        ? string.Empty
                        : "    发布时间：" + release.PublishedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")),
                Margin = new Thickness(0, 8, 0, 10),
                Foreground = Brushes.DimGray
            };
            Grid.SetRow(versions, 1);
            root.Children.Add(versions);

            var notes = new TextBox
            {
                Text = string.IsNullOrWhiteSpace(release.Notes)
                    ? "本版本已通过 GitHub Actions 完整构建和校验。"
                    : release.Notes,
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
                        ? "该版本缺少 SHA-256 清单，只能打开发布页面手动下载。"
                        : "点击“立即更新”后优先从服务器下载；服务器不可用时自动切换 GitHub。"),
                TextWrapping = TextWrapping.Wrap
            };
            statusPanel.Children.Add(_status);
            _channel = new TextBlock
            {
                Text = BotUpdateService.IsPackageReady(release)
                    ? "下载通道：本地缓存"
                    : "下载通道：等待开始",
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 5, 0, 0)
            };
            statusPanel.Children.Add(_channel);
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
            var open = new Button
            {
                Content = "查看发布页面",
                MinWidth = 105,
                Height = 32,
                Margin = new Thickness(0, 0, 8, 0)
            };
            open.Click += delegate { BotUpdateService.OpenReleasesPage(); };
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
            _installButton.Click += async delegate { await InstallAsync(); };
            DockPanel.SetDock(_installButton, Dock.Right);
            buttons.Children.Add(_installButton);

            _laterButton = new Button
            {
                Content = "稍后提醒",
                MinWidth = 90,
                Height = 32,
                Margin = new Thickness(8, 0, 0, 0)
            };
            _laterButton.Click += delegate { Close(); };
            DockPanel.SetDock(_laterButton, Dock.Right);
            buttons.Children.Add(_laterButton);

            _skipButton = new Button
            {
                Content = "跳过此版本",
                MinWidth = 90,
                Height = 32,
                Margin = new Thickness(8, 0, 0, 0)
            };
            _skipButton.Click += delegate
            {
                BotUpdateService.SkipVersion(_release.Version);
                Close();
            };
            DockPanel.SetDock(_skipButton, Dock.Right);
            buttons.Children.Add(_skipButton);

            Grid.SetRow(buttons, 4);
            root.Children.Add(buttons);
            Closing += delegate
            {
                if (_downloadCts != null)
                {
                    try { _downloadCts.Cancel(); } catch { }
                }
            };
        }

        private async Task InstallAsync()
        {
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
                    var channel = string.IsNullOrWhiteSpace(BotUpdateService.CurrentDownloadChannel)
                        ? "连接中"
                        : BotUpdateService.CurrentDownloadChannel;
                    _channel.Text = "下载通道：" + channel;
                    _status.Text = "正在下载并校验安装包：" + value + "%";
                });
                var package = await BotUpdateService.DownloadPackageAsync(
                    _release,
                    progress,
                    _downloadCts.Token);
                _channel.Text = "下载通道：" + (string.IsNullOrWhiteSpace(BotUpdateService.CurrentDownloadChannel)
                    ? "已完成"
                    : BotUpdateService.CurrentDownloadChannel);
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
                MessageBox.Show(
                    _status.Text,
                    "Bot 更新",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
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
