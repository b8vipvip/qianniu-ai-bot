using System.Windows;
using System.Windows.Controls;

namespace Bot.Knowledge
{
    public class KnowledgeCenterWindow : Window
    {
        private TabControl _tabs;
        private KnowledgeImportControl _import;
        private KnowledgeManagerControl _manager;

        public KnowledgeCenterWindow()
        {
            Title = "AI客服 - 知识库";
            Width = 1100;
            Height = 720;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            _tabs = new TabControl();
            Content = _tabs;
            _manager = new KnowledgeManagerControl();
            _import = new KnowledgeImportControl(delegate { ShowManager(); });
            _tabs.Items.Add(new TabItem { Header = "智能导入", Content = _import });
            _tabs.Items.Add(new TabItem { Header = "问答管理", Content = _manager });
        }

        public void ShowManager()
        {
            _manager.RefreshData();
            _tabs.SelectedIndex = 1;
        }

        public void NavigateToManager(
            string seller,
            string buyer,
            string question,
            string answer)
        {
            ShowManager();
            if (!_manager.LocateEntry(seller, buyer, question, answer))
            {
                MessageBox.Show(
                    this,
                    "没有找到完全对应的知识条目，已按当前问题或答案显示搜索结果。",
                    "知识库定位",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }

        protected override void OnClosed(System.EventArgs e)
        {
            if (_import != null) _import.CancelForWindowClose();
            base.OnClosed(e);
        }

        public static void MyShow(Window owner)
        {
            var wnd = new KnowledgeCenterWindow();
            if (owner != null) wnd.Owner = owner;
            wnd.Show();
        }

        public static void ShowManagerAndLocate(
            Window owner,
            string seller,
            string buyer,
            string question,
            string answer)
        {
            var wnd = new KnowledgeCenterWindow();
            if (owner != null) wnd.Owner = owner;
            wnd.Show();
            wnd.NavigateToManager(seller, buyer, question, answer);
        }
    }
}
