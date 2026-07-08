using System;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Bot.ChromeNs;
using BotLib;

namespace Bot.AssistWindow.Widget.Robot
{
    public partial class CtlRobot
    {
        private DispatcherTimer _diagnosticsTimer;
        private DataDeskWindow _dataDeskWindow;

        public bool IsDataDeskVisible
        {
            get { return _dataDeskWindow != null && _dataDeskWindow.IsVisible; }
        }

        protected override void OnInitialized(EventArgs e)
        {
            base.OnInitialized(e);
            _diagnosticsTimer = new DispatcherTimer();
            _diagnosticsTimer.Interval = TimeSpan.FromSeconds(2);
            _diagnosticsTimer.Tick += (s, args) => RefreshStatusDiagnostics();
            _diagnosticsTimer.Start();
        }

        public void ShowDataDesk(Window owner)
        {
            try
            {
                if (_dataDeskWindow != null && _dataDeskWindow.IsVisible)
                {
                    _dataDeskWindow.Activate();
                    _dataDeskWindow.DockToOwner();
                    return;
                }

                _dataDeskWindow = new DataDeskWindow(owner);
                _dataDeskWindow.Closed += (s, e) => _dataDeskWindow = null;
                _dataDeskWindow.Show();
                _dataDeskWindow.DockToOwner();
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
        }

        public void CloseDataDesk()
        {
            try
            {
                if (_dataDeskWindow != null)
                {
                    _dataDeskWindow.Close();
                    _dataDeskWindow = null;
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
        }

        private string BuildApiStatus(RuntimeStatsSnapshot snapshot)
        {
            try
            {
                var endpoints = AiEndpointStore.GetEnabledEndpoints();
                if (endpoints == null || endpoints.Count < 1) return "未配置";
                if (snapshot != null && snapshot.ApiUsages != null && snapshot.ApiUsages.Count > 0)
                {
                    var first = snapshot.ApiUsages.FirstOrDefault();
                    if (first != null && !string.IsNullOrWhiteSpace(first.LastStatus))
                    {
                        return first.LastStatus + "｜今日" + first.TodayCalls + "次";
                    }
                }
                return "已配置" + endpoints.Count + "个";
            }
            catch
            {
                return "检测失败";
            }
        }

        private string BuildConnectionDetail(ConnectionDiagnosticsSnapshot diag)
        {
            if (diag == null) return string.Empty;
            var sb = new StringBuilder();
            sb.AppendLine("WS连接：" + diag.WebSocketStatus);
            sb.AppendLine("注入状态：" + diag.InjectionStatus);
            sb.AppendLine("千牛参数：" + diag.QnParamStatus);
            sb.AppendLine("无障碍/可访问UI：" + diag.AccessibilityStatus);
            sb.AppendLine("发送按钮识别：" + diag.ButtonStatus);
            sb.AppendLine("最近发送结果：" + diag.SendStatus);
            return sb.ToString().Trim();
        }

        private SolidColorBrush StatusBrush(bool ok)
        {
            return new SolidColorBrush(ok ? Color.FromRgb(39, 174, 96) : Color.FromRgb(242, 153, 74));
        }

        private void RefreshStatusDiagnostics()
        {
            try
            {
                var diag = BotConnectionDiagnostics.GetSnapshot();
                var stats = BotRuntimeStats.GetSnapshot();
                var connectionOk = diag != null && diag.Summary == "连接正常";
                var summary = diag == null || string.IsNullOrWhiteSpace(diag.Summary) ? "正在检测" : diag.Summary;
                var detail = BuildConnectionDetail(diag);

                if (txtStatusSummary != null)
                {
                    txtStatusSummary.Text = summary;
                    txtStatusSummary.Foreground = StatusBrush(connectionOk);
                    txtStatusSummary.ToolTip = detail;
                }
                if (txtConnectionStatus != null)
                {
                    txtConnectionStatus.Text = "连接状态：" + summary;
                    txtConnectionStatus.Foreground = StatusBrush(connectionOk);
                    txtConnectionStatus.ToolTip = detail;
                }
                if (txtStatusApi != null)
                {
                    txtStatusApi.Text = "API连接：" + BuildApiStatus(stats);
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
        }
    }

    internal class DataDeskWindow : Window
    {
        private readonly Window _owner;
        private readonly DispatcherTimer _timer;
        private TextBlock _txtTotalReception;
        private TextBlock _txtTodayReception;
        private TextBlock _txtTotalAutoReply;
        private TextBlock _txtTodayAutoReply;
        private TextBlock _txtTotalAiCall;
        private TextBlock _txtTodayAiCall;
        private TextBlock _txtTotalTokens;
        private TextBlock _txtTodayTokens;
        private TextBlock _txtAvgLatency;
        private TextBlock _txtLastError;
        private StackPanel _panelApiStats;

        public DataDeskWindow(Window owner)
        {
            _owner = owner;
            Title = "数据台";
            Width = 335;
            MinWidth = 300;
            Height = owner != null && owner.ActualHeight > 200 ? owner.ActualHeight : 680;
            WindowStartupLocation = WindowStartupLocation.Manual;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.CanResize;
            Background = new SolidColorBrush(Color.FromRgb(247, 249, 252));
            Content = BuildContent();

            if (_owner != null)
            {
                Owner = _owner;
                _owner.LocationChanged += OwnerChanged;
                _owner.SizeChanged += OwnerChanged;
            }
            Closed += DataDeskWindow_Closed;
            Loaded += (s, e) => DockToOwner();

            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(3);
            _timer.Tick += (s, e) => RefreshStats();
            _timer.Start();
            RefreshStats();
        }

        private void DataDeskWindow_Closed(object sender, EventArgs e)
        {
            if (_timer != null) _timer.Stop();
            if (_owner != null)
            {
                _owner.LocationChanged -= OwnerChanged;
                _owner.SizeChanged -= OwnerChanged;
            }
        }

        private void OwnerChanged(object sender, EventArgs e)
        {
            DockToOwner();
        }

        public void DockToOwner()
        {
            try
            {
                if (_owner == null) return;
                Left = _owner.Left + _owner.ActualWidth + 6;
                Top = _owner.Top;
                if (_owner.ActualHeight > 200) Height = _owner.ActualHeight;
            }
            catch
            {
            }
        }

        private UIElement BuildContent()
        {
            var root = new DockPanel { Margin = new Thickness(10) };
            var title = new TextBlock
            {
                Text = "数据台",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55)),
                Margin = new Thickness(0, 0, 0, 10)
            };
            DockPanel.SetDock(title, Dock.Top);
            root.Children.Add(title);

            var scv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            root.Children.Add(scv);
            var sp = new StackPanel();
            scv.Content = sp;

            sp.Children.Add(Card(BuildReceptionPanel()));
            sp.Children.Add(Card(BuildAiPanel()));
            sp.Children.Add(Card(BuildApiPanel()));
            return root;
        }

        private Border Card(UIElement child)
        {
            return new Border
            {
                Child = child,
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(230, 236, 242)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 8)
            };
        }

        private UIElement BuildReceptionPanel()
        {
            var sp = new StackPanel();
            sp.Children.Add(SectionTitle("接待概览"));
            var grid = new UniformGrid { Columns = 2 };
            _txtTotalReception = AddMetric(grid, "总接待");
            _txtTodayReception = AddMetric(grid, "今日接待");
            _txtTotalAutoReply = AddMetric(grid, "总自动回复");
            _txtTodayAutoReply = AddMetric(grid, "今日自动回复");
            sp.Children.Add(grid);
            return sp;
        }

        private UIElement BuildAiPanel()
        {
            var sp = new StackPanel();
            sp.Children.Add(SectionTitle("AI调用"));
            var grid = new UniformGrid { Columns = 2 };
            _txtTotalAiCall = AddMetric(grid, "总调用");
            _txtTodayAiCall = AddMetric(grid, "今日调用");
            _txtTotalTokens = AddMetric(grid, "总Token");
            _txtTodayTokens = AddMetric(grid, "今日Token");
            sp.Children.Add(grid);
            _txtAvgLatency = SmallText("平均耗时：0ms");
            _txtAvgLatency.Margin = new Thickness(0, 8, 0, 0);
            sp.Children.Add(_txtAvgLatency);
            _txtLastError = SmallText(string.Empty);
            _txtLastError.Foreground = new SolidColorBrush(Color.FromRgb(235, 87, 87));
            _txtLastError.TextWrapping = TextWrapping.Wrap;
            sp.Children.Add(_txtLastError);
            return sp;
        }

        private UIElement BuildApiPanel()
        {
            var sp = new StackPanel();
            sp.Children.Add(SectionTitle("各API接口消耗"));
            _panelApiStats = new StackPanel();
            sp.Children.Add(_panelApiStats);
            return sp;
        }

        private TextBlock SectionTitle(string text)
        {
            return new TextBlock { Text = text, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55)), Margin = new Thickness(0, 0, 0, 8) };
        }

