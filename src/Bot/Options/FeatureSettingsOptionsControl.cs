using Bot.ChromeNs;
using Bot.Knowledge;
using BotLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Bot.Options
{
    /// <summary>
    /// Reuses the existing feature-settings implementation inside the unified settings window.
    /// The legacy Window remains available for compatibility, while its content is detached and
    /// hosted here so users no longer have to navigate through separate popup windows.
    /// </summary>
    internal sealed class FeatureSettingsOptionsControl : UserControl, IOptions
    {
        private readonly FeatureSettingsWindow _legacyWindow;
        private readonly TabControl _tabs;
        private readonly MethodInfo _saveAllMethod;
        private CheckBox _firstInquiryFixedReplyEnabled;
        private TextBox _firstInquiryFixedReplyAnswer;
        private string _currentPage;

        public FeatureSettingsOptionsControl(string seller)
        {
            Seller = seller ?? string.Empty;
            _legacyWindow = new FeatureSettingsWindow("知识库");
            _tabs = GetPrivateField<TabControl>(_legacyWindow, "_tabs");
            _saveAllMethod = typeof(FeatureSettingsWindow).GetMethod(
                "SaveAll",
                BindingFlags.Instance | BindingFlags.NonPublic);

            if (_tabs == null || _saveAllMethod == null)
            {
                throw new InvalidOperationException("无法初始化功能设置页面。请重新安装完整版本。 ");
            }

            RemoveMeaninglessLicensePage();
            ReplaceKnowledgePageWithEmbeddedLauncher();
            OrganizeAutoReplyRulesPage();
            HideLegacyTabHeaders();

            var hosted = _legacyWindow.Content as UIElement;
            if (hosted == null)
            {
                throw new InvalidOperationException("功能设置内容为空。 ");
            }
            _legacyWindow.Content = null;
            PrepareHostedLayout(hosted);
            Content = hosted;
            NavigateTo("知识库");
        }

        public string Seller { get; private set; }

        public OptionEnum OptionType
        {
            get { return OptionEnum.FeatureSettings; }
        }

        public void InitUI(string seller)
        {
            Seller = seller ?? Seller;
        }

        public void NavigateTo(string pageTitle)
        {
            pageTitle = (pageTitle ?? string.Empty).Trim();
            if (pageTitle.Length == 0) return;

            foreach (TabItem tab in _tabs.Items)
            {
                var header = Convert.ToString(tab.Header) ?? string.Empty;
                if (string.Equals(header, pageTitle, StringComparison.Ordinal)
                    || header.IndexOf(pageTitle, StringComparison.Ordinal) >= 0
                    || pageTitle.IndexOf(header, StringComparison.Ordinal) >= 0)
                {
                    _tabs.SelectedItem = tab;
                    _currentPage = header;
                    return;
                }
            }

            throw new InvalidOperationException("找不到设置页面：" + pageTitle);
        }

        public void Save(string seller)
        {
            try
            {
                _saveAllMethod.Invoke(_legacyWindow, null);
                if (_firstInquiryFixedReplyEnabled != null && _firstInquiryFixedReplyAnswer != null)
                {
                    FirstInquiryFixedReplyService.Save(
                        seller ?? Seller,
                        _firstInquiryFixedReplyEnabled.IsChecked == true,
                        _firstInquiryFixedReplyAnswer.Text ?? string.Empty);
                }
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException ?? ex;
            }
        }

        public void RestoreDefault()
        {
            MessageBox.Show(
                Window.GetWindow(this),
                "该页面包含知识、规则、通知和策略等业务数据，为避免误清空，暂不提供一键恢复默认。请在当前页面逐项修改后保存。",
                "保护业务数据",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        public void NavHelp()
        {
            var page = string.IsNullOrWhiteSpace(_currentPage) ? "当前页面" : _currentPage;
            MessageBox.Show(
                Window.GetWindow(this),
                page + "的修改会按当前 ShopKey 独立保存。完成修改后点击设置窗口右下角的“保存设置”。",
                "设置帮助",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void RemoveMeaninglessLicensePage()
        {
            var licenseTab = _tabs.Items
                .OfType<TabItem>()
                .FirstOrDefault(x => string.Equals(Convert.ToString(x.Header), "账号与授权", StringComparison.Ordinal));
            if (licenseTab != null)
            {
                _tabs.Items.Remove(licenseTab);
            }

            SetPrivateField(_legacyWindow, "_licensee", null);
            SetPrivateField(_legacyWindow, "_licenseKey", null);
            SetPrivateField(_legacyWindow, "_expireDate", null);
            SetPrivateField(_legacyWindow, "_offlineAuth", null);
        }

        private void ReplaceKnowledgePageWithEmbeddedLauncher()
        {
            var knowledgeTab = _tabs.Items
                .OfType<TabItem>()
                .FirstOrDefault(x => string.Equals(Convert.ToString(x.Header), "知识库", StringComparison.Ordinal));
            if (knowledgeTab == null) return;

            var panel = new StackPanel { Margin = new Thickness(26) };
            panel.Children.Add(new TextBlock
            {
                Text = "知识库中心",
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55))
            });
            panel.Children.Add(new TextBlock
            {
                Text = "知识库使用独立管理界面，支持智能导入、搜索、编辑、分类整理和 JSON 导入导出。数据仍按当前店铺独立保存。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(102, 112, 133)),
                Margin = new Thickness(0, 10, 0, 18)
            });
            var button = new Button
            {
                Content = "打开知识库中心",
                Width = 160,
                Height = 36,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Color.FromRgb(37, 99, 235)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(37, 99, 235)),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            button.Click += delegate
            {
                KnowledgeCenterWindow.MyShow(Window.GetWindow(this));
            };
            panel.Children.Add(button);
            knowledgeTab.Content = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Margin = new Thickness(8),
                Child = panel
            };
        }

        private void OrganizeAutoReplyRulesPage()
        {
            var autoReplyTab = _tabs.Items
                .OfType<TabItem>()
                .FirstOrDefault(x => string.Equals(Convert.ToString(x.Header), "自动回复规则", StringComparison.Ordinal));
            if (autoReplyTab == null) return;

            var legacyContent = DetachLegacyScrollHost(autoReplyTab.Content as UIElement);
            autoReplyTab.Content = null;

            var settings = FirstInquiryFixedReplyService.Load(Seller)
                ?? new FirstInquiryFixedReplySettings();
            _firstInquiryFixedReplyEnabled = new CheckBox
            {
                Content = "启用首条咨询固定回复",
                IsChecked = settings.Enabled,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55)),
                VerticalAlignment = VerticalAlignment.Center
            };
            _firstInquiryFixedReplyAnswer = new TextBox
            {
                Text = settings.Answer ?? string.Empty,
                MinHeight = 64,
                MaxHeight = 96,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 8, 0, 0),
                ToolTip = "新一轮咨询第一个事件触发时直接发送。默认：在的，亲！"
            };

            // The unified settings shell already renders the page title. Keep the auto-reply
            // controls in one continuous scroll surface instead of splitting the page into two
            // numbered cards with an inner legacy scrollbar.
            var body = new StackPanel { Margin = new Thickness(12, 8, 12, 14) };
            body.Children.Add(new TextBlock
            {
                Text = "首条咨询固定回复",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55))
            });

            var enabledRow = new DockPanel { Margin = new Thickness(0, 9, 0, 0) };
            DockPanel.SetDock(_firstInquiryFixedReplyEnabled, Dock.Right);
            enabledRow.Children.Add(_firstInquiryFixedReplyEnabled);
            enabledRow.Children.Add(new TextBlock
            {
                Text = "新买家或超过 10 分钟未互动后再次来询，都可重新触发。固定回复真实发送成功后才记录本轮已回复。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                Margin = new Thickness(0, 2, 18, 0)
            });
            body.Children.Add(enabledRow);
            body.Children.Add(new TextBlock
            {
                Text = "固定答案",
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(55, 65, 81)),
                Margin = new Thickness(0, 12, 0, 0)
            });
            body.Children.Add(_firstInquiryFixedReplyAnswer);
            body.Children.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                Margin = new Thickness(0, 18, 0, 14)
            });
            if (legacyContent != null) body.Children.Add(legacyContent);

            autoReplyTab.Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                CanContentScroll = false,
                Content = body
            };
        }

        private static UIElement DetachLegacyScrollHost(UIElement content)
        {
            if (content == null) return null;
            var scroll = content as ScrollViewer;
            if (scroll != null)
            {
                var child = scroll.Content as UIElement;
                scroll.Content = null;
                return child ?? content;
            }

            var border = content as Border;
            if (border != null)
            {
                var nested = border.Child as ScrollViewer;
                if (nested != null)
                {
                    var child = nested.Content as UIElement;
                    nested.Content = null;
                    border.Child = child;
                }
            }
            return content;
        }

        private void HideLegacyTabHeaders()
        {
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetBinding(
                ContentPresenter.ContentProperty,
                new Binding("SelectedContent")
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
                });
            var template = new ControlTemplate(typeof(TabControl));
            template.VisualTree = presenter;
            _tabs.Template = template;
            _tabs.BorderThickness = new Thickness(0);
            _tabs.Background = Brushes.Transparent;
        }

        private static void PrepareHostedLayout(UIElement hosted)
        {
            var root = hosted as DockPanel;
            if (root == null) return;
            root.Margin = new Thickness(0);

            foreach (var footer in root.Children.OfType<DockPanel>().ToList())
            {
                if (DockPanel.GetDock(footer) != Dock.Bottom) continue;
                foreach (var buttonPanel in footer.Children.OfType<StackPanel>().ToList())
                {
                    if (buttonPanel.Orientation == Orientation.Horizontal)
                    {
                        buttonPanel.Visibility = Visibility.Collapsed;
                    }
                }
            }
        }

        private static T GetPrivateField<T>(object target, string name) where T : class
        {
            var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            return field == null ? null : field.GetValue(target) as T;
        }

        private static void SetPrivateField(object target, string name, object value)
        {
            var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field != null) field.SetValue(target, value);
        }
    }
}
