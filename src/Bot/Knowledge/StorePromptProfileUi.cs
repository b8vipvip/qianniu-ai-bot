using Bot.ChromeNs;
using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bot.Knowledge
{
    internal static class StorePromptProfileUi
    {
        private static int _initialized;

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            EventManager.RegisterClassHandler(
                typeof(KnowledgeManagerControl),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnManagerLoaded),
                true);
        }

        private static void OnManagerLoaded(object sender, RoutedEventArgs e)
        {
            var manager = sender as KnowledgeManagerControl;
            if (manager == null) return;
            var top = FindFirst<WrapPanel>(manager);
            if (top == null) return;
            if (top.Children.OfType<Button>().Any(x => Convert.ToString(x.Tag) == "store-prompt-profile")) return;

            var button = new Button
            {
                Content = "店铺提示词",
                Width = 92,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 6),
                Tag = "store-prompt-profile",
                ToolTip = "填写店铺介绍、链接服务范围、售后保障等资料，并让AI整理成所有智能回复都会使用的固定前置提示词。"
            };
            button.Click += (s, args) =>
            {
                var window = new StorePromptProfileWindow
                {
                    Owner = Window.GetWindow(manager)
                };
                window.ShowDialog();
            };
            top.Children.Add(button);
        }

        private static T FindFirst<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) return null;
            var direct = root as T;
            if (direct != null) return direct;
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var found = FindFirst<T>(VisualTreeHelper.GetChild(root, i));
                if (found != null) return found;
            }
            if (root is ContentControl)
            {
                var child = ((ContentControl)root).Content as DependencyObject;
                var found = FindFirst<T>(child);
                if (found != null) return found;
            }
            return null;
        }
    }

    internal sealed class StorePromptProfileWindow : Window
    {
        private readonly TextBox _raw;
        private readonly TextBox _prompt;
        private readonly TextBlock _status;
        private readonly Button _generate;
        private readonly Button _save;
        private CancellationTokenSource _generationCts;

        public StorePromptProfileWindow()
        {
            Title = "店铺固定提示词";
            Width = 820;
            Height = 700;
            MinWidth = 680;
            MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = root;

            var intro = new TextBlock
            {
                Text = "把店铺介绍、商品/服务范围、不同链接支持什么、购买前提、售后保障和明确不支持的事项粘贴到下面。AI会整理成稳定的前置提示词。",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
                Foreground = Brushes.DimGray
            };
            Grid.SetRow(intro, 0);
            root.Children.Add(intro);

            var rawLabel = new TextBlock
            {
                Text = "原始店铺资料",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 6)
            };
            Grid.SetRow(rawLabel, 1);
            root.Children.Add(rawLabel);

            _raw = new TextBox
            {
                AcceptsReturn = true,
                AcceptsTab = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(_raw, 2);
            root.Children.Add(_raw);

            var promptHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var promptLabel = new TextBlock
            {
                Text = "标准前置提示词（可手动修改）",
                FontWeight = FontWeights.Bold
            };
            DockPanel.SetDock(promptLabel, Dock.Left);
            promptHeader.Children.Add(promptLabel);
            _status = new TextBlock
            {
                Text = string.Empty,
                Foreground = Brushes.DimGray,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            DockPanel.SetDock(_status, Dock.Right);
            promptHeader.Children.Add(_status);
            Grid.SetRow(promptHeader, 3);
            root.Children.Add(promptHeader);

            _prompt = new TextBox
            {
                AcceptsReturn = true,
                AcceptsTab = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 0, 0, 12)
            };
            Grid.SetRow(_prompt, 4);
            root.Children.Add(_prompt);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            _generate = new Button
            {
                Content = "AI生成标准提示词",
                Width = 145,
                Height = 30,
                Margin = new Thickness(0, 0, 8, 0)
            };
            _generate.Click += async (s, e) => await GenerateAsync();
            buttons.Children.Add(_generate);

            _save = new Button
            {
                Content = "保存",
                Width = 80,
                Height = 30,
                Margin = new Thickness(0, 0, 8, 0)
            };
            _save.Click += (s, e) => SaveAndClose();
            buttons.Children.Add(_save);

            var close = new Button { Content = "关闭", Width = 80, Height = 30 };
            close.Click += (s, e) => Close();
            buttons.Children.Add(close);
            Grid.SetRow(buttons, 5);
            root.Children.Add(buttons);

            var profile = StorePromptProfileService.GetProfile();
            _raw.Text = profile.RawInput ?? string.Empty;
            _prompt.Text = profile.StandardPrompt ?? string.Empty;
            _status.Text = string.IsNullOrWhiteSpace(profile.UpdatedAt)
                ? "尚未配置"
                : "最后更新：" + profile.UpdatedAt;

            Closing += (s, e) =>
            {
                if (_generationCts != null)
                {
                    try { _generationCts.Cancel(); } catch { }
                }
            };
        }

        private async System.Threading.Tasks.Task GenerateAsync()
        {
            if (string.IsNullOrWhiteSpace(_raw.Text))
            {
                MessageBox.Show("请先填写原始店铺资料。", "店铺提示词", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _generationCts = new CancellationTokenSource();
            _generate.IsEnabled = false;
            _save.IsEnabled = false;
            _generate.Content = "正在整理...";
            _status.Text = "AI正在整理，完成后会自动保存";
            try
            {
                var prompt = await StorePromptProfileService.GenerateStandardPromptAsync(
                    _raw.Text,
                    _generationCts.Token);
                _prompt.Text = prompt;
                _status.Text = "已生成并保存 · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                MessageBox.Show(
                    "标准前置提示词已生成并保存。后续智能回复会自动把它作为高优先级店铺事实和服务边界。",
                    "生成完成",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                _status.Text = "生成已取消";
            }
            catch (Exception ex)
            {
                _status.Text = "生成失败";
                MessageBox.Show("生成提示词失败：" + ex.Message, "店铺提示词", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (_generationCts != null)
                {
                    _generationCts.Dispose();
                    _generationCts = null;
                }
                _generate.IsEnabled = true;
                _save.IsEnabled = true;
                _generate.Content = "AI生成标准提示词";
            }
        }

        private void SaveAndClose()
        {
            try
            {
                StorePromptProfileService.Save(_raw.Text, _prompt.Text);
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存提示词失败：" + ex.Message, "店铺提示词", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
