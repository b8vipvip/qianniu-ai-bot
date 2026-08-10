using BotLib;
using System;
using System.Windows.Threading;

namespace Bot.AssistWindow
{
    public partial class WndAssist
    {
        /// <summary>
        /// Multi-shop mode must expose one attached Bot shell for every visible Qianniu
        /// reception window. This method only affects window presentation; it does not
        /// enable AI or sending while seller identity is unresolved.
        /// </summary>
        internal void EnsureVisibleForMultiShopAttachedMode()
        {
            if (Desk == null || !Desk.IsAlive || !Desk.IsVisibleAndNotMinimized) return;
            if (Dispatcher == null || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;

            Action show = () =>
            {
                try
                {
                    if (IsClosed || Desk == null || !Desk.IsAlive || !Desk.IsVisibleAndNotMinimized) return;
                    if (!IsVisible) ShowAssist();
                    else Track(false, true);
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("保持多店铺贴窗Bot可见失败: " + ex.Message, 10);
                }
            };

            if (Dispatcher.CheckAccess()) show();
            else Dispatcher.BeginInvoke(DispatcherPriority.Background, show);
        }
    }
}
