using Bot.AssistWindow;
using Bot.AssistWindow.Widget;
using Bot.AssistWindow.Widget.Robot;
using Bot.Automation.ChatDeskNs;
using Bot.ShopScope;
using BotLib;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Windows.Threading;

namespace Bot
{
    public partial class App
    {
        private readonly object _multiShopRuntimeSessionBootstrap =
            ChromeNs.MultiShopRuntimeSessionCoordinator.InitializeForApp();
    }
}

namespace Bot.ChromeNs
{
    /// <summary>
    /// Keeps QN/CDP sellers, native Qianniu Desk HWNDs and ShopKeys aligned without
    /// replacing the existing message pipeline. A seller may own only one native Desk and
    /// a Desk may own only one seller for the lifetime of that live binding.
    /// </summary>
    internal static class MultiShopRuntimeSessionCoordinator
    {
        private static readonly ConcurrentDictionary<QN, byte> Subscribed =
            new ConcurrentDictionary<QN, byte>();
        private static Timer _timer;
        private static int _started;

        public static object InitializeForApp()
        {
            Start();
            return new object();
        }

        public static void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            _timer = new Timer(_ => Refresh(), null, 300, 400);
            Log.Info("多店铺运行时会话协调器已启动。");
        }

        internal static bool EnsureShopBinding(ShopContext shop)
        {
            if (shop == null || string.IsNullOrWhiteSpace(shop.DisplayName)) return false;
            var qn = QN.FindExistingBySellerNick(shop.DisplayName);
            if (qn == null || qn.Rpa == null) return false;
            var ok = qn.Rpa.EnsureSellerDeskBinding(true);
            SyncAttachedUi(qn);
            return ok;
        }

        private static void Refresh()
        {
            try
            {
                foreach (var qn in QN.GetRuntimeSafetySnapshot())
                {
                    if (qn == null) continue;
                    if (Subscribed.TryAdd(qn, 0)) Subscribe(qn);
                    if (qn.Rpa != null) qn.Rpa.EnsureSellerDeskBinding();
                    SyncAttachedUi(qn);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("刷新多店铺运行时会话失败: " + ex.Message, 10);
            }
        }

        private static void Subscribe(QN qn)
        {
            qn.EvSellerSwitched += Qn_EvSellerSwitched;
            qn.EvBuyerSwitched += Qn_EvBuyerSwitched;
            qn.EvRecieveNewMessage += Qn_EvRecieveNewMessage;
            Log.Info("已订阅店铺运行时会话: seller=" + Seller(qn));
        }

        private static void Qn_EvSellerSwitched(object sender, SellerSwitchedEventArgs e)
        {
            var qn = sender as QN;
            // EvSellerSwitched is raised by the active chat/dialog switch path. It is the
            // safest moment to associate an unresolved generic Desk with the seller whose
            // native Qianniu reception window is currently foreground. Never do this from
            // background message/status events.
            DeskSellerBindingRegistry.BindForegroundSeller(qn, "seller-switched-foreground");
            EnsureQn(qn, true);
        }

        private static void Qn_EvBuyerSwitched(object sender, BuyerSwitchedEventArgs e)
        {
            EnsureQn(sender as QN, true);
        }

        private static void Qn_EvRecieveNewMessage(object sender, RecieveNewMessageEventArgs e)
        {
            // Background messages must not create seller-to-HWND associations. They may only
            // use a relationship that was already proven by native title or active switching.
            EnsureQn(sender as QN, true);
        }

        private static void EnsureQn(QN qn, bool force)
        {
            if (qn == null) return;
            try
            {
                if (qn.Rpa != null) qn.Rpa.EnsureSellerDeskBinding(force);
                SyncAttachedUi(qn);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("校准店铺运行时会话失败: seller=" + Seller(qn)
                    + ", " + ex.Message, 10);
            }
        }

        private static void SyncAttachedUi(QN qn)
        {
            var seller = Seller(qn);
            if (seller.Length == 0) return;
            var desk = DeskSellerBindingRegistry.FindSellerDesk(seller);
            if (desk == null) return;
            var assist = desk.AssistWindow;
            if (assist == null || assist.Dispatcher == null) return;

            Action action = () =>
            {
                try
                {
                    var tab = assist.ctlRightPanel.GetTabItem(RightPanel.TabTypeEnum.Robot);
                    var robot = tab == null ? null : tab.Content as CtlRobot;
                    if (robot != null) robot.SynchronizeSellerSession(qn);
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("同步店铺Bot界面失败: seller=" + seller + ", " + ex.Message, 10);
                }
            };

            if (assist.Dispatcher.CheckAccess()) action();
            else if (!assist.Dispatcher.HasShutdownStarted && !assist.Dispatcher.HasShutdownFinished)
                assist.Dispatcher.BeginInvoke(DispatcherPriority.Background, action);
        }

        private static string Seller(QN qn)
        {
            return qn == null || qn.Seller == null
                ? string.Empty
                : (qn.Seller.Nick ?? string.Empty).Trim();
        }
    }
}
