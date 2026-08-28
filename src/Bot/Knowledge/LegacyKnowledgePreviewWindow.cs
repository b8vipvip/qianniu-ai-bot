using Bot.ChromeNs;
using Bot.ShopScope;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bot.Knowledge
{
    /// <summary>
    /// Read-only preview of the real Knowledge Center V1 UI immediately before the V2 shell was
    /// introduced (pre-V2 anchor: commit 86e0138b2f2e4583530aaf0264b6215a8443f35e).
    /// It intentionally reuses the V1 controls so every historical tab/button/config field remains
    /// visible, while mutation controls are made read-only/disabled and the V1 reply runtime is never enabled.
    /// </summary>
    internal sealed class LegacyKnowledgePreviewWindow : Window
    {
        private readonly TabControl _tabs;
        private readonly KnowledgeImportControl _import;
        private readonly KnowledgeManagerControl _manager;
        private readonly AiOptimizationHistoryControl _optimizationHistory;

        private LegacyKnowledgePreviewWindow()
        {
            Title = "AI客服 - 知识库（V1改版前旧版预览，只读）";
            Width = 1100;
            Height = 720;
            MinWidth = 900;
            MinHeight = 620;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new DockPanel();
            Content = root;
            var toolbar = new WrapPanel { Margin = new Thickness(10, 10, 10, 4) };
            DockPanel.SetDock(toolbar, Dock.Top);
            var importPackage = new Button { Content = "导入知识库完整包", Width = 140, Height = 30, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false, ToolTip = "旧版预览模式：按钮保留展示，但不会执行导入" };
            var exportPackage = new Button { Content = "导出知识库完整包", Width = 140, Height = 30, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false, ToolTip = "旧版预览模式：按钮保留展示，但不会执行导出" };
            toolbar.Children.Add(importPackage);
            toolbar.Children.Add(exportPackage);
            root.Children.Add(toolbar);

            _tabs = new TabControl();
            root.Children.Add(_tabs);
            _manager = new KnowledgeManagerControl();
            _import = new KnowledgeImportControl(delegate { });
            _optimizationHistory = new AiOptimizationHistoryControl();
            _tabs.Items.Add(new TabItem { Header = "智能导入", Content = _import });
            _tabs.Items.Add(new TabItem { Header = "问答管理", Content = _manager });
            _tabs.Items.Add(new TabItem { Header = "AI优化记录", Content = _optimizationHistory });

            Loaded += delegate
            {
                MakePreviewOnly(_import);
                MakePreviewOnly(_manager);
                MakePreviewOnly(_optimizationHistory);
                try { _manager.RefreshData(); } catch { }
            };
        }

        internal static void MyShow(Window owner, string seller)
        {
            try
            {
                var shop = ResolveShop(owner, seller);
                LegacyKnowledgePreviewWindow window;
                using (ShopSettingsScope.Enter(shop)) window = new LegacyKnowledgePreviewWindow();
                ShopScopedUiBridge.Attach(window, shop);
                if (owner != null) window.Owner = owner;
                window.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, "无法打开知识中心V1改版前旧版预览：" + ex.Message, "旧版知识库预览", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            try { if (_import != null) _import.CancelForWindowClose(); } catch { }
            base.OnClosed(e);
        }

        private static ShopContext ResolveShop(Window owner, string seller)
        {
            var attached = ShopScopedUiBridge.Get(owner);
            if (attached != null) return attached;
            var effectiveSeller = (seller ?? string.Empty).Trim();
            if (effectiveSeller.Length == 0 && QN.CurQN != null && QN.CurQN.Seller != null) effectiveSeller = (QN.CurQN.Seller.Nick ?? string.Empty).Trim();
            if (effectiveSeller.Length == 0) throw new InvalidOperationException("未识别当前店铺。请从对应店铺的设置页面重新打开。");
            var shop = ShopContextLocator.ResolveRuntimeBySellerNick(effectiveSeller);
            if (shop == null) throw new InvalidOperationException("未找到当前店铺的 ShopKey。");
            return shop;
        }

        private static void MakePreviewOnly(DependencyObject root)
        {
            if (root == null) return;
            var button = root as Button;
            if (button != null)
            {
                button.IsEnabled = false;
                if (button.ToolTip == null) button.ToolTip = "旧版预览模式：保留历史按钮外观，不执行操作";
            }
            var text = root as TextBox;
            if (text != null) text.IsReadOnly = true;
            var password = root as PasswordBox;
            if (password != null) password.IsEnabled = false;
            var combo = root as ComboBox;
            if (combo != null) { combo.IsHitTestVisible = false; combo.IsTabStop = false; }
            var check = root as CheckBox;
            if (check != null) check.IsEnabled = false;
            var radio = root as RadioButton;
            if (radio != null) radio.IsEnabled = false;
            var date = root as DatePicker;
            if (date != null) date.IsEnabled = false;
            var grid = root as DataGrid;
            if (grid != null) { grid.IsReadOnly = true; grid.CanUserAddRows = false; grid.CanUserDeleteRows = false; }

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++) MakePreviewOnly(VisualTreeHelper.GetChild(root, i));
        }
    }
}
