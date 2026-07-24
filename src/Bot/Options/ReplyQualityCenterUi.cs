using Bot.ChromeNs;
using BotLib.Extensions;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Bot.Options
{
    internal static class ReplyQualityCenterUi
    {
        private const string TabHeader = "回复质量中心";
        private static int _initialized;

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            EventManager.RegisterClassHandler(
                typeof(FeatureSettingsWindow),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnWindowLoaded),
                true);
        }

        private static void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            var window = sender as FeatureSettingsWindow;
            if (window == null) return;
            TryInject(window, 0);
        }

        private static void TryInject(FeatureSettingsWindow window, int attempt)
        {
            if (window == null || attempt > 6) return;
            try
            {
                var field = typeof(FeatureSettingsWindow).GetField(
                    "_tabs",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var rootTabs = field == null ? null : field.GetValue(window) as TabControl;
                if (rootTabs == null) return;
                var logTab = rootTabs.Items.OfType<TabItem>().FirstOrDefault(x => string.Equals(
                    Convert.ToString(x.Header),
                    "日志与调试",
                    StringComparison.Ordinal));
                if (logTab == null) return;
                var nested = logTab.Content as TabControl;
                if (nested == null)
                {
                    window.Dispatcher.BeginInvoke(new Action(() => TryInject(window, attempt + 1)));
                    return;
                }
                if (nested.Items.OfType<TabItem>().Any(x => string.Equals(
                    Convert.ToString(x.Header),
                    TabHeader,
                    StringComparison.Ordinal)))
                {
                    return;
                }
                nested.Items.Add(new TabItem
                {
                    Header = TabHeader,
                    Content = new ReplyQualityCenterControl()
                });
            }
            catch (Exception ex)
            {
                BotLib.Log.ErrorWithMaxCount("注入回复质量中心失败：" + ex.Message, 10);
            }
        }
    }

    internal sealed class ReplyQualityCenterControl : UserControl
    {
        private sealed class RangeOption
        {
            public string Name { get; set; }
            public int Days { get; set; }
            public override string ToString() { return Name; }
        }

        private readonly ObservableCollection<ReplyQualityDailyView> _daily =
            new ObservableCollection<ReplyQualityDailyView>();
        private readonly ComboBox _range;
        private readonly TextBlock _score;
        private readonly TextBlock _routeSummary;
        private readonly TextBlock _qualitySummary;
        private readonly TextBlock _latencySummary;
        private readonly TextBox _detail;
        private readonly DataGrid _grid;
        private bool _subscribed;
        private ReplyQualitySummary _current;

        public ReplyQualityCenterControl()
        {
            var root = new DockPanel { Margin = new Thickness(8) };
            Content = root;

            var toolbar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8)
            };
            DockPanel.SetDock(toolbar, Dock.Top);
            root.Children.Add(toolbar);

            toolbar.Children.Add(new TextBlock
            {
                Text = "统计范围：",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            });
            _range = new ComboBox
            {
                Width = 105,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 0),
                ItemsSource = new[]
                {
                    new RangeOption { Name = "今天", Days = 1 },
                    new RangeOption { Name = "最近7天", Days = 7 },
                    new RangeOption { Name = "最近30天", Days = 30 },
                    new RangeOption { Name = "最近90天", Days = 90 }
                },
                SelectedIndex = 1
            };
            _range.SelectionChanged += (s, e) => RefreshMetrics();
            toolbar.Children.Add(_range);

            var refresh = MakeButton("刷新指标", 90);
            refresh.Click += (s, e) => RefreshMetrics();
            toolbar.Children.Add(refresh);

            var copy = MakeButton("复制质量报告", 105);
            copy.Click += (s, e) =>
            {
                if (_current != null) Clipboard.SetText(ReplyQualityMetricsService.FormatSummary(_current));
            };
            toolbar.Children.Add(copy);

            var open = MakeButton("打开数据目录", 105);
            open.Click += (s, e) =>
            {
                try { PathEx.OpenFolder(ReplyQualityMetricsService.MetricsDirectory); }
                catch (Exception ex)
                {
                    MessageBox.Show("打开数据目录失败：" + ex.Message, "回复质量中心", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            toolbar.Children.Add(open);

            var cards = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            DockPanel.SetDock(cards, Dock.Top);
            root.Children.Add(cards);

            _score = Card("质量分\n暂无数据", 22, FontWeights.Bold);
            _routeSummary = Card("路由分布\n暂无数据", 13, FontWeights.Normal);
            _qualitySummary = Card("校验与发送\n暂无数据", 13, FontWeights.Normal);
            _latencySummary = Card("回复延迟\n暂无数据", 13, FontWeights.Normal);
            AddCard(cards, _score, 0);
            AddCard(cards, _routeSummary, 1);
            AddCard(cards, _qualitySummary, 2);
            AddCard(cards, _latencySummary, 3);

            var content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(245) });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.Children.Add(content);

            _grid = new DataGrid
            {
                ItemsSource = _daily,
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                SelectionMode = DataGridSelectionMode.Single
            };
            _grid.Columns.Add(Column("日期", "Date", 90));
            _grid.Columns.Add(Column("路由", "TotalRoutes", 58));
            _grid.Columns.Add(Column("直答", "Direct", 52));
            _grid.Columns.Add(Column("上下文", "Contextual", 62));
            _grid.Columns.Add(Column("通用AI", "GeneralAi", 62));
            _grid.Columns.Add(Column("视觉", "Vision", 52));
            _grid.Columns.Add(Column("人工", "Manual", 52));
            _grid.Columns.Add(Column("校验通过", "ValidatorPass", 72));
            _grid.Columns.Add(Column("重答", "ValidatorRepair", 52));
            _grid.Columns.Add(Column("阻止", "ValidatorManual", 52));
            _grid.Columns.Add(Column("发送成功", "SendSuccess", 72));
            _grid.Columns.Add(Column("发送失败", "SendFailure", 72));
            _grid.Columns.Add(Column("人工纠正", "HumanCorrections", 72));
            _grid.Columns.Add(Column("平均答案ms", "AverageAnswerMs", 88));
            _grid.Columns.Add(Column("P95答案ms", "P95AnswerMs", 85));
            Grid.SetRow(_grid, 0);
            content.Children.Add(_grid);

            var splitter = new GridSplitter
            {
                Height = 8,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                ResizeDirection = GridResizeDirection.Rows
            };
            Grid.SetRow(splitter, 1);
            content.Children.Add(splitter);

            _detail = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                Padding = new Thickness(8)
            };
            Grid.SetRow(_detail, 2);
            content.Children.Add(_detail);

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            RefreshMetrics();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_subscribed) return;
            _subscribed = true;
            ReplyQualityMetricsService.MetricsChanged += OnMetricsChanged;
            RefreshMetrics();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (!_subscribed) return;
            _subscribed = false;
            ReplyQualityMetricsService.MetricsChanged -= OnMetricsChanged;
        }

        private void OnMetricsChanged()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(RefreshMetrics));
                return;
            }
            RefreshMetrics();
        }

        private void RefreshMetrics()
        {
            try
            {
                var option = _range == null ? null : _range.SelectedItem as RangeOption;
                var days = option == null ? 7 : option.Days;
                _current = ReplyQualityMetricsService.GetSummary(days);
                _daily.Clear();
                foreach (var item in _current.Daily) _daily.Add(item);

                _score.Text = _current.QualityScore <= 0 && _current.TotalRoutes == 0
                    ? "质量分\n暂无数据"
                    : "质量分\n" + _current.QualityScore + "/100";
                _routeSummary.Text = "路由分布\n直答 " + _current.RouteDirect
                    + " · 上下文 " + _current.RouteContextual
                    + " · 通用AI " + _current.RouteGeneralAi
                    + " · 视觉 " + _current.RouteVision
                    + " · 人工 " + _current.RouteManual;
                _qualitySummary.Text = "校验与发送\n校验通过 " + Percent(_current.ValidatorPassRate)
                    + " · 重答 " + _current.ValidationRegenerate
                    + " · 阻止 " + _current.ValidationManual
                    + "\n发送成功 " + Percent(_current.SendSuccessRate)
                    + " · 人工纠正 " + Percent(_current.HumanCorrectionRate);
                _latencySummary.Text = "回复延迟\n答案平均 " + _current.AverageAnswerLatencyMs
                    + "ms · P95 " + _current.P95AnswerLatencyMs
                    + "ms\n完整发送平均 " + _current.AverageTotalSendLatencyMs
                    + "ms · P95 " + _current.P95TotalSendLatencyMs + "ms";
                _detail.Text = ReplyQualityMetricsService.FormatSummary(_current)
                    + "\n\n数据说明：仅保存按天汇总的计数、延迟样本和问题类别，不保存买家名称、聊天内容、答案正文或订单信息。\n数据文件："
                    + ReplyQualityMetricsService.MetricsFilePath;
            }
            catch (Exception ex)
            {
                _detail.Text = "读取回复质量指标失败：" + ex.Message;
            }
        }

        private static TextBlock Card(string text, double fontSize, FontWeight weight)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = fontSize,
                FontWeight = weight,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
                Foreground = new SolidColorBrush(Color.FromRgb(30, 41, 59))
            };
        }

        private static void AddCard(Grid grid, UIElement element, int column)
        {
            Grid.SetColumn(element, column);
            grid.Children.Add(element);
        }

        private static Button MakeButton(string text, double width)
        {
            return new Button
            {
                Content = text,
                Width = width,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 0),
                Padding = new Thickness(8, 2, 8, 2)
            };
        }

        private static DataGridTextColumn Column(string header, string binding, double width)
        {
            return new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(binding),
                Width = new DataGridLength(width)
            };
        }

        private static string Percent(double value)
        {
            return (Math.Max(0, Math.Min(1, value)) * 100).ToString("0.0") + "%";
        }
    }
}
