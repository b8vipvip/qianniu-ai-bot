using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bot.UpdateNs
{
    internal sealed class BotUpdateAutoProgressWindow : Window
    {
        private static BotUpdateAutoProgressWindow _current;
        private readonly TextBlock _status;
        private readonly TextBlock _channel;
        private readonly ProgressBar _progress;
        private readonly string _version;

        private BotUpdateAutoProgressWindow(BotReleaseInfo release)
        {
            _version = release == null ? string.Empty : release.Version;
            Title = "Bot 自动更新";
            Width = 480;
            Height = 205;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;
            Topmost = false;

            var panel = new StackPanel { Margin = new Thickness(20) };
            Content = panel;
            panel.Children.Add(new TextBlock
            {
                Text = "正在自动更新到 " + _version,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(30, 64, 175))
            });
            _status = new TextBlock
            {
                Text = "等待下载通道...",
                Margin = new Thickness(0, 14, 0, 5),
                TextWrapping = TextWrapping.Wrap
            };
            panel.Children.Add(_status);
            _channel = new TextBlock
            {
                Text = "下载通道：等待服务器分配",
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 0, 0, 8)
            };
            panel.Children.Add(_channel);
            _progress = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Height = 12,
                Value = 0,
                IsIndeterminate = true
            };
            panel.Children.Add(_progress);
            panel.Children.Add(new TextBlock
            {
                Text = "优先使用服务器；服务器镜像未准备好或不可用时只切换一次 GitHub。连接阶段使用动态进度，收到安装包数据后才显示实际百分比。",
                Margin = new Thickness(0, 10, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            });
            Closed += delegate
            {
                BotUpdateService.StatusChanged -= OnStatusChanged;
                if (ReferenceEquals(_current, this)) _current = null;
            };
            BotUpdateService.StatusChanged += OnStatusChanged;
        }

        public static void ShowFor(BotReleaseInfo release)
        {
            if (release == null) return;
            if (_current != null && _current.IsVisible)
            {
                _current.Activate();
                return;
            }
            _current = new BotUpdateAutoProgressWindow(release);
            _current.Show();
        }

        private void OnStatusChanged(BotUpdateCheckResult result)
        {
            if (result == null) return;
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (result.Release != null
                    && !string.IsNullOrWhiteSpace(_version)
                    && !string.Equals(result.Release.Version, _version, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var connecting = result.DownloadPercent <= 0
                    && result.DownloadedBytes <= 0
                    && !result.InstallStarted;
                if (connecting)
                {
                    _progress.IsIndeterminate = true;
                    _progress.Value = 0;
                }
                else if (result.DownloadPercent >= 0)
                {
                    _progress.IsIndeterminate = false;
                    _progress.Value = Math.Max(0, Math.Min(100, result.DownloadPercent));
                }

                if (!string.IsNullOrWhiteSpace(result.DownloadChannel))
                {
                    _channel.Text = "下载通道：" + result.DownloadChannel;
                }

                if (connecting && !string.IsNullOrWhiteSpace(result.DownloadChannel))
                {
                    _status.Text = "正在连接 " + result.DownloadChannel
                        + " 下载通道，收到首批安装包数据后显示实际百分比...";
                }
                else if (!string.IsNullOrWhiteSpace(result.Message))
                {
                    _status.Text = result.Message;
                }

                if (result.InstallStarted)
                {
                    _progress.IsIndeterminate = false;
                    _progress.Value = 100;
                }
            }));
        }
    }
}