        private TextBlock SmallText(string text)
        {
            return new TextBlock { Text = text, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)) };
        }

        private TextBlock AddMetric(Panel parent, string label)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 8, 8) };
            sp.Children.Add(SmallText(label));
            var value = new TextBlock { Text = "0", FontSize = 18, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55)) };
            sp.Children.Add(value);
            parent.Children.Add(sp);
            return value;
        }

        private string FormatNum(long value)
        {
            if (value >= 1000000) return (value / 10000.0).ToString("0.0") + "万";
            return value.ToString();
        }

        private void RefreshStats()
        {
            try
            {
                var snapshot = BotRuntimeStats.GetSnapshot();
                _txtTotalReception.Text = FormatNum(snapshot.TotalReceptionCount);
                _txtTodayReception.Text = FormatNum(snapshot.TodayReceptionCount);
                _txtTotalAutoReply.Text = FormatNum(snapshot.TotalAutoReplies);
                _txtTodayAutoReply.Text = FormatNum(snapshot.TodayAutoReplies);
                _txtTotalAiCall.Text = FormatNum(snapshot.TotalAiCalls);
                _txtTodayAiCall.Text = FormatNum(snapshot.TodayAiCalls);
                _txtTotalTokens.Text = FormatNum(snapshot.TotalTokens);
                _txtTodayTokens.Text = FormatNum(snapshot.TodayTokens);
                _txtAvgLatency.Text = "平均耗时：" + snapshot.AvgLatencyMs + "ms，失败：" + snapshot.TodayAiFailedCalls + "/今日";
                _txtLastError.Text = string.IsNullOrWhiteSpace(snapshot.LastError) ? string.Empty : "最近错误：" + snapshot.LastError;

                _panelApiStats.Children.Clear();
                if (snapshot.ApiUsages == null || snapshot.ApiUsages.Count < 1)
                {
                    _panelApiStats.Children.Add(SmallText("暂无API调用数据"));
                    return;
                }

                foreach (var api in snapshot.ApiUsages)
                {
                    var border = new Border
                    {
                        Background = Brushes.White,
                        BorderBrush = new SolidColorBrush(Color.FromRgb(230, 236, 242)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(8),
                        Margin = new Thickness(0, 0, 0, 6)
                    };
                    var sp = new StackPanel();
                    sp.Children.Add(new TextBlock { Text = api.EndpointName, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55)) });
                    sp.Children.Add(SmallText("今日调用 " + api.TodayCalls + " 次｜今日Token " + api.TodayTokens));
                    sp.Children.Add(SmallText("总Token " + api.TotalTokens + "｜失败 " + api.FailedCalls + "｜均耗时 " + api.AvgLatencyMs + "ms"));
                    border.Child = sp;
                    _panelApiStats.Children.Add(border);
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
        }
    }
}