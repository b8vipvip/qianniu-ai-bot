using Bot.ChromeNs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bot.Knowledge
{
    internal static class KnowledgeOptimizationUi
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
            if (top.Children.OfType<Button>().Any(x => Convert.ToString(x.Tag) == "knowledge-optimize")) return;

            var button = new Button
            {
                Content = "优化问答",
                Width = 86,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 6),
                Tag = "knowledge-optimize",
                ToolTip = "分批调用AI审校已有问答，优先修复答案截断、答非所问和明显语病；开始前自动备份。"
            };
            button.Click += async (s, args) => await RunOptimizationAsync(manager, button);
            top.Children.Add(button);
        }

        private static async System.Threading.Tasks.Task RunOptimizationAsync(
            KnowledgeManagerControl manager,
            Button button)
        {
            var choice = MessageBox.Show(
                "请选择优化范围：\n\n" +
                "【是】仅优化 智能导入 / 历史扫描 / 自动学习 / AI生成 的问答（推荐）\n" +
                "【否】优化全部已启用问答\n" +
                "【取消】不执行\n\n" +
                "AI只会在发现答案截断、答非所问、明显语病等问题时修改，并会在开始前自动备份知识库。",
                "优化问答",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel) return;

            var all = BotFeatureStore.GetKnowledgeBase() ?? new List<KnowledgeBaseEntry>();
            var targets = choice == MessageBoxResult.Yes
                ? all.Where(KnowledgeOptimizationService.IsAiManagedSource).ToList()
                : all.Where(x => x != null && x.Enabled).ToList();
            if (targets.Count == 0)
            {
                MessageBox.Show("没有找到符合当前优化范围的问答。", "优化问答", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var owner = Window.GetWindow(manager);
            var progressWindow = new KnowledgeOptimizationProgressWindow(targets.Count)
            {
                Owner = owner
            };
            button.IsEnabled = false;
            button.Content = "优化中...";
            progressWindow.Show();

            try
            {
                var result = await KnowledgeOptimizationService.OptimizeAsync(
                    targets,
                    p => manager.Dispatcher.BeginInvoke(new Action(() => progressWindow.UpdateProgress(p))),
                    progressWindow.Token);

                manager.RefreshData();
                progressWindow.MarkCompleted();
                MessageBox.Show(
                    "问答优化完成。\n\n" +
                    "处理：" + result.Total + " 条\n" +
                    "已优化：" + result.Changed + " 条\n" +
                    "保持原文：" + result.Kept + " 条\n" +
                    "失败未修改：" + result.Failed + " 条" +
                    (string.IsNullOrWhiteSpace(result.BackupPath) ? string.Empty : "\n\n优化前备份：\n" + result.BackupPath),
                    "优化问答完成",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                manager.RefreshData();
                progressWindow.MarkCompleted();
                MessageBox.Show("已停止优化。已经完成并保存的批次会保留，未处理批次不会修改。", "优化已停止", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                manager.RefreshData();
                progressWindow.MarkCompleted();
                MessageBox.Show("优化失败：" + ex.Message + "\n\n已完成批次不会丢失，未完成内容保持原样。", "优化问答", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                button.IsEnabled = true;
                button.Content = "优化问答";
                if (progressWindow.IsVisible) progressWindow.Close();
                progressWindow.Dispose();
            }
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

    internal sealed class KnowledgeOptimizationProgressWindow : Window, IDisposable
    {
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly ProgressBar _bar;
        private readonly TextBlock _status;
        private readonly TextBlock _detail;
        private readonly Button _cancel;
        private readonly int _total;
        private bool _completed;

        public CancellationToken Token { get { return _cts.Token; } }

        public KnowledgeOptimizationProgressWindow(int total)
        {
            _total = Math.Max(1, total);
            Title = "正在优化问答";
            Width = 430;
            Height = 220;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var root = new StackPanel { Margin = new Thickness(18) };
            Content = root;
            root.Children.Add(new TextBlock
            {
                Text = "AI正在分批审校知识库",
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 10)
            });
            _status = new TextBlock { Text = "正在准备...", Margin = new Thickness(0, 0, 0, 8) };
            root.Children.Add(_status);
            _bar = new ProgressBar { Minimum = 0, Maximum = _total, Height = 18, Margin = new Thickness(0, 0, 0, 8) };
            root.Children.Add(_bar);
            _detail = new TextBlock { Text = "已优化 0 条", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray };
            root.Children.Add(_detail);
            _cancel = new Button
            {
                Content = "停止优化",
                Width = 90,
                Height = 28,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            _cancel.Click += (s, e) =>
            {
                _cancel.IsEnabled = false;
                _cancel.Content = "正在停止...";
                _cts.Cancel();
            };
            root.Children.Add(_cancel);
            Closing += (s, e) =>
            {
                if (_completed) return;
                e.Cancel = true;
                _cancel.IsEnabled = false;
                _cancel.Content = "正在停止...";
                _cts.Cancel();
            };
        }

        public void UpdateProgress(KnowledgeOptimizationProgress progress)
        {
            if (progress == null) return;
            _bar.Value = Math.Min(_total, Math.Max(0, progress.Processed));
            _status.Text = "第 " + progress.BatchIndex + "/" + progress.BatchCount + " 批 · " + (progress.Message ?? string.Empty);
            _detail.Text = "已处理 " + progress.Processed + "/" + _total +
                " · 已优化 " + progress.Changed +
                " · 保持原文 " + progress.Kept +
                " · 失败 " + progress.Failed;
        }

        public void MarkCompleted()
        {
            _completed = true;
        }

        public void Dispose()
        {
            _cts.Dispose();
        }
    }
}
