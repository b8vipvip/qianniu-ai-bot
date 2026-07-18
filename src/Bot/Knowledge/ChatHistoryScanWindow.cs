using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bot.Knowledge
{
    public sealed class ChatHistoryScanWindow : Window
    {
        private RadioButton _all;
        private RadioButton _range;
        private DatePicker _start;
        private DatePicker _end;
        private Button _startButton;
        private Button _cancelButton;
        private Button _closeButton;
        private TextBox _progress;
        private TextBlock _summary;
        private CancellationTokenSource _cts;
        private bool _running;

        public ChatHistoryScanWindow()
        {
            Title = "扫描历史聊天记录";
            Width = 680;
            Height = 590;
            MinWidth = 620;
            MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Brushes.White;
            Build();
            Closing += (s, e) =>
            {
                if (_running && _cts != null) _cts.Cancel();
            };
        }

        private void Build()
        {
            var root = new DockPanel { Margin = new Thickness(16) };
            Content = root;

            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            _startButton = Button("开始扫描", 100, Start_Click);
            _cancelButton = Button("取消任务", 90, Cancel_Click);
            _cancelButton.IsEnabled = false;
            _closeButton = Button("关闭", 80, (s, e) => Close());
            footer.Children.Add(_startButton);
            footer.Children.Add(_cancelButton);
            footer.Children.Add(_closeButton);

            var body = new StackPanel();
            root.Children.Add(body);

            body.Children.Add(new TextBlock
            {
                Text = "扫描历史聊天记录",
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55))
            });
            body.Children.Add(new TextBlock
            {
                Text = "系统会优先读取千牛聊天界面左侧“全部买家”列表；只有列表读取不到时才尝试独立“消息管理器”，并通过千牛历史消息接口分页获取聊天记录。只整理买家提问后客服已经回答的轮次，不向买家发送任何消息。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(75, 85, 99)),
                Margin = new Thickness(0, 8, 0, 14)
            });

            var optionsBorder = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Background = new SolidColorBrush(Color.FromRgb(248, 250, 252))
            };
            body.Children.Add(optionsBorder);

            var options = new StackPanel();
            optionsBorder.Child = options;

            _all = new RadioButton
            {
                Content = "全部扫描",
                IsChecked = true,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            };
            _range = new RadioButton
            {
                Content = "按时间段扫描",
                FontWeight = FontWeights.SemiBold
            };
            _all.Checked += (s, e) => RefreshRangeState();
            _range.Checked += (s, e) => RefreshRangeState();
            options.Children.Add(_all);
            options.Children.Add(_range);

            var dates = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(22, 8, 0, 0)
            };
            options.Children.Add(dates);
            dates.Children.Add(new TextBlock { Text = "开始日期：", VerticalAlignment = VerticalAlignment.Center });
            _start = new DatePicker
            {
                Width = 150,
                SelectedDate = DateTime.Today.AddDays(-30),
                Margin = new Thickness(0, 0, 16, 0)
            };
            dates.Children.Add(_start);
            dates.Children.Add(new TextBlock { Text = "结束日期：", VerticalAlignment = VerticalAlignment.Center });
            _end = new DatePicker
            {
                Width = 150,
                SelectedDate = DateTime.Today
            };
            dates.Children.Add(_end);
            RefreshRangeState();

            body.Children.Add(new TextBlock
            {
                Text = "处理说明",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 14, 0, 5)
            });
            body.Children.Add(new TextBlock
            {
                Text = "• 联系人优先从聊天界面左侧“全部买家”列表读取，消息管理器和千牛联系人接口作为补充。\n" +
                       "• 每个买家通过会话编号分页读取历史消息，扫描结束后恢复原聊天窗口。\n" +
                       "• 聊天记录先整理成问答轮次，再沿用“智能导入”的分段、超时、重试和去重逻辑。\n" +
                       "• 手机号、长订单号及 API Key 会先脱敏；系统提示、撤回提示和未回答问题不会进入知识库。\n" +
                       "• 全量扫描耗时与联系人数量、历史消息数量和 AI 接口速度有关，可随时取消。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(75, 85, 99))
            });

            _summary = new TextBlock
            {
                Text = "尚未开始。",
                Margin = new Thickness(0, 14, 0, 6),
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(37, 99, 235))
            };
            body.Children.Add(_summary);

            _progress = new TextBox
            {
                Height = 210,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                Padding = new Thickness(10)
            };
            body.Children.Add(_progress);
        }

        private static Button Button(string text, double width, RoutedEventHandler handler)
        {
            var button = new Button
            {
                Content = text,
                Width = width,
                Height = 32,
                Margin = new Thickness(8, 0, 0, 0)
            };
            button.Click += handler;
            return button;
        }

        private void RefreshRangeState()
        {
            var enabled = _range != null && _range.IsChecked == true;
            if (_start != null) _start.IsEnabled = enabled;
            if (_end != null) _end.IsEnabled = enabled;
        }

        private async void Start_Click(object sender, RoutedEventArgs e)
        {
            if (_running) return;
            var options = BuildOptions();
            if (options == null) return;

            _running = true;
            _cts = new CancellationTokenSource();
            SetRunningState(true);
            _summary.Text = "正在准备扫描...";
            _progress.Text = string.Empty;

            try
            {
                var service = new ChatHistoryScanService();
                var result = await service.ScanAndImportAsync(
                    options,
                    p => Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _progress.Text = p == null ? string.Empty : p.ToString();
                        _progress.ScrollToEnd();
                    })),
                    _cts.Token);

                var imported = result.ImportResult ?? new KnowledgeImportResult();
                var sb = new StringBuilder();
                sb.Append("扫描完成：联系人 ").Append(result.ScannedContacts).Append("/").Append(result.ContactCount);
                sb.Append("，有效聊天消息 ").Append(result.MessageCount);
                sb.Append("，有效问答轮次 ").Append(result.PairCount);
                sb.Append("，新增知识 ").Append(imported.Added);
                sb.Append("，跳过重复 ").Append(imported.DuplicateSkipped);
                if (result.FailedContacts > 0) sb.Append("，读取失败 ").Append(result.FailedContacts);
                _summary.Text = sb.ToString();

                _progress.Text = sb + Environment.NewLine
                    + "联系人来源：全部买家列表 " + result.ChatBuyerListContactCount
                    + "，消息管理器 " + result.MessageManagerContactCount
                    + "，接口 " + result.ApiContactCount
                    + Environment.NewLine
                    + "消息管理器：" + (result.MessageManagerOpened
                        ? "已作为兜底自动打开"
                        : (result.ChatBuyerListContactCount > 0
                            ? "未打开（已从左侧全部买家列表读取）"
                            : "未找到入口，已使用接口或当前会话兜底"))
                    + (string.IsNullOrWhiteSpace(result.Diagnostics)
                        ? string.Empty
                        : Environment.NewLine + Environment.NewLine + "诊断信息：" + Environment.NewLine + result.Diagnostics);
            }
            catch (OperationCanceledException)
            {
                _summary.Text = "扫描任务已取消；已经完成并保存的 AI 批次不会回滚。";
                _progress.AppendText(Environment.NewLine + "任务已取消。");
            }
            catch (SmartImportException ex)
            {
                _summary.Text = "扫描后的知识生成未完成：" + ex.Message;
                _progress.AppendText(Environment.NewLine + ex.Message);
            }
            catch (Exception ex)
            {
                _summary.Text = "扫描失败：" + ex.Message;
                _progress.AppendText(Environment.NewLine + "失败：" + ex.Message);
            }
            finally
            {
                _running = false;
                SetRunningState(false);
                if (_cts != null)
                {
                    _cts.Dispose();
                    _cts = null;
                }
            }
        }

        private ChatHistoryScanOptions BuildOptions()
        {
            var all = _all.IsChecked == true;
            if (!all && (!_start.SelectedDate.HasValue || !_end.SelectedDate.HasValue))
            {
                MessageBox.Show("请选择开始日期和结束日期。", "扫描历史聊天记录", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }
            if (!all && _start.SelectedDate.Value.Date > _end.SelectedDate.Value.Date)
            {
                MessageBox.Show("开始日期不能晚于结束日期。", "扫描历史聊天记录", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }
            return new ChatHistoryScanOptions
            {
                ScanAll = all,
                StartTime = all ? (DateTime?)null : _start.SelectedDate.Value.Date,
                EndTime = all ? (DateTime?)null : _end.SelectedDate.Value.Date,
                MaxContacts = 1000
            };
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null) _cts.Cancel();
        }

        private void SetRunningState(bool running)
        {
            _startButton.IsEnabled = !running;
            _cancelButton.IsEnabled = running;
            _closeButton.IsEnabled = !running;
            _all.IsEnabled = !running;
            _range.IsEnabled = !running;
            RefreshRangeState();
            if (running)
            {
                _start.IsEnabled = false;
                _end.IsEnabled = false;
            }
        }
    }
}
