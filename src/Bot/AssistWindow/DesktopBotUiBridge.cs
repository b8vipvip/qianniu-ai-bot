using Bot.AssistWindow.Widget.Robot;
using System;
using System.Windows.Threading;

namespace Bot.AssistWindow
{
    /// <summary>
    /// Passive UI-only bridge for the standalone desktop window.
    ///
    /// The existing Desk -> WndAssist -> CtlRobot chain remains authoritative.
    /// This bridge mirrors already-produced UI events into the desktop window and
    /// must never generate messages, call AI, query Qianniu, or record runtime stats.
    /// </summary>
    internal static class DesktopBotUiBridge
    {
        private static readonly object Sync = new object();
        private static WeakReference _robotReference;

        public static void Register(CtlRobot robot)
        {
            if (robot == null) return;
            lock (Sync)
            {
                _robotReference = new WeakReference(robot);
            }
        }

        public static void Unregister(CtlRobot robot)
        {
            lock (Sync)
            {
                if (_robotReference == null) return;
                var current = _robotReference.Target as CtlRobot;
                if (current == null || ReferenceEquals(current, robot))
                {
                    _robotReference = null;
                }
            }
        }

        public static void ChangeSeller(string seller)
        {
            Invoke(robot => robot.MirrorSeller(seller));
        }

        public static void ChangeBuyer(string buyer)
        {
            Invoke(robot => robot.MirrorBuyer(buyer));
        }

        public static void AddConversation(
            string seller,
            string buyer,
            string question,
            string answer,
            bool isAutoReply,
            string answerSource)
        {
            Invoke(robot => robot.MirrorConversation(
                seller,
                buyer,
                question,
                answer,
                isAutoReply,
                answerSource));
        }

        private static void Invoke(Action<CtlRobot> action)
        {
            if (action == null) return;
            CtlRobot robot = null;
            lock (Sync)
            {
                if (_robotReference != null && _robotReference.IsAlive)
                {
                    robot = _robotReference.Target as CtlRobot;
                }
            }
            if (robot == null) return;

            try
            {
                var dispatcher = robot.Dispatcher;
                if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
                if (dispatcher.CheckAccess())
                {
                    action(robot);
                }
                else
                {
                    dispatcher.BeginInvoke(DispatcherPriority.Background, action, robot);
                }
            }
            catch
            {
                // The standalone UI is optional. It must never affect the Desk runtime chain.
            }
        }
    }
}
