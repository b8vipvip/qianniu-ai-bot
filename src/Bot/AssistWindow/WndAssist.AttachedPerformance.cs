using Bot.Automation.ChatDeskNs;
using BotLib;
using BotLib.Misc;
using BotLib.Wpf.Extensions;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace Bot.AssistWindow
{
    public partial class WndAssist
    {
        private static readonly object AttachedPerformanceBootstrap = InitializeAttachedPerformance();
        private static int _lowChurnTimerInstalled;
        private bool _lowChurnTrackingInstalled;

        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;
        private static readonly IntPtr HwndNotTopmost = new IntPtr(-2);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int x,
            int y,
            int cx,
            int cy,
            uint uFlags);

        private static object InitializeAttachedPerformance()
        {
            EventManager.RegisterClassHandler(
                typeof(WndAssist),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(AttachedWindowLoadedForPerformance),
                true);
            return new object();
        }

        private static void AttachedWindowLoadedForPerformance(object sender, RoutedEventArgs e)
        {
            var wnd = sender as WndAssist;
            if (wnd == null) return;

            // Let the historical Loaded handler finish first, then replace the high-churn
            // tracking hooks. This keeps all existing controls and business logic intact.
            wnd.Dispatcher.BeginInvoke(new Action(delegate
            {
                wnd.InstallLowChurnTracking();
            }));

            if (Interlocked.Exchange(ref _lowChurnTimerInstalled, 1) == 0)
            {
                try
                {
                    var oldTimer = _timer;
                    _timer = new NoReEnterTimer(SafePeriodicTrack, 5000, 1500);
                    if (oldTimer != null) oldTimer.Stop();
                    Log.Info("贴窗Bot已切换低刷新跟随：5秒兜底校准，禁用周期性Topmost抢焦点。" );
                }
                catch (Exception ex)
                {
                    Log.Exception(ex);
                }
            }
        }

        private void InstallLowChurnTracking()
        {
            if (_lowChurnTrackingInstalled || Desk == null) return;
            _lowChurnTrackingInstalled = true;

            // Replace only the event paths whose historical implementation calls Track()
            // and raises the WPF window via Topmost. Minimize/hide/close/maximize behavior
            // remains on the original handlers.
            Desk.EvShow -= Desk_EvShow;
            Desk.EvNormalize -= Desk_EvNormalize;
            Desk.EvMoved -= Desk_EvMoved;
            Desk.EvResized -= Desk_EvResized;
            Desk.EvGetForeground -= Desk_EvGetForeground;

            Desk.EvShow += SafeDesk_EvShow;
            Desk.EvNormalize += SafeDesk_EvNormalize;
            Desk.EvMoved += SafeDesk_EvMoved;
            Desk.EvResized += SafeDesk_EvResized;
            Desk.EvGetForeground += SafeDesk_EvGetForeground;
            Closed += SafeTrackingClosed;

            SafeTrackGeometry(false, false);
        }

        private void SafeTrackingClosed(object sender, EventArgs e)
        {
            try
            {
                if (Desk == null) return;
                Desk.EvShow -= SafeDesk_EvShow;
                Desk.EvNormalize -= SafeDesk_EvNormalize;
                Desk.EvMoved -= SafeDesk_EvMoved;
                Desk.EvResized -= SafeDesk_EvResized;
                Desk.EvGetForeground -= SafeDesk_EvGetForeground;
            }
            catch { }
        }

        private static void SafePeriodicTrack()
        {
            try
            {
                foreach (var wnd in AssistBag.ToArray())
                {
                    if (wnd == null || wnd.IsClosed) continue;
                    wnd.SafeTrackGeometry(false, true);
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
        }

        private void SafeDesk_EvShow(object sender, DeskEventArgs e)
        {
            DispatcherEx.xInvoke(delegate
            {
                if (Desk == null || Desk.IsMinimized || !Desk.IsVisible) return;
                SafeShowAssist();
            });
        }

        private void SafeDesk_EvNormalize(object sender, DeskEventArgs e)
        {
            WakeUp();
            SafeTrackGeometry(false, false);
        }

        private void SafeDesk_EvMoved(object sender, DeskEventArgs e)
        {
            SafeTrackGeometry(false, false);
        }

        private void SafeDesk_EvResized(object sender, DeskEventArgs e)
        {
            SafeTrackGeometry(false, false);
        }

        private void SafeDesk_EvGetForeground(object sender, DeskEventArgs e)
        {
            DispatcherEx.xInvoke(delegate
            {
                SafeShowAssist();
                FollowDeskZOrderWithoutActivation();
            });
        }

        private void SafeShowAssist()
        {
            if (!IsVisible) Show();
            SafeTrackGeometry(false, false);
            WakeUpAssist();
            FollowDeskZOrderWithoutActivation();
        }

        private void SafeTrackGeometry(bool adjustDeskLocation, bool periodic)
        {
            if (_isTracking || IsClosed) return;
            _isTracking = true;
            try
            {
                DispatcherEx.xInvoke(delegate
                {
                    try
                    {
                        if (!IsLoaded || IsHidden())
                        {
                            if (IsVisible) Hide();
                            return;
                        }

                        if (!IsVisible && Desk.GetVisiblePercent(true) > 0.0)
                            Show();
                        if (!IsVisible) return;

                        SetPanelsSize();
                        if (_isFirstTrack || adjustDeskLocation)
                        {
                            _isFirstTrack = false;
                            SetDeskLocation();
                        }
                        SetRightPanelPosition();

                        // Z-order is only corrected on real foreground/move/show events.
                        // The periodic fallback never raises the Bot window.
                        if (!periodic && Desk.IsForeground)
                            FollowDeskZOrderWithoutActivation();
                    }
                    catch (Exception ex)
                    {
                        Log.Exception(ex);
                    }
                });
            }
            finally
            {
                _isTracking = false;
            }
        }

        private void FollowDeskZOrderWithoutActivation()
        {
            try
            {
                if (!IsLoaded || Desk == null || Desk.Hwnd == null || Desk.Hwnd.Handle == 0) return;
                var hwnd = new IntPtr(Handle);
                var flags = SwpNoMove | SwpNoSize | SwpNoActivate;

                // Explicitly remove accidental topmost state left by historical versions,
                // then place the attached panel next to its Qianniu Desk in normal z-order.
                SetWindowPos(hwnd, HwndNotTopmost, 0, 0, 0, 0, flags);
                SetWindowPos(hwnd, new IntPtr(Desk.Hwnd.Handle), 0, 0, 0, 0, flags);
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
        }
    }
}
