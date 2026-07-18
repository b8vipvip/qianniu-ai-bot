using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Bot.ChromeNs;

namespace Bot.AssistWindow.Widget.Robot
{
    public class ConversationResendEventArgs : EventArgs
    {
        public string Seller { get; private set; }
        public string Buyer { get; private set; }
        public string Question { get; private set; }
        public string Answer { get; private set; }

        public ConversationResendEventArgs(string seller, string buyer, string question, string answer)
        {
            Seller = seller ?? string.Empty;
            Buyer = buyer ?? string.Empty;
            Question = question ?? string.Empty;
            Answer = answer ?? string.Empty;
        }
    }

    public class ConversationEditEventArgs : EventArgs
    {
        public string Seller { get; private set; }
        public string Buyer { get; private set; }
        public string Question { get; private set; }
        public string Answer { get; private set; }

        public ConversationEditEventArgs(string seller, string buyer, string question, string answer)
        {
            Seller = seller ?? string.Empty;
            Buyer = buyer ?? string.Empty;
            Question = question ?? string.Empty;
            Answer = answer ?? string.Empty;
        }
    }

    public partial class CtlConversation : UserControl
    {
        private readonly string sendedChar = "   √√";
        private string _seller = string.Empty;
        private string _buyer = string.Empty;
        private string _question = string.Empty;
        private string _answer = string.Empty;
        private bool _canResend = true;

        public event EventHandler<ConversationResendEventArgs> ResendRequested;
        public event EventHandler<ConversationEditEventArgs> EditRequested;

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
            _question = question ?? string.Empty;
            _answer = answer ?? string.Empty;
            _canResend = true;
            txtQuestion.Text = _question;
            txtAnswer.Text = _answer;
            SetSource(KnowledgeLearningService.ResolveAnswerSource(_seller, _buyer, _question, _answer));
            txtStatus.Text = isAutoReply ? "正在发送..." : "仅生成答案";
            txtStatus.Foreground = new SolidColorBrush(isAutoReply ? Color.FromRgb(47, 128, 237) : Color.FromRgb(107, 114, 128));
            txtTime.Text = DateTime.Now.ToString("HH:mm:ss");
        }

        public void SetAnswer(string answer)
        {
            _answer = answer ?? string.Empty;
            Ui(() =>
            {
                txtAnswer.Text = _answer;
                var source = KnowledgeLearningService.ResolveAnswerSource(_seller, _buyer, _question, _answer);
                if (!string.IsNullOrWhiteSpace(source)) SetSource(source);
                txtTime.Text = DateTime.Now.ToString("HH:mm:ss");
            });
        }

        public void SetSource(string source)
        {
            Ui(() =>
            {
                txtSource.Text = source ?? string.Empty;
                txtSource.Visibility = string.IsNullOrWhiteSpace(source) ? Visibility.Collapsed : Visibility.Visible;
                txtSource.Foreground = new SolidColorBrush(source == "本地" ? Color.FromRgb(39, 174, 96) : source.StartsWith("人工") ? Color.FromRgb(155, 81, 224) : Color.FromRgb(47, 128, 237));
            });
        }

        public void SetStatus(string text, bool success)
        {
            Ui(() =>
            {
                txtStatus.Text = text ?? string.Empty;
                txtStatus.Foreground = new SolidColorBrush(success ? Color.FromRgb(39, 174, 96) : Color.FromRgb(235, 87, 87));
                txtTime.Text = DateTime.Now.ToString("HH:mm:ss");
            });
        }

        public void SetSkipped(string detail)
        {
            _canResend = false;
            Ui(() =>
            {
                txtAnswer.Text = _answer;
                txtStatus.Text = "已跳过，未发送";
                txtStatus.ToolTip = detail ?? string.Empty;
                txtStatus.Foreground = new SolidColorBrush(Color.FromRgb(242, 153, 74));
                txtTime.Text = DateTime.Now.ToString("HH:mm:ss");
            });
        }

        private void Ui(Action action)
        {
            if (Dispatcher.CheckAccess()) action();
            else Dispatcher.BeginInvoke(action);
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
                string blockedReason;
                string manualAnswer;
                if (!success && KnowledgeLearningService.TryTakeSendBlock(_seller, _buyer, _answer, out blockedReason, out manualAnswer))
                {
                    txtAnswer.Text = _answer;
                    txtStatus.Text = blockedReason;
                    txtStatus.Foreground = new SolidColorBrush(Color.FromRgb(242, 153, 74));
                    SetSource("人工回复");
                }
                else
                {
                    txtAnswer.Text = success ? _answer + sendedChar : _answer;
                    txtStatus.Text = success ? (string.IsNullOrWhiteSpace(detail) ? "已发送" : detail) : (string.IsNullOrWhiteSpace(detail) ? "发送失败" : detail);
                    txtStatus.Foreground = new SolidColorBrush(success ? Color.FromRgb(39, 174, 96) : Color.FromRgb(235, 87, 87));
                }
                txtTime.Text = DateTime.Now.ToString("HH:mm:ss");
            });
        }

        private void RaiseResendRequested()
        {
            var handler = ResendRequested;
            if (handler != null) handler(this, new ConversationResendEventArgs(_seller, _buyer, _question, _answer));
        }

        private void RaiseEditRequested()
        {
            var handler = EditRequested;
            if (handler != null) handler(this, new ConversationEditEventArgs(_seller, _buyer, _question, _answer));
        }

        private void txtAnswer_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            var menu = new ContextMenu();
            if (_canResend)
            {
                var resend = new MenuItem { Header = "重发" };
                resend.Click += (s, args) => RaiseResendRequested();
                menu.Items.Add(resend);
            }
            var edit = new MenuItem { Header = "修改" };
            edit.Click += (s, args) => RaiseEditRequested();
            menu.Items.Add(edit);
            menu.PlacementTarget = txtAnswer;
            menu.IsOpen = true;
        }
    }
}
