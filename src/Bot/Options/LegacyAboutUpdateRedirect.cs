using BotLib;
using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Bot.Options
{
    /// <summary>
    /// 右侧面板旧“关于”菜单仍弹出静态 AI客服 v2 文本。
    /// 在不改动旧 XAML 事件绑定的前提下，用 WPF 类处理器优先拦截该菜单，统一打开“关于与版本更新”。
    /// </summary>
    internal static class LegacyAboutUpdateRedirect
    {
        private static int _initialized;

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            EventManager.RegisterClassHandler(
                typeof(MenuItem),
                MenuItem.ClickEvent,
                new RoutedEventHandler(OnMenuItemClick),
                true);
            Log.Info("旧关于菜单已重定向到关于与版本更新中心。");
        }

        private static void OnMenuItemClick(object sender, RoutedEventArgs e)
        {
            var item = sender as MenuItem;
            if (item == null || !string.Equals(HeaderText(item), "关于", StringComparison.Ordinal)) return;

            e.Handled = true;
            var dispatcher = item.Dispatcher ?? Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(BotAboutUpdateLauncher.Show), DispatcherPriority.Background);
        }

        private static string HeaderText(MenuItem item)
        {
            var textBlock = item.Header as TextBlock;
            if (textBlock != null) return (textBlock.Text ?? string.Empty).Trim();
            return Convert.ToString(item.Header ?? string.Empty).Trim();
        }
    }
}
