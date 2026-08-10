using Bot.Knowledge;
using Bot.ShopScope;
using BotLib;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
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
        private CheckBox _knowledgeCloudSync;
        private Button _clearToken;
        private Button _importLegacy;
        private Button _syncKnowledge;
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
                _knowledgeCloudSync.IsChecked = KnowledgeCloudSyncService.IsEnabledForShop(_shop);
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
            SaveConnection(true);
        }

        private void SaveConnection(bool queueCloudSync)
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

                KnowledgeCloudSyncService.SetEnabledForShop(
                    _shop,
                    _knowledgeCloudSync.IsChecked == true,
                    queueCloudSync);

                RefreshTokenStatus();
                _status.Text = "本店连接与云同步设置已保存。服务端地址、Bot Token、知识库、规则、消息状态和本店云数据均按 ShopKey 隔离。";
                _status.Foreground = Brushes.SeaGreen;
                Log.Info("店铺绑定已保存: shopKey=" + _shop.ShopKey
                    + ", stable=" + _shop.HasStableSellerId
                    + ", serverUrlScoped=" + _connection.HasShopServerUrl
                    + ", tokenFingerprint=" + _connection.TokenFingerprint
                    + ", knowledgeCloud=" + (_knowledgeCloudSync.IsChecked == true));
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
            _knowledgeCloudSync.IsChecked = _shop != null && KnowledgeCloudSyncService.IsEnabledForShop(_shop);
            _status.Text = "已恢复界面内容，未删除本店已保存令牌、服务端地址或云端知识。";
            _status.Foreground = Brushes.Gray;
        }

        public void NavHelp()
        {
            MessageBox.Show(
                "每个店铺使用独立 ShopKey、独立 Bot 服务端地址和独立客户端令牌。令牌与店铺设置使用 Windows DPAPI CurrentUser 加密，只能由当前 Windows 用户在本机解密。升级后旧全局服务端地址只用于首次预填，保存后即写入本店独立配置。旧全局令牌不会自动复制，必须点击“导入旧全局令牌”并保存。启用云同步后，Windows 与 Web 端只同步当前 ShopKey 对应的知识库，并在应用云端版本前自动备份本店本机知识。",
                "店铺绑定帮助",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void BuildUi()
        {
            var root = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            var panel = new StackPanel { Margin = new Thickness(18) };
            root.Content = panel;
            Content = root;

            panel.Children.Add(new TextBlock
            {
                Text = "店铺身份、Bot 连接与云端知识库",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 12)
            });
            panel.Children.Add(new TextBlock
            {
                Text = "当前页面只配置本店。服务端地址、Bot Token、知识库、规则和运行状态均与其他 ShopKey 隔离；切换到另一家店铺打开设置时会看到另一套配置。",
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

            panel.Children.Add(Label("本店 Bot 服务端地址"));
            _serverUrl = new TextBox
            {
                MinWidth = 420,
                Margin = new Thickness(0, 4, 0, 4),
                ToolTip = "例如 https://api.example.com；末尾 /v1 会自动去除。保存后只写入当前 ShopKey。"
            };
            panel.Children.Add(_serverUrl);
            panel.Children.Add(new TextBlock
            {
                Text = "升级旧版本时可能先显示历史全局地址；点击保存后会固定为本店独立地址。",
                FontSize = 11,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 0, 0, 12)
            });

            panel.Children.Add(Label("本店 Bot 客户端令牌"));
            _token = new PasswordBox
            {
                MinWidth = 420,
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
                Margin = new Thickness(0, 0, 0, 12)
            };
            panel.Children.Add(_allowNicknameFallback);

            var cloudCard = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(239, 246, 255)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(191, 219, 254)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 12)
            };
            var cloudPanel = new StackPanel();
            cloudCard.Child = cloudPanel;
            cloudPanel.Children.Add(new TextBlock
            {
                Text = "云端知识库",
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(30, 64, 175))
            });
            _knowledgeCloudSync = new CheckBox
            {
                Content = "启用本店知识库云同步",
                Margin = new Thickness(0, 8, 0, 5),
                ToolTip = "仅同步当前 ShopKey；云端版本应用前会自动备份本店本机知识。"
            };
            cloudPanel.Children.Add(_knowledgeCloudSync);
            cloudPanel.Children.Add(new TextBlock
            {
                Text = "新服务器首次绑定本店 Token 后，可直接点击“保存连接并立即同步知识库”把该店云端知识拉到本机；后续本机与 Web 端修改会继续按 revision 自动同步。",
                Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(cloudCard);

            var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            _syncKnowledge = MakeButton("保存连接并立即同步知识库", 210);
            _syncKnowledge.Click += async (s, e) => await SyncKnowledge_Click();
            buttons.Children.Add(_syncKnowledge);
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
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 0, 0, 6)
            };
            panel.Children.Add(_status);
        }

        private async Task SyncKnowledge_Click()
        {
            if (_shop == null || _connection == null) return;
            _syncKnowledge.IsEnabled = false;
            try
            {
                _knowledgeCloudSync.IsChecked = true;
                SaveConnection(false);
                if (string.IsNullOrWhiteSpace(_connection.GetServerUrl()))
                    throw new InvalidOperationException("请先填写本店 Bot 服务端地址。" );
                if (!_connection.HasToken)
                    throw new InvalidOperationException("请先输入或导入本店 Bot 客户端令牌。" );

                _status.Text = "正在连接服务端并同步本店云端知识库...";
                _status.Foreground = Brushes.SteelBlue;
                await KnowledgeCloudSyncService.SyncNowAsync(_shop);
                _status.Text = "本店云端知识库同步完成。可以进入“知识库”页面核对条目；本次写入前的本机版本会在本店 backup 目录保留。";
                _status.Foreground = Brushes.SeaGreen;
            }
            catch (Exception ex)
            {
                _status.Text = "云端知识库同步失败：" + ex.Message;
                _status.Foreground = Brushes.IndianRed;
                Log.Exception(ex);
            }
            finally
            {
                _syncKnowledge.IsEnabled = true;
                RefreshTokenStatus(false);
            }
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
            _status.Text = "旧全局令牌已放入输入框，点击窗口底部“保存”或“保存连接并立即同步知识库”后才会写入本店 DPAPI 令牌文件。";
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
                _status.Text = "本店令牌及其加密备份副本已清除。";
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

        private void RefreshTokenStatus(bool updateMessage = true)
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
            if (!updateMessage) return;
            _status.Text = string.IsNullOrWhiteSpace(fingerprint)
                ? "可输入本店令牌，或显式导入旧全局令牌。"
                : "连接配置已按 ShopKey 保存；令牌文件：" + _connection.TokenPath;
            _status.Foreground = string.IsNullOrWhiteSpace(fingerprint) ? Brushes.Gray : Brushes.SeaGreen;
        }

        private void SetEnabled(bool enabled)
        {
            _serverUrl.IsEnabled = enabled;
            _token.IsEnabled = enabled;
            _clearToken.IsEnabled = enabled;
            _importLegacy.IsEnabled = enabled;
            _syncKnowledge.IsEnabled = enabled;
            _openFolder.IsEnabled = enabled;
            _allowNicknameFallback.IsEnabled = enabled;
            _knowledgeCloudSync.IsEnabled = enabled;
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
                Height = 32,
                Margin = new Thickness(0, 0, 8, 6)
            };
        }
    }
}
