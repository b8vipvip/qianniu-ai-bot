using Bot.AssistWindow.Widget.Robot;
using Bot.ChromeNs;
using Bot.Options;
using Bot.ShopScope;
using BotLib;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Bot.AssistWindow
{
    /// <summary>
    /// Independent top-level Bot workbench.
    ///
    /// This window does not own or control a Qianniu Desk HWND. Qianniu discovery,
    /// message receiving/sending and all runtime business logic continue to use the
    /// existing Desk/WndAssist/QN pipeline. The window only hosts existing controls
    /// and mirrors UI events through DesktopBotUiBridge.
    /// </summary>
    public sealed class BotDesktopWindow : Window
    {
        private static BotDesktopWindow _current;
        private readonly CtlRobot _robot;
        private readonly CheckBox _botEnabled;
        private readonly CheckBox _autoReply;
        private readonly TextBlock _connection;
        private readonly TextBlock _shop;
        private readonly DispatcherTimer _statusTimer;
        private string _lastSeller;

        public static BotDesktopWindow Current
        {
            get { return _current; }
        }

        public static void ShowMain()
        {
            if (Application.Current == null) return;
            if (!Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(ShowMain));
                return;
            }

            if (_current == null)
            {
                _current = new BotDesktopWindow();
                _current.Closed += delegate { _current = null; };
                _current.Show();
            }
            else
            {
                if (!_current.IsVisible) _current.Show();
                if (_current.WindowState == WindowState.Minimized) _current.WindowState = WindowState.Normal;
                _current.Activate();
            }
        }

        public BotDesktopWindow()
        {
            Title = "Qianniu AI Bot 工作台";
            Width = 820;
            Height = 760;
            MinWidth = 620;
            MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResize;
            ShowInTaskbar = true;
            Background = new SolidColorBrush(Color.FromRgb(247, 249, 252));

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Content = root;

            var header = BuildHeader(out _botEnabled, out _autoReply);
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            var status = BuildConnectionBar(out _connection, out _shop);
            Grid.SetRow(status, 1);
            root.Children.Add(status);

            _robot = new CtlRobot(null, null);
            Grid.SetRow(_robot, 2);
            root.Children.Add(_robot);

            Loaded += OnLoaded;
            Closed += OnClosed;
            Activated += delegate { RefreshStatus(); };

            _statusTimer = new DispatcherTimer();
            _statusTimer.Interval = TimeSpan.FromSeconds(1);
            _statusTimer.Tick += delegate { RefreshStatus(); };
        }

        private Grid BuildHeader(out CheckBox botEnabled, out CheckBox autoReply)
        {
            var header = new Grid
            {
                Height = 50,
                Background = new SolidColorBrush(Color.FromRgb(47, 128, 237))
            };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var i = 0; i < 6; i++) header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 0, 0, 0)
            };
            title.Children.Add(new TextBlock
            {
                Text = "AI客服工作台",
                Foreground = Brushes.White,
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            });
            title.Children.Add(new TextBlock
            {
                Text = "  独立窗口",
                Foreground = new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(title, 0);
            header.Children.Add(title);

            var botToggle = CreateHeaderCheckBox("启用Bot");
            botToggle.Click += delegate
            {
                Params.Robot.CanUseRobot = botToggle.IsChecked ?? true;
                RefreshSwitches();
                Log.Info("Bot总开关=" + (Params.Robot.CanUseRobotReal ? "启用" : "停用"));
            };
            Grid.SetColumn(botToggle, 1);
            header.Children.Add(botToggle);
            botEnabled = botToggle;

            var autoToggle = CreateHeaderCheckBox("自动回复");
            autoToggle.Click += delegate
            {
                Params.Robot.SetIsAutoReply(autoToggle.IsChecked ?? false);
                RefreshSwitches();
                Log.Info("自动回复=" + ((autoToggle.IsChecked ?? false) ? "开启" : "关闭"));
            };
            Grid.SetColumn(autoToggle, 2);
            header.Children.Add(autoToggle);
            autoReply = autoToggle;

            var dataButton = CreateHeaderButton("数据台", "显示或隐藏接待与 AI 调用统计");
            dataButton.Click += delegate
            {
                if (_robot.IsDataDeskVisible) _robot.CloseDataDesk();
                else _robot.ShowDataDesk(this);
            };
            Grid.SetColumn(dataButton, 3);
            header.Children.Add(dataButton);

            var settingsButton = CreateHeaderButton("设置", "打开统一 Bot 设置");
            settingsButton.Click += delegate { OpenSettings(); };
            Grid.SetColumn(settingsButton, 4);
            header.Children.Add(settingsButton);

            var hideButton = CreateHeaderButton("隐藏到托盘", "隐藏独立窗口；Bot 后台服务继续运行");
            hideButton.Click += delegate { Hide(); };
            Grid.SetColumn(hideButton, 5);
            header.Children.Add(hideButton);

            return header;
        }

        private Border BuildConnectionBar(out TextBlock connection, out TextBlock shop)
        {
            var border = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(230, 236, 242)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(12, 7, 12, 7)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            border.Child = grid;

            connection = new TextBlock
            {
                Text = "千牛：等待连接",
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(242, 153, 74)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 16, 0)
            };
            Grid.SetColumn(connection, 0);
            grid.Children.Add(connection);

            shop = new TextBlock
            {
                Text = "当前店铺：尚未识别",
                Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(shop, 1);
            grid.Children.Add(shop);
            return border;
        }

        private static CheckBox CreateHeaderCheckBox(string text)
        {
            return new CheckBox
            {
                Content = text,
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 4, 0)
            };
        }

        private static Button CreateHeaderButton(string text, string tooltip)
        {
            return new Button
            {
                Content = text,
                ToolTip = tooltip,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(4, 7, 4, 7),
                Cursor = System.Windows.Input.Cursors.Hand
            };
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            DesktopBotUiBridge.Register(_robot);
            RefreshStatus();
            _statusTimer.Start();
            Log.Info("独立Bot工作台已显示；千牛窗口生命周期不再是该窗口的显示前提。 ");
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _statusTimer.Stop();
            _robot.CloseDataDesk();
            DesktopBotUiBridge.Unregister(_robot);
        }

        private void RefreshSwitches()
        {
            _botEnabled.IsChecked = Params.Robot.CanUseRobotReal;
            _autoReply.IsChecked = Params.Robot.GetIsAutoReply();
            _autoReply.IsEnabled = Params.Robot.CanUseRobotReal;
            _autoReply.Opacity = Params.Robot.CanUseRobotReal ? 1.0 : 0.55;
            _robot.RefreshSwitchState();
        }

        private void RefreshStatus()
        {
            try
            {
                RefreshSwitches();
                var diag = BotConnectionDiagnostics.GetSnapshot();
                var seller = diag == null ? string.Empty : (diag.Seller ?? string.Empty).Trim();
                if (seller.Length == 0 && QN.CurQN != null && QN.CurQN.Seller != null)
                {
                    seller = (QN.CurQN.Seller.Nick ?? string.Empty).Trim();
                }
                if (seller.Length > 0)
                {
                    _lastSeller = seller;
                    DesktopBotUiBridge.ChangeSeller(seller);
                    AttachShopScope(seller);
                }
                else if (string.IsNullOrWhiteSpace(_lastSeller))
                {
                    _lastSeller = ResolveLastKnownSeller();
                    if (!string.IsNullOrWhiteSpace(_lastSeller)) AttachShopScope(_lastSeller);
                }

                var summary = diag == null ? string.Empty : (diag.Summary ?? string.Empty).Trim();
                var connected = seller.Length > 0 && string.Equals(summary, "连接正常", StringComparison.Ordinal);
                if (connected)
                {
                    _connection.Text = "千牛：已连接";
                    _connection.Foreground = new SolidColorBrush(Color.FromRgb(39, 174, 96));
                }
                else
                {
                    _connection.Text = seller.Length > 0 && summary.Length > 0
                        ? "千牛：" + summary
                        : "千牛：等待连接";
                    _connection.Foreground = new SolidColorBrush(Color.FromRgb(242, 153, 74));
                }

                _shop.Text = string.IsNullOrWhiteSpace(_lastSeller)
                    ? "当前店铺：尚未识别；可保持工作台开启，启动千牛后会自动连接"
                    : "当前店铺：" + _lastSeller + (seller.Length == 0 ? "（最近使用）" : string.Empty);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("刷新独立Bot工作台状态失败：" + ex.Message, 10);
            }
        }

        private void OpenSettings()
        {
            var seller = ResolvePreferredSeller();
            if (string.IsNullOrWhiteSpace(seller))
            {
                MessageBox.Show(
                    this,
                    "当前尚未识别任何店铺。\n\n可以继续保持 Bot 工作台开启；启动一次千牛并识别客服账号后，设置入口会自动使用该店铺。",
                    "等待千牛连接",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            AttachShopScope(seller);
            WndOption.MyShow(seller, this);
        }

        private string ResolvePreferredSeller()
        {
            if (!string.IsNullOrWhiteSpace(_lastSeller)) return _lastSeller;
            _lastSeller = ResolveLastKnownSeller();
            return _lastSeller;
        }

        private static string ResolveLastKnownSeller()
        {
            try
            {
                var store = new ShopProfileStore(new ShopScopedPathProvider());
                var profile = store.GetAll()
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.DisplayName))
                    .OrderByDescending(x => x.LastSeenAtUtc)
                    .FirstOrDefault();
                return profile == null ? string.Empty : (profile.DisplayName ?? string.Empty).Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        private void AttachShopScope(string seller)
        {
            if (string.IsNullOrWhiteSpace(seller)) return;
            try
            {
                var shop = ShopContextLocator.ResolveBySellerNick(seller);
                ShopScopedUiBridge.Attach(this, shop);
            }
            catch
            {
                // A newly opened Bot can exist before Qianniu has provided a stable shop identity.
                // Existing global compatibility behavior remains authoritative until it does.
            }
        }
    }
}
