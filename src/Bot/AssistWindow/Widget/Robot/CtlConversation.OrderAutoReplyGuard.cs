using Bot.ChromeNs;
using System;
using System.Threading;
using System.Windows;

namespace Bot.AssistWindow.Widget.Robot
{
    public partial class CtlConversation
    {
        private static int _orderAutoReplyGuardInstalled;

        internal static void InstallOrderAutoReplyGuard()
        {
            if (Interlocked.Exchange(ref _orderAutoReplyGuardInstalled, 1) != 0) return;
            EventManager.RegisterClassHandler(
                typeof(CtlConversation),
                FrameworkElement.InitializedEvent,
                new RoutedEventHandler(OnConversationInitializedForOrderAutoReplyGuard),
                true);
        }

        private static void OnConversationInitializedForOrderAutoReplyGuard(object sender, RoutedEventArgs e)
        {
            var ctl = sender as CtlConversation;
            if (ctl == null) return;
            // Initialized 发生在 Setup 之前，因此排到当前UI调用结束后执行；
            // 这样即使卡片没有进入当前可见买家列表，也能在 QNRpa 的 180ms 人工介入检查前完成登记。
            ctl.Dispatcher.BeginInvoke(new Action(ctl.PrepareOrderAutoReplyManualBypass));
        }

        private void PrepareOrderAutoReplyManualBypass()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_seller)
                    || string.IsNullOrWhiteSpace(_buyer)
                    || string.IsNullOrWhiteSpace(_answer)
                    || string.IsNullOrWhiteSpace(_question))
                {
                    return;
                }

                if (!_question.StartsWith("[买家下单]", StringComparison.Ordinal)) return;

                // 下单自动回复生成卡片后，QNRpa 会等待约 180ms 再做“是否有人工回复”检查。
                // 这里提前登记这一次精确答案为 Bot 预期发送，避免上一条 Bot 卖家回显被误判成人工介入。
                KnowledgeLearningService.AllowNextManualSend(_seller, _buyer, _answer);
                BotLib.Log.Info("下单自动回复已登记发送豁免，避免误判人工回复: seller="
                    + _seller + ", buyer=" + _buyer + ", question=" + _question);
            }
            catch (Exception ex)
            {
                BotLib.Log.Info("登记下单自动回复发送豁免失败，继续原流程：" + ex.Message);
            }
        }
    }
}
