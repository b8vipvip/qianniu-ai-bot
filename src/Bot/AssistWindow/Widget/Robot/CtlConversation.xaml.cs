using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Bot.AssistWindow.Widget.Robot
{
    public class ConversationResendEventArgs : EventArgs
    {
        public string Seller { get; private set; }
        public string Buyer { get; private set; }
        public string Answer { get; private set; }

        public ConversationResendEventArgs(string seller, string buyer, string answer)
        {
            Seller = seller ?? string.Empty;
            Buyer = buyer ?? string.Empty;
            Answer = answer ?? string.Empty;
        }
    }

    /// <summary>
    /// Interaction logic for CtlDialog.xaml
    /// </summary>
    public partial class CtlConversation : UserControl
    {
        private readonly string sendedChar = "   √√";
        private string _seller = string.Empty;
        private string _buyer = string.Empty;
        private string _answer = string.Empty;

        public event EventHandler<ConversationResendEventArgs> ResendRequested;

        public CtlConversation()
        {
            InitializeComponent();
        }

        public static CtlConversation Create(string seller, string buyer, string question, string answer, bool isAutoReply = false)
        {
            var dlg = new CtlConversation();
            dlg.Setup(seller, buyer, question, answer, isAutoReply);
            return dlg;
        }

        public void Setup(string seller, string buyer, string question, string answer, bool isAutoReply)
        {
            _seller = seller ?? string.Empty;
            _buyer = buyer ?? string.Empty;
            _answer = answer ?? string.Empty;
            txtQuestion.Text = question ?? string.Empty;
            txtAnswer.Text = _answer;
            txtStatus.Text = isAutoReply ? "正在发送..." : "仅生成答案";
            txtStatus.Foreground = new SolidColorBrush(isAutoReply ? Color.FromRgb(47, 128, 237) : Color.FromRgb(107, 114, 128));
            txtTime.Text = DateTime.Now.ToString("HH:mm:ss");
        }

        private void Ui(Action action)
        {
            if (Dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                Dispatcher.BeginInvoke(action);
            }
        }

        public void SetSendPending(string text)
        {
            Ui(() =>
            {
                txtAnswer.Text = _answer;
                txtStatus.Text = string.IsNullOrWhiteSpace(text) ? "正在发送..." : text;
                txtStatus.Foreground = new SolidColorBrush(Color.FromRgb(47, 128, 237));
                txtTime.Text = DateTime.Now.ToString("HH:mm:ss");
            });
        }

        public void SetSendResult(bool success, string detail)
        {
            Ui(() =>
            {
                txtAnswer.Text = success ? _answer + sendedChar : _answer;
                txtStatus.Text = success ? (string.IsNullOrWhiteSpace(detail) ? "已发送" : detail) : (string.IsNullOrWhiteSpace(detail) ? "发送失败" : detail);
                txtStatus.Foreground = new SolidColorBrush(success ? Color.FromRgb(39, 174, 96) : Color.FromRgb(235, 87, 87));
                txtTime.Text = DateTime.Now.ToString("HH:mm:ss");
            });
        }

        private void RaiseResendRequested()
        {
            var handler = ResendRequested;
            if (handler != null)
            {
                handler(this, new ConversationResendEventArgs(_seller, _buyer, _answer));
            }
        }

        private void txtAnswer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            var menu = new ContextMenu();
            var item = new MenuItem { Header = "重发这条答案" };
            item.Click += (s, args) => RaiseResendRequested();
            menu.Items.Add(item);
            menu.PlacementTarget = txtAnswer;
            menu.IsOpen = true;
        }
    }
}