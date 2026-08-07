using Bot.AssistWindow.NotifyIcon;
using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Bot
{
    public partial class App
    {
        private readonly object _botDesktopStartupBootstrap =
            AssistWindow.BotDesktopStartup.InitializeForApp();
    }
}

namespace Bot.AssistWindow
{
    /// <summary>
    /// Hooks the application's always-present tray window instead of any Qianniu Desk.
    /// This guarantees the desktop workbench can be shown before Qianniu exists.
    /// </summary>
    internal static class BotDesktopStartup
    {
        private static int _initialized;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                EventManager.RegisterClassHandler(
                    typeof(WndNotifyIcon),
                    FrameworkElement.LoadedEvent,
                    new RoutedEventHandler(OnTrayWindowLoaded),
                    true);
            }
            return new object();
        }

        private static void OnTrayWindowLoaded(object sender, RoutedEventArgs e)
        {
            var window = sender as WndNotifyIcon;
            var dispatcher = window == null ? Dispatcher.CurrentDispatcher : window.Dispatcher;
            dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(BotDesktopWindow.ShowMain));
        }
    }
}
