using Bot.AssistWindow;
using Bot.Common;
using Bot.Common.Windows;
using Bot.ShopScope;
using BotLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bot.Options
{
    public partial class WndOption : EtWindow
    {
        private sealed class SettingsPage
        {
            public string Group { get; set; }
            public string Title { get; set; }
            public string Description { get; set; }
            public OptionEnum PageType { get; set; }
            public IOptions Control { get; set; }
            public string FeaturePage { get; set; }
            public RadioButton NavigationButton { get; set; }
        }

        private readonly ShopContext _shop;
        private readonly List<SettingsPage> _pages = new List<SettingsPage>();
        private readonly List<IOptions> _visitedOptions = new List<IOptions>();
        private FeatureSettingsOptionsControl _featureSettings;
        private SettingsPage _currentPage;
        private OptionEnum _pendingPage = OptionEnum.Unknown;
        private bool _initialized;

        private WndOption(string seller)
        {
            Seller = seller;
            try
            {
                _shop = ShopContextLocator.ResolveBySellerNick(seller);
            }
            catch (Exception ex)
            {
                Log.Info("设置窗口暂未取得稳定店铺作用域，将保留旧全局配置兼容模式: seller="
                    + seller + ", error=" + ex.Message);
            }
            InitializeComponent();
            Loaded += WndOption_Loaded;
        }

        private void WndOption_Loaded(object sender, RoutedEventArgs e)
        {
            if (_initialized) return;
            _initialized = true;

            BuildSettingsPages();
            BuildNavigation();
            UpdateShopScopeHeader();

            var firstPage = _pendingPage == OptionEnum.Unknown
                ? _pages.FirstOrDefault(x => x.PageType == OptionEnum.ShopBinding)
                : _pages.FirstOrDefault(x => x.PageType == _pendingPage);
            NavigateTo(firstPage ?? _pages.FirstOrDefault());
        }

        private void BuildSettingsPages()
        {
            ShopBindingOptionsControl shopBinding = null;
            CtlRobotOptions aiSettings = null;
            CtlDataManagement dataManagement = null;
            BotUpdateOptionsControl aboutUpdate = null;

            RunInShopScope(delegate
            {
                shopBinding = new ShopBindingOptionsControl(Seller);
                aiSettings = new CtlRobotOptions(Seller);
                _featureSettings = new FeatureSettingsOptionsControl(Seller);
                dataManagement = new CtlDataManagement();
                aboutUpdate = new BotUpdateOptionsControl();
            });

            AddPage("店铺与连接", "店铺绑定", "查看 ShopKey、迁移旧数据并管理当前店铺身份。",
                OptionEnum.ShopBinding, shopBinding);
            AddPage("店铺与连接", "AI 服务", "配置统一 API 服务地址、店铺令牌和文本/视觉路由。",
                OptionEnum.Robot, aiSettings);

            AddFeaturePage("回复与通知", "知识库", "管理店铺问答、智能导入、搜索和分类。",
                OptionEnum.GoodsKnowledge, "知识库");
            AddFeaturePage("回复与通知", "自动回复规则", "设置转人工规则、下单回复和关键词边界。",
                OptionEnum.AutoReplyRules, "自动回复规则");
            AddFeaturePage("回复与通知", "消息通知", "配置工作时间、转人工通知和通知渠道。",
                OptionEnum.Notifications, "消息通知");
            AddFeaturePage("回复与通知", "消息策略", "控制语气、长度、禁用词和知识使用方式。",
                OptionEnum.MessagePolicy, "消息策略");

            AddPage("数据与安全", "数据管理", "备份、恢复和迁移当前店铺的业务数据。",
                OptionEnum.DataManagement, dataManagement);
            AddFeaturePage("数据与安全", "日志与调试", "查看运行日志和诊断信息。",
                OptionEnum.Diagnostics, "日志与调试");
            AddFeaturePage("数据与安全", "商业化合规", "维护上线前的隐私、告知和人工接管清单。",
                OptionEnum.Compliance, "商业化合规清单");

            AddPage("系统", "关于与更新", "检查正式版本、查看更新说明和自动更新状态。",
                OptionEnum.AboutUpdate, aboutUpdate);
        }

        private void AddPage(
            string group,
            string title,
            string description,
            OptionEnum pageType,
            IOptions control)
        {
            if (control == null) throw new InvalidOperationException("设置页面初始化失败：" + title);
            _pages.Add(new SettingsPage
            {
                Group = group,
                Title = title,
                Description = description,
                PageType = pageType,
                Control = control
            });
        }

        private void AddFeaturePage(
            string group,
            string title,
            string description,
            OptionEnum pageType,
            string featurePage)
        {
            AddPage(group, title, description, pageType, _featureSettings);
            _pages[_pages.Count - 1].FeaturePage = featurePage;
        }

        private void BuildNavigation()
        {
            navPanel.Children.Clear();
            string currentGroup = null;
            var navStyle = FindResource("SettingsNavItem") as Style;
            var groupStyle = FindResource("SettingsGroupTitle") as Style;

            foreach (var page in _pages)
            {
                if (!string.Equals(currentGroup, page.Group, StringComparison.Ordinal))
                {
                    currentGroup = page.Group;
                    navPanel.Children.Add(new TextBlock
                    {
                        Text = currentGroup,
                        Style = groupStyle
                    });
                }

                var title = new TextBlock
                {
                    Text = page.Title,
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                var description = new TextBlock
                {
                    Text = page.Description,
                    FontSize = 10.5,
                    Foreground = new SolidColorBrush(Color.FromRgb(102, 112, 133)),
                    Margin = new Thickness(0, 3, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                var content = new StackPanel();
                content.Children.Add(title);
                content.Children.Add(description);

                var button = new RadioButton
                {
                    GroupName = "SettingsNavigation",
                    Style = navStyle,
                    Content = content,
                    Tag = page
                };
                button.Checked += NavigationButton_Checked;
                page.NavigationButton = button;
                navPanel.Children.Add(button);
            }
        }

        private void NavigationButton_Checked(object sender, RoutedEventArgs e)
        {
            var button = sender as RadioButton;
            var page = button == null ? null : button.Tag as SettingsPage;
            NavigateTo(page);
        }

        private void NavigateTo(SettingsPage page)
        {
            if (page == null) return;

            if (!string.IsNullOrWhiteSpace(page.FeaturePage))
            {
                _featureSettings.NavigateTo(page.FeaturePage);
            }

            _currentPage = page;
            contentHost.Content = page.Control;
            txtPageTitle.Text = page.Title;
            txtPageDescription.Text = page.Description;
            if (page.NavigationButton != null && page.NavigationButton.IsChecked != true)
            {
                page.NavigationButton.IsChecked = true;
            }
            if (!_visitedOptions.Contains(page.Control))
            {
                _visitedOptions.Add(page.Control);
            }

            btnRestoreCurrent.IsEnabled = !(page.Control is FeatureSettingsOptionsControl);
            btnRestoreCurrent.Opacity = btnRestoreCurrent.IsEnabled ? 1.0 : 0.55;
        }

        private void UpdateShopScopeHeader()
        {
            if (_shop == null)
            {
                txtShopScope.Text = Seller + " · 旧全局兼容模式（尚未取得稳定 ShopKey）";
                btnSave.ToolTip = "保存为当前客服的旧全局兼容设置";
                return;
            }

            txtShopScope.Text = (_shop.DisplayName ?? Seller) + " · ShopKey：" + _shop.ShopKey;
            btnSave.ToolTip = "保存到当前店铺的独立配置：" + _shop.ShopKey;
        }

        private void btnHelp_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage != null && _currentPage.Control != null)
            {
                _currentPage.Control.NavHelp();
            }
        }

        public static void MyShow(
            string seller,
            WndAssist owner = null,
            OptionEnum showPage = OptionEnum.Unknown,
            Action uiCallback = null)
        {
            Util.Assert(!string.IsNullOrEmpty(seller));
            var wndOp = ShowSameNickOneInstance<WndOption>(seller, delegate
            {
                return new WndOption(seller);
            }, owner, true);

            if (uiCallback != null)
            {
                wndOp.Closed += delegate { uiCallback(); };
            }
            if (showPage > OptionEnum.Unknown)
            {
                wndOp.ShowPage(showPage);
            }
        }

        private void ShowPage(OptionEnum showPage)
        {
            _pendingPage = showPage;
            if (!_initialized) return;
            NavigateTo(_pages.FirstOrDefault(x => x.PageType == showPage));
        }

        private void sbSave_Click(object sender, RoutedEventArgs e)
        {
            Save(Seller);
        }

        private void Save(string seller)
        {
            Util.Assert(!string.IsNullOrEmpty(seller));
            try
            {
                Hide();
                RunInShopScope(delegate
                {
                    foreach (var options in _visitedOptions.ToList())
                    {
                        options.Save(seller);
                    }
                });
                Close();
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                Show();
                Activate();
                MessageBox.Show(
                    this,
                    "保存设置失败：" + ex.Message + "\n\n窗口已保留，请修正后重试。",
                    "保存失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void btnRestoreCurrentPageToDef_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage == null || _currentPage.Control == null) return;
            RunInShopScope(_currentPage.Control.RestoreDefault);
        }

        private void EtWindow_Closed(object sender, EventArgs e)
        {
        }

        private void RunInShopScope(Action action)
        {
            if (action == null) return;
            if (_shop == null)
            {
                action();
                return;
            }
            using (ShopSettingsScope.Enter(_shop))
            {
                action();
            }
        }
    }
}
