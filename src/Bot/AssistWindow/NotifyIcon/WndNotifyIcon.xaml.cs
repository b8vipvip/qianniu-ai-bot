using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using Bot.AssistWindow.NotifyIcon.MenuCreator;
using Bot.Common;
using BotLib;
using BotLib.Extensions;
using BotLib.Misc;
using BotLib.Wpf.Extensions;
using Bot.Common.Windows;

namespace Bot.AssistWindow.NotifyIcon
{
    public partial class WndNotifyIcon : Window
    {
        private const int WM_HOTKEY = 786;
        private static WndNotifyIcon _inst;
        private bool _exitRequested;

        public static WndNotifyIcon Inst
        {
            get
            {
                if (_inst == null)
                {
                    _inst = new WndNotifyIcon();
                }
                return _inst;
            }
        }

        private WndNotifyIcon()
        {
            InitializeComponent();
            Loaded += WndNotifyIcon_Loaded;
        }

        private async void WndNotifyIcon_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= WndNotifyIcon_Loaded;
            this.xMoveToWorkAreaCenter();
            this.xShowFirstTime();
            DelayCaller.CallAfterDelay(() =>
            {
                if (!_exitRequested && IsLoaded)
                {
                    Visibility = Visibility.Collapsed;
                }
            }, 5000, true);
            notifyIcon.Text = string.Format("{0}({1})", Params.AppName, Params.VersionStr);
            CreateHelpMenu();
            notifyIcon.StartBlink(base.FindResource("iconGray") as ImageSource);

            await BootStrap.Init();
            notifyIcon.StopBlink();
            if (!_exitRequested && IsLoaded)
            {
                Visibility = Visibility.Collapsed;
            }
        }

        private void CreateHelpMenu()
        {
            HelpMenuCreator.Create(notifyIcon);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            try
            {
                base.OnSourceInitialized(e);
                var hWndSrc = PresentationSource.FromVisual(this) as HwndSource;
                if (hWndSrc != null)
                {
                    hWndSrc.AddHook(HwndSourceHookDelegate);
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
        }

        private IntPtr HwndSourceHookDelegate(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void btnExit_Click(object sender, EventArgs e)
        {
            if (_exitRequested) return;
            _exitRequested = true;

            try { notifyIcon.StopBlink(); }
            catch (Exception ex) { Log.Exception(ex); }

            var app = System.Windows.Application.Current;
            if (app == null) return;

            // A WPF Window cannot be shown again after Close().  The tray window may already
            // have been closed by the attached-only UI lifecycle, so exit must never touch
            // Visibility/Show/EnsureHandle here.  Shutdown the application directly instead.
            if (app.Dispatcher.CheckAccess())
            {
                app.Shutdown();
            }
            else
            {
                app.Dispatcher.BeginInvoke(new Action(app.Shutdown));
            }
        }

        public void AddSellerMenuItem(string nick)
        {
            DispatcherEx.xInvoke(() =>
            {
                try
                {
                    SellerMenuCreator.Create(notifyIcon, nick);
                }
                catch (Exception e)
                {
                    Log.Exception(e);
                }
            });
        }

        public void RemoveSellerMenuItem(HashSet<string> removeNicks)
        {
            if (!removeNicks.xIsNullOrEmpty())
            {
                foreach (var nick in removeNicks)
                {
                    SellerMenuCreator.RemoveMenuItem(notifyIcon, nick);
                }
            }
        }
    }
}
