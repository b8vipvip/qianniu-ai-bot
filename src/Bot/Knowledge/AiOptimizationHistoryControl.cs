using Bot.ChromeNs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Bot.Knowledge
{
    internal sealed class AiOptimizationHistoryControl : UserControl
    {
        private sealed class HistoryRow
        {
            public DateTime SortTime { get; set; }
            public string Time { get; set; }
            public string Type { get; set; }
            public string Buyer { get; set; }
            public string Status { get; set; }
            public string Accuracy { get; set; }
            public string Applied { get; set; }
            public string Summary { get; set; }
            public object Source { get; set; }
        }

        private readonly DataGrid _grid;
        private readonly TextBox _detail;
        private readonly TextBlock _summary;

        public AiOptimizationHistoryControl()
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });

            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var refresh = new Button
            {
                Content = "刷新记录",
                Width = 90,
                Height = 30,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            refresh.Click += delegate { RefreshData(); };
            DockPanel.SetDock(refresh, Dock.Right);
            header.Children.Add(refresh);

            _summary = new TextBlock
            {
                Text = "显示人工介入后的即时AI对比，以及接待结束后的整轮知识复盘记录。",
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            header.Children.Add(_summary);
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            _grid = new DataGrid
            {
                IsReadOnly = true,
                AutoGenerateColumns = false,
                SelectionMode = DataGridSelectionMode.Single,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                CanUserAddRows = false,
                CanUserDeleteRows = false
            };
            _grid.Columns.Add(new DataGridTextColumn { Header = "时间", Binding = new System.Windows.Data.Binding("Time"), Width = 120 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "类型", Binding = new System.Windows.Data.Binding("Type"), Width = 130 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "买家", Binding = new System.Windows.Data.Binding("Buyer"), Width = 150 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "状态", Binding = new System.Windows.Data.Binding("Status"), Width = 100 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "AI准确度", Binding = new System.Windows.Data.Binding("Accuracy"), Width = 85 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "知识应用", Binding = new System.Windows.Data.Binding("Applied"), Width = 85 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "摘要/问题", Binding = new System.Windows.Data.Binding("Summary"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            _grid.SelectionChanged += Grid_SelectionChanged;
            Grid.SetRow(_grid, 1);
            root.Children.Add(_grid);

            _detail = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 10, 0, 0)
            };
            Grid.SetRow(_detail, 2);
            root.Children.Add(_detail);

            Content = root;
            Loaded += delegate
            {
                AiManualReplyOptimizationService.RecordsChanged += RecordsChanged;
                ConversationSessionLearningService.ReportsChanged += RecordsChanged;
                RefreshData();
            };
            Unloaded += delegate
            {
                AiManualReplyOptimizationService.RecordsChanged -= RecordsChanged;
                ConversationSessionLearningService.ReportsChanged -= RecordsChanged;
            };
        }

        public void RefreshData()
        {
            try
            {
                var rows = new List<HistoryRow>();
                rows.AddRange(AiManualReplyOptimizationService.GetRecords(500).Select(x => new HistoryRow
                {
                    SortTime = x.CreatedAt,
                    Time = x.CreatedAtText,
                    Type = "即时AI/人工对比",
                    Buyer = x.Buyer,
                    Status = x.Status,
                    Accuracy = x.AccuracyText,
                    Applied = x.ApplyText,
                    Summary = Short(x.Question, 120),
                    Source = x
                }));
                rows.AddRange(ConversationSessionLearningService.GetReports(500).Select(x => new HistoryRow
                {
                    SortTime = x.CompletedAt == DateTime.MinValue ? x.LastBuyerAt : x.CompletedAt,
                    Time = x.CompletedAtText,
                    Type = "接待结束复盘",
                    Buyer = x.Buyer,
                    Status = x.Status,
                    Accuracy = "-",
                    Applied = x.AppliedCount + "/" + (x.AppliedCount + x.SkippedCount),
                    Summary = Short(x.Summary, 120),
                    Source = x
                }));
                rows = rows.OrderByDescending(x => x.SortTime).Take(1000).ToList();
                _grid.ItemsSource = rows;
                _summary.Text = "AI优化记录：共 " + rows.Count + " 条。即时对比会显示AI准确度、人工回复原因和知识策略；整轮复盘保留历史自动学习记录。";
                if (rows.Count > 0) _grid.SelectedIndex = 0;
                else _detail.Text = "暂无AI优化记录。";
            }
            catch (Exception ex)
            {
                _detail.Text = "读取AI优化记录失败：" + ex.Message;
            }
        }

        private void RecordsChanged()
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(RefreshData));
            }
            catch { }
        }

        private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var row = _grid.SelectedItem as HistoryRow;
            if (row == null)
            {
                _detail.Text = string.Empty;
                return;
            }
            var immediate = row.Source as AiOptimizationRecordView;
            if (immediate != null)
            {
                _detail.Text = AiManualReplyOptimizationService.FormatRecord(immediate);
                return;
            }
            var session = row.Source as ConversationSessionLearningReportView;
            _detail.Text = session == null ? string.Empty : ConversationSessionLearningService.FormatReport(session);
        }

        private static string Short(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }
}
