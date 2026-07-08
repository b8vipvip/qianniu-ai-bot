using System.Windows;
using System.Windows.Controls;
using Bot.Options;

namespace Bot.AssistWindow.Widget
{
    public partial class RightPanel
    {
        private void menuFeatureSettings_Click(object sender, RoutedEventArgs e)
        {
            var item = sender as MenuItem;
            var page = "知识库";
            if (item != null)
            {
                page = (item.Tag ?? item.Header ?? page).ToString();
            }
            FeatureSettingsWindow.MyShow(Wnd, page);
        }
    }
}
