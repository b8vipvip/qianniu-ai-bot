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
    internal static class SlowResponseDiagnosticsUi
    {
        private const string InjectionTag = "slow-response-diagnostics-v1";
        private static int _initialized;

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            EventManager.RegisterClassHandler(
                typeof(FeatureSettingsWindow),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnFeatureSettingsLoaded),
                true);
        }

        private static void OnFeatureSettingsLoaded(object sender, RoutedEventArgs e)
        {
            var window = sender as FeatureSettingsWindow;
            if (window == null) return;
            try
            {
                var field = typeof(FeatureSettingsWindow).GetField("_tabs", BindingFlags.Instance | BindingFlags.NonPublic);
                var tabs = field == null ? null : field.GetValue(window) as TabControl;
                if (tabs == null) return;

                foreach (var item in tabs.Items)
                {
                    var tab = item as TabItem;
                    if (tab == null) continue;
                    var header = (tab.Header ?? string.Empty).ToString();
                    if (!string.Equals(header, "日志与调试", StringComparison.Ordinal)) continue;
                    if (string.Equals(Convert.ToString(tab.Tag), InjectionTag, StringComparison.Ordinal)) return;

                    var originalContent = tab.Content;
                    var nested = new TabControl
                    {
                        Margin = new Thickness(0)
                    };
                    nested.Items.Add(new TabItem
                    {
                        Header = "运行日志",
                        Content = originalContent
                    });
                    nested.Items.Add(new TabItem
                    {
                        Header = "异常报告",
                        Content = new SlowResponseAnomalyReportControl()
                    });
                    tab.Content = nested;
                    tab.Tag = InjectionTag;
                    return;
                }
            }
            catch (Exception ex)
            {
                BotLib.Log.Info("注入慢响应异常报告页面失败：" + ex.Message);
            }
        }
    }

    internal sealed class SlowResponseAnomalyReportControl : UserControl
    {
        private readonly ObservableCollection<SlowResponseAnomalyReport> _items =
            new ObservableCollection<SlowResponseAnomalyReport>();
        private readonly DataGrid _grid;
        private readonly TextBox _detail;
        private readonly TextBlock _summary;
        private bool _subscribed;

        public SlowResponseAnomalyReportControl()
        {
            var root = new DockPanel { Margin = new Thickness(8) };
            Content = root;

            var top = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8)
            };
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);

            var refresh = MakeButton("刷新异常报告", 110);
            refresh.Click += (s, e) => RefreshReports();
            top.Children.Add(refresh);

            var copy = MakeButton("复制选中报告", 110);
            copy.Click += (s, e) =>
            {
                var selected = _grid.SelectedItem as SlowResponseAnomalyReport;
                if (selected != null) Clipboard.SetText(SlowResponseAnomalyService.FormatReport(selected));
            };
            top.Children.Add(copy);

            var open = MakeButton("打开报告目录", 110);
            open.Click += (s, e) =>
            {
                try
                {
                    PathEx.OpenFolder(SlowResponseAnomalyService.ReportDirectory);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("打开报告目录失败：" + ex.Message, "异常报告", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            top.Children.Add(open);

            var clear = MakeButton("清空异常报告", 110);
            clear.Click += (s, e) =>
            {
                if (MessageBox.Show(
                    "确认清空本机保存的全部慢响应异常报告？此操作不会清空运行日志。",
                    "清空异常报告",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
                {
                    return;
                }
                try
                {
                    SlowResponseAnomalyService.ClearReports();
                    RefreshReports();
                }
                catch (Exception ex)
                {
                    MessageBox.Show("清空失败：" + ex.Message, "异常报告", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };
            top.Children.Add(clear);

            _summary = new TextBlock
            {
                Margin = new Thickness(0, 0, 0, 8),
                Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                TextWrapping = TextWrapping.Wrap
            };
            DockPanel.SetDock(_summary, Dock.Top);
            root.Children.Add(_summary);

            var content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(245) });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.Children.Add(content);

            _grid = new DataGrid
            {
                ItemsSource = _items,
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                SelectionMode = DataGridSelectionMode.Single,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal
            };
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = "时间",
                Binding = new Binding("CreatedAtText"),
                Width = new DataGridLength(120)
            });
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = "客服",
                Binding = new Binding("Seller"),
                Width = new DataGridLength(110)
            });
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = "买家",
                Binding = new Binding("Buyer"),
                Width = new DataGridLength(110)
            });
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = "总耗时(秒)",
                Binding = new Binding("TotalSeconds") { StringFormat = "0.0" },
                Width = new DataGridLength(90)
            });
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = "严重度",
                Binding = new Binding("Severity"),
                Width = new DataGridLength(70)
            });
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = "分析状态",
                Binding = new Binding("AnalysisStatus"),
                Width = new DataGridLength(160)
            });
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = "可能原因",
                Binding = new Binding("LikelyCause"),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star)
            });
            _grid.SelectionChanged += (s, e) => ShowSelectedDetail();
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
            RefreshReports();
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

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_subscribed) return;
            _subscribed = true;
            SlowResponseAnomalyService.ReportsChanged += OnReportsChanged;
            RefreshReports();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (!_subscribed) return;
            _subscribed = false;
            SlowResponseAnomalyService.ReportsChanged -= OnReportsChanged;
        }

        private void OnReportsChanged()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(RefreshReports));
                return;
            }
            RefreshReports();
        }

        private void RefreshReports()
        {
            try
            {
                var selectedId = (_grid == null ? null : _grid.SelectedItem as SlowResponseAnomalyReport)?.Id;
                var reports = SlowResponseAnomalyService.GetReports(200);
                _items.Clear();
                foreach (var report in reports) _items.Add(report);
                _summary.Text = "超过 " + SlowResponseAnomalyService.ThresholdSeconds
                    + " 秒会自动生成异常报告并调用AI分析。当前显示最近 " + reports.Count
                    + " 条，本机报告文件：" + SlowResponseAnomalyService.ReportFilePath;

                if (!string.IsNullOrWhiteSpace(selectedId))
                {
                    _grid.SelectedItem = _items.FirstOrDefault(x => string.Equals(x.Id, selectedId, StringComparison.Ordinal));
                }
                if (_grid.SelectedItem == null && _items.Count > 0) _grid.SelectedIndex = 0;
                ShowSelectedDetail();
            }
            catch (Exception ex)
            {
                _summary.Text = "读取异常报告失败：" + ex.Message;
            }
        }

        private void ShowSelectedDetail()
        {
            if (_detail == null) return;
            var selected = _grid.SelectedItem as SlowResponseAnomalyReport;
            _detail.Text = selected == null
                ? "暂无异常报告。"
                : SlowResponseAnomalyService.FormatReport(selected);
        }
    }
}
