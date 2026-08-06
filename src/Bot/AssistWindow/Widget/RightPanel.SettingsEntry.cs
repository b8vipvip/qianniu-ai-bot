using Bot.Options;
using System.Windows;

namespace Bot.AssistWindow.Widget
{
    public partial class RightPanel
    {
        private void btnOpenSettings_Click(object sender, RoutedEventArgs e)
        {
            WndOption.MyShow(Wnd.Desk.WndTitle, Wnd);
        }
    }
}
