using Bot.Automation.ChatDeskNs.Automators;
using Bot.Options;
using System.Windows;

namespace Bot.AssistWindow.Widget
{
    public partial class RightPanel
    {
        private void btnOpenSettings_Click(object sender, RoutedEventArgs e)
        {
            var desk = Wnd == null ? null : Wnd.Desk;
            if (desk == null || desk.Hwnd == null) return;

            var seller = QnAccountFinder.ResolveSellerNameForWindow(
                desk.ProcessId,
                desk.Hwnd.Handle,
                desk.WndTitle);
            if (QnAccountFinder.IsGenericReceptionTitle(seller))
            {
                MessageBox.Show(
                    "当前千牛接待窗口已经检测到，但还没有取得该窗口唯一对应的客服账号。\n\n"
                    + "请先在这个千牛窗口中点击一次买家会话，等待约 1～2 秒后再打开设置。"
                    + "系统不会在多个店铺之间猜测绑定，以避免串店。",
                    "正在识别店铺身份",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            WndOption.MyShow(seller, Wnd);
        }
    }
}
