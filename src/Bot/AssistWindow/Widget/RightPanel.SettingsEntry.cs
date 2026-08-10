using Bot.Automation.ChatDeskNs;
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

            var seller = DeskSellerBindingRegistry.GetSeller(desk);
            if (string.IsNullOrWhiteSpace(seller))
            {
                seller = QnAccountFinder.ResolveSellerNameForWindow(
                    desk.ProcessId,
                    desk.Hwnd.Handle,
                    desk.WndTitle);
                if (!QnAccountFinder.IsGenericReceptionTitle(seller))
                {
                    var bound = DeskSellerBindingRegistry.BindResolvedSeller(
                        desk, seller, "settings-window-identity");
                    if (bound == null) seller = string.Empty;
                }
            }

            if (string.IsNullOrWhiteSpace(seller) || QnAccountFinder.IsGenericReceptionTitle(seller))
            {
                MessageBox.Show(
                    "当前千牛接待窗口已经检测到，但还没有取得该窗口唯一对应的客服账号。\n\n"
                    + "请回到这个千牛窗口，点击一次任意买家会话；Bot 会在千牛的会话切换事件中把当前 seller 与这个窗口一对一绑定。"
                    + "系统不会让两个店铺共享同一个窗口，以避免串店。",
                    "正在识别店铺身份",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            WndOption.MyShow(seller, Wnd);
        }
    }
}
