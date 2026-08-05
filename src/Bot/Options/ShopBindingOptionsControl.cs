using Bot.ShopScope;
using BotLib;
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bot.Options
{
    internal sealed class ShopBindingOptionsControl : UserControl, IOptions
    {
        private readonly ShopScopedPathProvider _paths = new ShopScopedPathProvider();
        private ShopContext _shop;
        private ShopControlPlaneConnectionStore _connection;
        private TextBox _serverUrl;
        private PasswordBox _token;
        private TextBlock _identity;
        private TextBlock _fingerprint;
        private TextBlock _status;
        private CheckBox _allowNicknameFallback;
        private Button _clearToken;
        private Button _importLegacy;
        private Button _openFolder;
        private string _seller;

        public ShopBindingOptionsControl(string seller)
        {
            BuildUi();
            InitUI(seller);
        }

        public OptionEnum OptionType
        {
            get { return OptionEnum.ShopBinding; }
        }

        public void InitUI(string seller)
        {
            _seller = (seller ?? string.Empty).Trim();
            try
            {
                _shop = ShopContextLocator.ResolveBySellerNick(_seller);
                _connection = new ShopControlPlaneConnectionStore(_shop, _paths);
                _serverUrl.Text = _connection.GetServerUrl();
                _token.Password = string.Empty;
                _identity.Text = BuildIdentityText(_shop);
                _allowNicknameFallback.Visibility = _shop.HasStableSellerId
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                _allowNicknameFallback.IsChecked = false;
                SetEnabled(true);
                RefreshTokenStatus();
            }
            catch (Exception ex)
            {
                _shop = null;
                _connection = null;
                _identity.Text = "店铺身份解析失败：" + ex.Message;
                _fingerprint.Text = "未绑定";
                _status.Text = "请保持目标千牛店铺在线后重新打开设置。";
                _status.Foreground = Brushes.IndianRed;
                SetEnabled(false);
            }
        }

        public void Save(string seller)
        {
            if (_shop == null || _connection == null) return;
            try
            {
                var candidate = (_token.Password ?? string.Empty).Trim();
                if (candidate.Length > 0
                    && !_shop.HasStableSellerId
                    && _allowNicknameFallback.IsChecked != true)
                {
                    throw new InvalidOperationException(
                        "当前千牛身份没有 TargetId。若必须临时使用，请勾选昵称回退确认后再保存。" );
                }

                _connection.SetServerUrl(_serverUrl.Text);
                if (candidate.Length > 0)
                {
                    _connection.SaveToken(candidate);
                    _token.Password = string.Empty;
                }
                RefreshTokenStatus();
                _status.Text = "本店绑定已保存。AI/功能设置将写入该 ShopKey 的独立配置目录。";
                _status.Foreground = Brushes.SeaGreen;
                Log.Info("店铺绑定已保存: shopKey=" + _shop.ShopKey
                    + ", stable=" + _shop.HasStableSellerId
                    + ", tokenFingerprint=" + _connection.TokenFingerprint);
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                throw;
            }
        }

        public void RestoreDefault()
        {
            if (_connection == null) return;
            _serverUrl.Text = _connection.GetServerUrl();
            _token.Password = string.Empty;
            _allowNicknameFallback.IsChecked = false;
            _status.Text = "已恢复界面内容，未删除本店已保存令牌。";
            _status.Foreground = Brushes.Gray;
        }

        public void NavHelp()
        {
            MessageBox.Show(
                "每个店铺使用独立 ShopKey 和独立 Bot 客户端令牌。令牌使用 Windows DPAPI CurrentUser 加密，只能由当前 Windows 用户在本机解密。统一 API 地址仍属于全局共享设置。旧全局令牌不会自动复制，必须点击“导入旧全局令牌”并保存。",
                "店铺绑定帮助",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void BuildUi()
        {
            var root = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            var panel = new StackPanel { Margin = new Thickness(18) };
            root.Content = panel;
            Content = root;

            panel.Children.Add(new TextBlock
            {
                Text = "店铺身份与 Bot 客户端令牌",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 12)
            });
            panel.Children.Add(new TextBlock
            {
                Text = "令牌按店铺独立加密保存。店铺显示名仅用于展示，不参与目录或授权。",
                Foreground = Brushes.DimGray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });

            _identity = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Background = new SolidColorBrush(Color.FromRgb(244, 247, 251)),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 12)
            };
            panel.Children.Add(_identity);

            panel.Children.Add(Label("统一 API 地址（全局共享）"));
            _serverUrl = new TextBox
            {
                MinWidth = 520,
                Margin = new Thickness(0, 4, 0, 12),
                ToolTip = "例如 https://api.example.com；末尾 /v1 会自动去除。"
            };
            panel.Children.Add(_serverUrl);

            panel.Children.Add(Label("本店 Bot 客户端令牌"));
            _token = new PasswordBox
            {
                MinWidth = 520,
                Margin = new Thickness(0, 4, 0, 6),
                ToolTip = "留空表示保留当前令牌；输入新令牌后保存会覆盖本店令牌。"
            };
            panel.Children.Add(_token);

            _fingerprint = new TextBlock
            {
                Foreground = Brushes.SteelBlue,
                Margin = new Thickness(0, 0, 0, 8)
            };
            panel.Children.Add(_fingerprint);

            _allowNicknameFallback = new CheckBox
            {
                Content = new TextBlock
                {
                    Text = "我确认当前版本未提供稳定 TargetId，临时按规范化昵称绑定（店铺改名后需要重新处理）",
                    TextWrapping = TextWrapping.Wrap
                },
                Foreground = Brushes.DarkOrange,
                Margin = new Thickness(0, 0, 0, 10)
            };
            panel.Children.Add(_allowNicknameFallback);

            var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            _importLegacy = MakeButton("导入旧全局令牌", 132);
            _importLegacy.Click += ImportLegacy_Click;
            buttons.Children.Add(_importLegacy);
            _clearToken = MakeButton("清除本店令牌", 118);
            _clearToken.Click += ClearToken_Click;
            buttons.Children.Add(_clearToken);
            _openFolder = MakeButton("打开本店配置目录", 142);
            _openFolder.Click += OpenFolder_Click;
            buttons.Children.Add(_openFolder);
            panel.Children.Add(buttons);

            _status = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray
            };
            panel.Children.Add(_status);
        }

        private void ImportLegacy_Click(object sender, RoutedEventArgs e)
        {
            var legacy = ShopControlPlaneConnectionStore.GetLegacyGlobalToken();
            if (string.IsNullOrWhiteSpace(legacy))
            {
                _status.Text = "旧全局配置中没有可导入的 Bot 客户端令牌。";
                _status.Foreground = Brushes.DarkOrange;
                return;
            }
            _token.Password = legacy;
            _status.Text = "旧全局令牌已放入输入框，点击窗口底部“保存”后才会写入本店 DPAPI 令牌文件。";
            _status.Foreground = Brushes.SteelBlue;
        }

        private void ClearToken_Click(object sender, RoutedEventArgs e)
        {
            if (_connection == null || !_connection.HasToken) return;
            if (MessageBox.Show(
                "确定清除店铺“" + (_shop.DisplayName ?? _shop.ShopKey) + "”的独立 Bot 客户端令牌吗？不会删除其他店铺令牌。",
                "清除本店令牌",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try
            {
                _connection.ClearToken();
                _token.Password = string.Empty;
                RefreshTokenStatus();
                _status.Text = "本店令牌已清除。";
                _status.Foreground = Brushes.DarkOrange;
            }
            catch (Exception ex)
            {
                MessageBox.Show("清除失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_shop == null) return;
            try
            {
                Process.Start("explorer.exe", _paths.GetConfigRoot(_shop));
            }
            catch (Exception ex)
            {
                _status.Text = "打开目录失败：" + ex.Message;
                _status.Foreground = Brushes.IndianRed;
            }
        }

        private void RefreshTokenStatus()
        {
            if (_connection == null)
            {
                _fingerprint.Text = "未绑定";
                return;
            }
            var fingerprint = _connection.TokenFingerprint;
            _fingerprint.Text = string.IsNullOrWhiteSpace(fingerprint)
                ? "状态：本店尚未保存独立令牌"
                : "状态：已保存本店令牌｜指纹 " + fingerprint + "（不显示令牌原文）";
            _clearToken.IsEnabled = _connection.HasToken;
            _status.Text = string.IsNullOrWhiteSpace(fingerprint)
                ? "可输入本店令牌，或显式导入旧全局令牌。"
                : "令牌文件：" + _connection.TokenPath;
            _status.Foreground = string.IsNullOrWhiteSpace(fingerprint) ? Brushes.Gray : Brushes.SeaGreen;
        }

        private void SetEnabled(bool enabled)
        {
            _serverUrl.IsEnabled = enabled;
            _token.IsEnabled = enabled;
            _clearToken.IsEnabled = enabled;
            _importLegacy.IsEnabled = enabled;
            _openFolder.IsEnabled = enabled;
            _allowNicknameFallback.IsEnabled = enabled;
        }

        private static string BuildIdentityText(ShopContext shop)
        {
            return "店铺：" + (string.IsNullOrWhiteSpace(shop.DisplayName) ? "（未提供显示名）" : shop.DisplayName)
                + "\nShopKey：" + shop.ShopKey
                + "\n平台：" + shop.Platform
                + "\n卖家身份：" + (shop.HasStableSellerId ? "TargetId（稳定候选）" : "昵称回退（不稳定）")
                + "\n店铺目录：%LocalAppData%\\QianniuAiBot\\shops\\" + shop.ShopKey;
        }

        private static TextBlock Label(string text)
        {
            return new TextBlock { Text = text, FontWeight = FontWeights.SemiBold };
        }

        private static Button MakeButton(string text, double width)
        {
            return new Button
            {
                Content = text,
                Width = width,
                Height = 30,
                Margin = new Thickness(0, 0, 8, 0)
            };
        }
    }
}
