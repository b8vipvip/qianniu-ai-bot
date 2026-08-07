using Bot.AssistWindow.Widget.Robot;
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Bot.AssistWindow
{
    /// <summary>
    /// Passive UI-only bridge for the standalone desktop window.
    ///
    /// The existing Desk -> WndAssist -> CtlRobot chain remains authoritative.
    /// This bridge observes controls that the legacy UI has already created and mirrors
    /// them into the desktop window. It never generates messages, calls AI, queries
    /// Qianniu, or records runtime statistics.
    /// </summary>
    internal static class DesktopBotUiBridge
    {
        private static readonly object Sync = new object();
        private static readonly ConditionalWeakTable<CtlConversation, object> ObservedConversations =
            new ConditionalWeakTable<CtlConversation, object>();
        private static WeakReference _robotReference;
        private static int _initialized;

        public static void Register(CtlRobot robot)
        {
            if (robot == null) return;
            InitializeObserver();
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

        private static void InitializeObserver()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            EventManager.RegisterClassHandler(
                typeof(CtlConversation),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnConversationLoaded),
                true);
        }

        private static void OnConversationLoaded(object sender, RoutedEventArgs e)
        {
            var conversation = sender as CtlConversation;
            if (conversation == null || conversation.IsDesktopMirror) return;

            object marker;
            if (ObservedConversations.TryGetValue(conversation, out marker)) return;
            try { ObservedConversations.Add(conversation, new object()); }
            catch { return; }

            var snapshot = conversation.GetDesktopSnapshot();
            if (snapshot == null) return;
            Invoke(robot =>
            {
                robot.MirrorSeller(snapshot.Seller);
                robot.MirrorBuyer(snapshot.Buyer);
                robot.MirrorConversation(
                    snapshot.Seller,
                    snapshot.Buyer,
                    snapshot.Question,
                    snapshot.Answer,
                    snapshot.IsAutoReply,
                    snapshot.AnswerSource);
            });
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
