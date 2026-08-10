using Bot.AssistWindow.NotifyIcon;
using Bot.Automation.ChatDeskNs;
using BotLib;
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
    /// The standalone workbench is the fallback UI when no Qianniu reception window exists.
    /// When one or more Desk instances are discovered, their normal WndAssist panels are the
    /// default UI (one attached Bot panel per Desk). The standalone window remains available
    /// from the tray menu at any time.
    /// </summary>
    internal static class BotDesktopStartup
    {
        private static int _initialized;
        private static DispatcherTimer _startupTimer;
        private static DateTime _waitStartedAt;

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
            dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => StartFallbackTimer(dispatcher)));
        }

        private static void StartFallbackTimer(Dispatcher dispatcher)
        {
            if (_startupTimer != null) return;
            _waitStartedAt = DateTime.UtcNow;
            _startupTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(250),
                DispatcherPriority.Background,
                StartupTimer_Tick,
                dispatcher);
            _startupTimer.Start();
        }

        private static void StartupTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                if (Desk.Snapshot().Count > 0)
                {
                    StopFallbackTimer();
                    Log.Info("已发现千牛接待窗口，默认使用每店铺独立贴窗 Bot；独立工作台可从托盘手动打开。" );
                    return;
                }

                // Give DeskScanner enough time to discover already-open Qianniu windows before
                // deciding that this is a true no-Qianniu startup.
                if ((DateTime.UtcNow - _waitStartedAt).TotalSeconds < 2.5) return;

                StopFallbackTimer();
                BotDesktopWindow.ShowMain();
                Log.Info("启动后未发现千牛接待窗口，显示独立 Bot 工作台作为回退界面。" );
            }
            catch (Exception ex)
            {
                StopFallbackTimer();
                Log.ErrorWithMaxCount("判断 Bot 默认显示模式失败，回退独立工作台: " + ex.Message, 5);
                BotDesktopWindow.ShowMain();
            }
        }

        private static void StopFallbackTimer()
        {
            if (_startupTimer == null) return;
            _startupTimer.Stop();
            _startupTimer.Tick -= StartupTimer_Tick;
            _startupTimer = null;
        }
    }
}
