from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def write(path, content):
    p = ROOT / path
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(content, encoding="utf-8-sig")


def replace_once(path, old, new):
    text = read(path)
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected one match, got {count}")
    write(path, text.replace(old, new, 1))


ctl_conversation_cs = r'''using System;
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
'''

ctl_conversation_xaml = r'''<UserControl x:Class="Bot.AssistWindow.Widget.Robot.CtlConversation"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
             xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
             xmlns:local="clr-namespace:Bot.AssistWindow.Widget.Robot"
             mc:Ignorable="d">
    <UserControl.Resources>
        <SolidColorBrush x:Key="CardBorder" Color="#E5EAF2" />
        <SolidColorBrush x:Key="QuestionBg" Color="#EAF5FF" />
        <SolidColorBrush x:Key="AnswerBg" Color="#F8FAFC" />
        <SolidColorBrush x:Key="PrimaryText" Color="#1F2937" />
        <SolidColorBrush x:Key="MutedText" Color="#6B7280" />
        <SolidColorBrush x:Key="SuccessGreen" Color="#27AE60" />
    </UserControl.Resources>
    <Border Background="#FFFFFFFF" BorderBrush="{StaticResource CardBorder}" BorderThickness="1" CornerRadius="8" Margin="8 5 8 7" Padding="0">
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
            </Grid.RowDefinitions>
            <Grid Grid.Row="0" Background="{StaticResource QuestionBg}" MinHeight="38">
                <Grid.ColumnDefinitions><ColumnDefinition Width="38"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                <Border Background="#BFE8FF" CornerRadius="6" Margin="6" />
                <TextBlock Text="问" VerticalAlignment="Center" HorizontalAlignment="Center" FontWeight="Bold" Foreground="#164E63" />
                <TextBlock x:Name="txtQuestion" Grid.Column="1" Padding="4 8 8 8" Foreground="{StaticResource PrimaryText}" FontWeight="SemiBold" TextWrapping="Wrap" VerticalAlignment="Center" />
            </Grid>
            <Grid Grid.Row="1" Background="{StaticResource AnswerBg}" MinHeight="42">
                <Grid.ColumnDefinitions><ColumnDefinition Width="38"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                <Border Background="#D6F5E1" CornerRadius="6" Margin="6" />
                <TextBlock Text="答" VerticalAlignment="Center" HorizontalAlignment="Center" FontWeight="Bold" Foreground="#166534" />
                <StackPanel Grid.Column="1" Margin="4 7 8 7">
                    <TextBlock x:Name="txtAnswer" Foreground="{StaticResource PrimaryText}" TextWrapping="Wrap" MouseRightButtonDown="txtAnswer_MouseRightButtonDown" />
                    <StackPanel Orientation="Horizontal" Margin="0 5 0 0">
                        <TextBlock x:Name="txtSource" FontSize="11" FontWeight="SemiBold" Visibility="Collapsed" />
                        <TextBlock Text="  ·  " Foreground="{StaticResource MutedText}" FontSize="11" />
                        <TextBlock x:Name="txtStatus" Foreground="{StaticResource SuccessGreen}" FontSize="11" />
                        <TextBlock Text="  ·  " Foreground="{StaticResource MutedText}" FontSize="11" />
                        <TextBlock x:Name="txtTime" Foreground="{StaticResource MutedText}" FontSize="11" />
                    </StackPanel>
                </StackPanel>
            </Grid>
        </Grid>
    </Border>
</UserControl>
'''

conversation_edit_window = r'''using System;
using System.Windows;
using System.Windows.Controls;

namespace Bot.AssistWindow.Widget.Robot
{
    internal sealed class ConversationEditWindow : Window
    {
        private readonly TextBox _answer;
        public string EditedAnswer { get { return (_answer.Text ?? string.Empty).Trim(); } }

        public ConversationEditWindow(string question, string answer)
        {
            Title = "修改答案";
            Width = 620;
            Height = 430;
            MinWidth = 520;
            MinHeight = 360;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize;

            var root = new DockPanel { Margin = new Thickness(16) };
            Content = root;

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            var cancel = new Button { Content = "取消", Width = 86, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
            cancel.Click += (s, e) => { DialogResult = false; };
            var save = new Button { Content = "保存修改", Width = 96, Height = 30, IsDefault = true };
            save.Click += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(EditedAnswer))
                {
                    MessageBox.Show("答案不能为空。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                DialogResult = true;
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(save);

            var panel = new Grid();
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(90) });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.Children.Add(panel);

            var qLabel = new TextBlock { Text = "本次问题", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) };
            panel.Children.Add(qLabel);
            var qBox = new TextBox { Text = question ?? string.Empty, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(8) };
            Grid.SetRow(qBox, 1);
            panel.Children.Add(qBox);
            var aLabel = new TextBlock { Text = "修改后的答案", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 12, 0, 6) };
            Grid.SetRow(aLabel, 2);
            panel.Children.Add(aLabel);
            _answer = new TextBox { Text = answer ?? string.Empty, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(8) };
            Grid.SetRow(_answer, 3);
            panel.Children.Add(_answer);
        }
    }
}
'''

knowledge_learning_service = r'''using Bot.Knowledge;
using BotLib;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal sealed class KnowledgeLearningResult
    {
        public bool Success { get; set; }
        public bool Added { get; set; }
        public bool Updated { get; set; }
        public string Message { get; set; }
    }

    internal static class KnowledgeLearningService
    {
        private sealed class SourceStamp { public string Source; public DateTime ExpiresAt; }
        private sealed class BlockStamp { public string Reason; public string ManualAnswer; public DateTime ExpiresAt; }
        private static readonly object SaveLock = new object();
        private static readonly ConcurrentDictionary<string, SourceStamp> Sources = new ConcurrentDictionary<string, SourceStamp>();
        private static readonly ConcurrentDictionary<string, BlockStamp> Blocks = new ConcurrentDictionary<string, BlockStamp>();
        private static readonly ConcurrentDictionary<string, DateTime> ManualBypass = new ConcurrentDictionary<string, DateTime>();
        public static event EventHandler KnowledgeBaseChanged;

        public static void RegisterAnswerSource(string seller, string buyer, string question, string answer, string source)
        {
            if (string.IsNullOrWhiteSpace(answer) || string.IsNullOrWhiteSpace(source)) return;
            Sources[AnswerKey(seller, buyer, question, answer)] = new SourceStamp { Source = source, ExpiresAt = DateTime.Now.AddMinutes(30) };
        }

        public static string ResolveAnswerSource(string seller, string buyer, string question, string answer)
        {
            Cleanup();
            SourceStamp stamp;
            return Sources.TryGetValue(AnswerKey(seller, buyer, question, answer), out stamp) && stamp.ExpiresAt >= DateTime.Now ? stamp.Source : string.Empty;
        }

        public static bool TryFindLocalAnswer(string seller, string buyer, string question, out KnowledgeBaseEntry matched, out double score)
        {
            matched = null;
            score = 0;
            var policy = BotFeatureStore.GetMessagePolicy();
            if (policy == null || !policy.EnableKnowledgeBase || string.IsNullOrWhiteSpace(question)) return false;
            var turns = ConversationContextStore.GetRecentTurns(seller, buyer, question, 8);
            var latestAgentPrompt = turns.LastOrDefault(x => x.Role == "assistant" && !string.IsNullOrWhiteSpace(x.Text));
            foreach (var item in BotFeatureStore.GetKnowledgeBase().Where(x => x != null && x.Enabled && !string.IsNullOrWhiteSpace(x.Answer)))
            {
                var currentScore = Score(item, question, false);
                if (latestAgentPrompt != null) currentScore = Math.Max(currentScore, Score(item, latestAgentPrompt.Text, true));
                if (currentScore > score) { score = currentScore; matched = item; }
            }
            return matched != null && score >= 0.84;
        }

        private static double Score(KnowledgeBaseEntry item, string query, bool contextOnly)
        {
            var q = KnowledgeAiService.NormalizeQuestion(query);
            var title = KnowledgeAiService.NormalizeQuestion(item.Title);
            if (string.IsNullOrWhiteSpace(q) || string.IsNullOrWhiteSpace(title)) return 0;
            if (q == title) return contextOnly ? 0.91 : 1.0;
            if (Math.Min(q.Length, title.Length) >= 4 && (q.Contains(title) || title.Contains(q))) return contextOnly ? 0.87 : 0.95;
            foreach (var keyword in SplitKeywords(item.Keywords))
            {
                var k = KnowledgeAiService.NormalizeQuestion(keyword);
                if (k.Length >= 2 && q.Contains(k)) return contextOnly ? 0.85 : 0.90;
            }
            var similarity = BigramSimilarity(q, title);
            if (similarity >= 0.68) return contextOnly ? 0.84 : 0.86;
            return similarity * 0.75;
        }

        private static IEnumerable<string> SplitKeywords(string value)
        {
            return (value ?? string.Empty).Split(new[] { ',', '，', ';', '；', '|', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim());
        }

        private static double BigramSimilarity(string a, string b)
        {
            var aa = Bigrams(a); var bb = Bigrams(b);
            if (aa.Count == 0 || bb.Count == 0) return 0;
            var common = aa.Intersect(bb).Count();
            return (2.0 * common) / (aa.Count + bb.Count);
        }

        private static HashSet<string> Bigrams(string value)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i + 1 < (value ?? string.Empty).Length; i++) set.Add(value.Substring(i, 2));
            return set;
        }

        public static void AllowNextManualSend(string seller, string buyer, string answer)
        {
            ManualBypass[SendKey(seller, buyer, answer)] = DateTime.Now.AddSeconds(15);
        }

        public static bool TryBlockForManualReply(QN qn, string buyer, string candidateAnswer, out string question, out string manualAnswer)
        {
            question = string.Empty; manualAnswer = string.Empty;
            if (qn == null || qn.Seller == null) return false;
            var seller = qn.Seller.Nick ?? string.Empty;
            DateTime bypassUntil;
            var sendKey = SendKey(seller, buyer, candidateAnswer);
            if (ManualBypass.TryRemove(sendKey, out bypassUntil) && bypassUntil >= DateTime.Now) return false;

            DateTime questionTime;
            if (!ConversationContextStore.TryGetLatestBuyerQuestion(seller, buyer, out question, out questionTime)) return false;
            try
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var type = typeof(QN);
                var echoBuyer = Convert.ToString(type.GetField("_lastSellerEchoBuyer", flags).GetValue(qn));
                var echoText = Convert.ToString(type.GetField("_lastSellerEchoText", flags).GetValue(qn));
                var echoTime = (DateTime)type.GetField("_lastSellerEchoTime", flags).GetValue(qn);
                if (!string.Equals((echoBuyer ?? string.Empty).Trim(), (buyer ?? string.Empty).Trim(), StringComparison.Ordinal)) return false;
                if (echoTime < questionTime.AddMilliseconds(-500) || echoTime < DateTime.Now.AddMinutes(-20)) return false;
                if (string.IsNullOrWhiteSpace(echoText) || Normalize(echoText) == Normalize(candidateAnswer)) return false;
                manualAnswer = echoText.Trim();
                Blocks[sendKey] = new BlockStamp { Reason = "已取消：客服已人工回复，本次 Bot 答案未发送", ManualAnswer = manualAnswer, ExpiresAt = DateTime.Now.AddMinutes(5) };
                RegisterAnswerSource(seller, buyer, question, manualAnswer, "人工回复");
                QueueLearn(question, manualAnswer, "人工回复", seller, buyer);
                Log.Info("自动发送已取消：检测到客服人工回复。seller=" + seller + ", buyer=" + buyer);
                return true;
            }
            catch (Exception ex)
            {
                Log.Info("检测客服人工回复失败，继续原发送流程：" + ex.Message);
                return false;
            }
        }

        public static bool TryTakeSendBlock(string seller, string buyer, string answer, out string reason, out string manualAnswer)
        {
            reason = string.Empty; manualAnswer = string.Empty;
            BlockStamp stamp;
            if (!Blocks.TryRemove(SendKey(seller, buyer, answer), out stamp) || stamp.ExpiresAt < DateTime.Now) return false;
            reason = stamp.Reason; manualAnswer = stamp.ManualAnswer;
            return true;
        }

        public static void QueueLearn(string question, string answer, string sourceType, string seller, string buyer)
        {
            if (!CanLearn(question, answer)) return;
            Task.Run(async () =>
            {
                try { await LearnAsync(question, answer, sourceType, seller, buyer); }
                catch (Exception ex) { Log.Info("知识自动学习失败：" + ex.Message); }
            });
        }

        public static async Task<KnowledgeLearningResult> LearnAsync(string question, string answer, string sourceType, string seller, string buyer)
        {
            if (!CanLearn(question, answer)) return new KnowledgeLearningResult { Success = false, Message = "问题或答案为空，未写入知识库" };
            var context = ConversationContextStore.BuildTimelineText(seller, buyer, question, 10);
            var safeQuestion = RedactSensitive(question);
            var safeAnswer = RedactSensitive(answer);
            var messages = new JArray
            {
                new JObject { ["role"] = "system", ["content"] = "你是电商客服知识库整理器。只输出一个JSON对象：{\"question\":\"通用化问题\",\"answer\":\"可复用答案\",\"category\":\"分类\",\"keywords\":[\"关键词\"]}。不得保留真实手机号、验证码、订单号、身份证、银行卡、买家账号等个人数据，必须改写成通用占位表达；不要编造事实。" },
                new JObject { ["role"] = "user", ["content"] = "来源：" + sourceType + "\n原始问题：" + safeQuestion + "\n原始答案：" + safeAnswer + (string.IsNullOrWhiteSpace(context) ? string.Empty : "\n同一买家最近时间线：\n" + RedactSensitive(context)) }
            };

            string learnedQuestion = safeQuestion;
            string learnedAnswer = safeAnswer;
            string category = "自动学习";
            string keywords = string.Empty;
            try
            {
                var result = await Task.Run(() => MyOpenAI.CallStructuredChat(messages, 500, 0.05, 90, CancellationToken.None));
                if (result.Success)
                {
                    var parsed = ParseObject(result.Answer);
                    learnedQuestion = RedactSensitive(Convert.ToString(parsed["question"])).Trim();
                    learnedAnswer = RedactSensitive(Convert.ToString(parsed["answer"])).Trim();
                    category = Convert.ToString(parsed["category"]).Trim();
                    var arr = parsed["keywords"] as JArray;
                    keywords = arr == null ? Convert.ToString(parsed["keywords"]) : string.Join(",", arr.Select(x => x.ToString().Trim()).Where(x => x.Length > 0));
                }
            }
            catch (Exception ex)
            {
                Log.Info("AI整理知识失败，使用安全兜底内容：" + ex.Message);
            }
            if (string.IsNullOrWhiteSpace(learnedQuestion)) learnedQuestion = safeQuestion;
            if (string.IsNullOrWhiteSpace(learnedAnswer)) learnedAnswer = safeAnswer;
            if (string.IsNullOrWhiteSpace(category)) category = "自动学习";
            return SaveLearned(learnedQuestion, learnedAnswer, category, keywords, sourceType);
        }

        private static KnowledgeLearningResult SaveLearned(string question, string answer, string category, string keywords, string sourceType)
        {
            lock (SaveLock)
            {
                var list = BotFeatureStore.GetKnowledgeBase();
                var qKey = KnowledgeAiService.NormalizeQuestion(question);
                var manualPreferred = (sourceType ?? string.Empty).StartsWith("人工", StringComparison.Ordinal);
                var existing = list.FirstOrDefault(x => KnowledgeAiService.NormalizeQuestion(x.Title) == qKey);
                var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                if (existing != null)
                {
                    if (manualPreferred && !string.Equals((existing.Answer ?? string.Empty).Trim(), answer.Trim(), StringComparison.Ordinal))
                    {
                        existing.Answer = answer.Trim(); existing.Category = category; existing.Keywords = keywords; existing.UpdatedAt = now; existing.SourceType = sourceType; existing.AiGenerated = false;
                        BotFeatureStore.SaveKnowledgeBase(list); RaiseKnowledgeChanged();
                        return new KnowledgeLearningResult { Success = true, Updated = true, Message = "已用人工确认答案更新知识库" };
                    }
                    return new KnowledgeLearningResult { Success = true, Message = "知识库已存在相同问题，未重复添加" };
                }
                var contentHash = KnowledgeAiService.ContentHash(question, answer);
                if (list.Any(x => KnowledgeAiService.ContentHash(x.Title, x.Answer) == contentHash)) return new KnowledgeLearningResult { Success = true, Message = "知识库已存在相同内容，未重复添加" };
                list.Add(new KnowledgeBaseEntry
                {
                    Id = Guid.NewGuid().ToString("N"), Enabled = true, Category = category, Title = question.Trim(), Answer = answer.Trim(), Keywords = keywords ?? string.Empty,
                    CreatedAt = now, UpdatedAt = now, AiGenerated = !manualPreferred, SourceType = sourceType ?? "自动学习"
                });
                BotFeatureStore.SaveKnowledgeBase(list); RaiseKnowledgeChanged();
                return new KnowledgeLearningResult { Success = true, Added = true, Message = "已整理并加入知识库" };
            }
        }

        private static JObject ParseObject(string text)
        {
            text = (text ?? string.Empty).Trim();
            var start = text.IndexOf('{'); var end = text.LastIndexOf('}');
            if (start < 0 || end <= start) throw new Exception("未找到JSON对象");
            return JObject.Parse(text.Substring(start, end - start + 1));
        }

        private static bool CanLearn(string question, string answer)
        {
            return !string.IsNullOrWhiteSpace(question) && !string.IsNullOrWhiteSpace(answer) && !answer.StartsWith("错误：", StringComparison.Ordinal) && answer.IndexOf("已跳过", StringComparison.Ordinal) < 0;
        }

        private static string RedactSensitive(string value)
        {
            value = value ?? string.Empty;
            value = Regex.Replace(value, @"(?<!\d)1\d{10}(?!\d)", "[手机号]");
            value = Regex.Replace(value, @"(?<!\d)\d{15,19}(?!\d)", "[敏感编号]");
            value = Regex.Replace(value, @"(?i)sk-[a-z0-9_-]{12,}", "[API_KEY]");
            return value;
        }

        private static string Normalize(string value) { return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", string.Empty); }
        private static string AnswerKey(string seller, string buyer, string question, string answer) { return Normalize(seller) + "|" + Normalize(buyer) + "|" + KnowledgeAiService.NormalizeQuestion(question) + "|" + Normalize(answer); }
        private static string SendKey(string seller, string buyer, string answer) { return Normalize(seller) + "|" + Normalize(buyer) + "|" + Normalize(answer); }
        private static void Cleanup()
        {
            var now = DateTime.Now;
            foreach (var x in Sources.Where(x => x.Value.ExpiresAt < now).Select(x => x.Key).ToList()) { SourceStamp ignored; Sources.TryRemove(x, out ignored); }
            foreach (var x in Blocks.Where(x => x.Value.ExpiresAt < now).Select(x => x.Key).ToList()) { BlockStamp ignored; Blocks.TryRemove(x, out ignored); }
            foreach (var x in ManualBypass.Where(x => x.Value < now).Select(x => x.Key).ToList()) { DateTime ignored; ManualBypass.TryRemove(x, out ignored); }
        }
        private static void RaiseKnowledgeChanged() { var handler = KnowledgeBaseChanged; if (handler != null) handler(null, EventArgs.Empty); }
    }
}
'''

write("src/Bot/AssistWindow/Widget/Robot/CtlConversation.xaml.cs", ctl_conversation_cs)
write("src/Bot/AssistWindow/Widget/Robot/CtlConversation.xaml", ctl_conversation_xaml)
write("src/Bot/AssistWindow/Widget/Robot/ConversationEditWindow.cs", conversation_edit_window)
write("src/Bot/ChromeNs/KnowledgeLearningService.cs", knowledge_learning_service)

replace_once(
    "src/Bot/AssistWindow/Widget/Robot/CtlRobot.xaml.cs",
    "            ctlConversation.ResendRequested += CtlConversation_ResendRequested;\n",
    "            ctlConversation.ResendRequested += CtlConversation_ResendRequested;\n            ctlConversation.EditRequested += CtlConversation_EditRequested;\n")

old_resend = r'''        private async void CtlConversation_ResendRequested(object sender, ConversationResendEventArgs e)
        {
            var ctl = sender as CtlConversation;
            if (ctl == null) return;
            if (string.IsNullOrWhiteSpace(e.Answer))
            {
                ctl.SetSendResult(false, "重发失败：答案为空");
                return;
            }

            try
            {
                ctl.SetSendPending("手动重发中...");
                var qn = QN.CurQN;
                if (qn == null)
                {
                    ctl.SetSendResult(false, "重发失败：未识别千牛会话");
                    return;
                }

                var ok = await qn.SendTextWithRetryAsync(e.Buyer, e.Answer, 1);
                ctl.SetSendResult(ok, ok ? "重发成功" : "重发失败，已重试1次");
            }
            catch (Exception ex)
            {
                ctl.SetSendResult(false, "重发异常");
                Log.Exception(ex);
            }
        }
'''
new_resend = r'''        private async void CtlConversation_ResendRequested(object sender, ConversationResendEventArgs e)
        {
            var ctl = sender as CtlConversation;
            if (ctl == null) return;
            if (string.IsNullOrWhiteSpace(e.Answer))
            {
                ctl.SetSendResult(false, "重发失败：答案为空");
                return;
            }

            try
            {
                ctl.SetSendPending("手动重发中...");
                var qn = QN.CurQN;
                if (qn == null)
                {
                    ctl.SetSendResult(false, "重发失败：未识别千牛会话");
                    return;
                }
                KnowledgeLearningService.AllowNextManualSend(e.Seller, e.Buyer, e.Answer);
                var ok = await qn.SendTextWithRetryAsync(e.Buyer, e.Answer, 1);
                ctl.SetSendResult(ok, ok ? "重发成功" : "重发失败，已重试1次");
            }
            catch (Exception ex)
            {
                ctl.SetSendResult(false, "重发异常");
                Log.Exception(ex);
            }
        }

        private async void CtlConversation_EditRequested(object sender, ConversationEditEventArgs e)
        {
            var ctl = sender as CtlConversation;
            if (ctl == null) return;
            var wnd = new ConversationEditWindow(e.Question, e.Answer) { Owner = Window.GetWindow(this) };
            if (wnd.ShowDialog() != true) return;
            ctl.SetAnswer(wnd.EditedAnswer);
            ctl.SetSource("人工修改");
            ctl.SetSendPending("正在整理并写入知识库...");
            try
            {
                var result = await KnowledgeLearningService.LearnAsync(e.Question, wnd.EditedAnswer, "人工修改", e.Seller, e.Buyer);
                ctl.SetStatus(result.Success ? result.Message : "答案已修改，但知识库整理失败：" + result.Message, result.Success);
            }
            catch (Exception ex)
            {
                ctl.SetStatus("答案已修改，但知识库整理异常", false);
                Log.Exception(ex);
            }
        }
'''
replace_once("src/Bot/AssistWindow/Widget/Robot/CtlRobot.xaml.cs", old_resend, new_resend)

replace_once(
    "src/Bot/ChromeNs/QNRpa.cs",
    '''        public async Task<bool> SendTextAsync(string buyer, string text)\n        {\n            return await OpenAndSendText(buyer, text);\n        }\n''',
    '''        public async Task<bool> SendTextAsync(string buyer, string text)\n        {\n            await Task.Delay(180);\n            string manualQuestion;\n            string manualAnswer;\n            if (KnowledgeLearningService.TryBlockForManualReply(_qn, buyer, text, out manualQuestion, out manualAnswer)) return false;\n            return await OpenAndSendText(buyer, text);\n        }\n''')

replace_once(
    "src/Bot/ChromeNs/MyOpenAI.cs",
    '''                    Log.Info("商品链接使用本地预设回复，未调用AI接口。buyer=" + buyer);\n                    return presetReply;\n''',
    '''                    Log.Info("商品链接使用本地预设回复，未调用AI接口。buyer=" + buyer);\n                    KnowledgeLearningService.RegisterAnswerSource(seller, buyer, question, presetReply, "本地");\n                    return presetReply;\n''')

replace_once(
    "src/Bot/ChromeNs/MyOpenAI.cs",
    '''                if (string.IsNullOrWhiteSpace(question)) return "错误：买家消息为空，未调用AI。";\n\n                string manualAnswer;\n''',
    '''                if (string.IsNullOrWhiteSpace(question)) return "错误：买家消息为空，未调用AI。";\n\n                KnowledgeBaseEntry localKnowledge;\n                double localScore;\n                if (KnowledgeLearningService.TryFindLocalAnswer(seller, buyer, question, out localKnowledge, out localScore))\n                {\n                    KnowledgeLearningService.RegisterAnswerSource(seller, buyer, question, localKnowledge.Answer, "本地");\n                    Log.Info("命中本地知识库，未调用AI。buyer=" + buyer + ", knowledgeId=" + localKnowledge.Id + ", score=" + localScore.ToString("0.00"));\n                    return BotFeatureStore.ApplyOutputPolicy(localKnowledge.Answer);\n                }\n\n                string manualAnswer;\n''')

replace_once(
    "src/Bot/ChromeNs/MyOpenAI.cs",
    '''                        if (ConversationContextStore.IsWithdrawnAnswer(seller, buyer, finalAnswer))\n                        {\n                            return "错误：该回复已被客服撤回，已阻止再次发送。";\n                        }\n                        return finalAnswer;\n''',
    '''                        if (ConversationContextStore.IsWithdrawnAnswer(seller, buyer, finalAnswer))\n                        {\n                            return "错误：该回复已被客服撤回，已阻止再次发送。";\n                        }\n                        KnowledgeLearningService.RegisterAnswerSource(seller, buyer, question, finalAnswer, "AI生成");\n                        KnowledgeLearningService.QueueLearn(question, finalAnswer, "AI生成", seller, buyer);\n                        return finalAnswer;\n''')

insert_marker = '''        public static string BuildTimelineText(string seller, string buyer, string currentQuestion, int maxTurns)\n'''
insert_method = r'''        public static bool TryGetLatestBuyerQuestion(string seller, string buyer, out string question, out DateTime timestamp)
        {
            question = string.Empty;
            timestamp = DateTime.MinValue;
            TimelineState state;
            if (!States.TryGetValue(Key(seller, buyer), out state)) return false;
            lock (state.Sync)
            {
                Cleanup(state, DateTime.Now);
                var latest = state.Turns
                    .Where(t => t != null && t.Role == "user" && !t.Withdrawn && !string.IsNullOrWhiteSpace(t.Text))
                    .OrderByDescending(t => t.Timestamp)
                    .FirstOrDefault();
                if (latest == null) return false;
                question = latest.Text;
                timestamp = latest.Timestamp == DateTime.MinValue ? DateTime.Now.AddMinutes(-1) : latest.Timestamp;
                return true;
            }
        }

'''
replace_once("src/Bot/ChromeNs/ConversationContextStore.cs", insert_marker, insert_method + insert_marker)

replace_once(
    "src/Bot/ChromeNs/VisionRequestService.cs",
    '''                            return result;\n''',
    '''                            KnowledgeLearningService.RegisterAnswerSource(task.SellerNick, task.BuyerNick, "[图片]", result.Answer, "AI生成");\n                            KnowledgeLearningService.QueueLearn("买家发送图片。" + (string.IsNullOrWhiteSpace(timeline) ? string.Empty : "\\n" + timeline), result.Answer, "视觉AI", task.SellerNick, task.BuyerNick);\n                            return result;\n''')

knowledge_manager = read("src/Bot/Knowledge/KnowledgeManagerControl.cs")
knowledge_manager = knowledge_manager.replace(
    '        public KnowledgeManagerControl(){Build();RefreshData();}',
    '        public KnowledgeManagerControl(){Build();RefreshData();Loaded+=(s,e)=>KnowledgeLearningService.KnowledgeBaseChanged+=OnKnowledgeBaseChanged;Unloaded+=(s,e)=>KnowledgeLearningService.KnowledgeBaseChanged-=OnKnowledgeBaseChanged;}')
knowledge_manager = knowledge_manager.replace(
    '        private void ExportJson(){try{var dlg=new SaveFileDialog{Title="导出知识库",FileName="qianniu-knowledge-base.json",Filter="JSON文件 (*.json)|*.json|所有文件 (*.*)|*.*"};if(dlg.ShowDialog()!=true)return;File.WriteAllText(dlg.FileName,JsonConvert.SerializeObject(_all,Formatting.Indented),System.Text.Encoding.UTF8);}catch(Exception ex){MessageBox.Show("导出失败："+ex.Message);}}\n',
    '        private void ExportJson(){try{var dlg=new SaveFileDialog{Title="导出知识库",FileName="qianniu-knowledge-base.json",Filter="JSON文件 (*.json)|*.json|所有文件 (*.*)|*.*"};if(dlg.ShowDialog()!=true)return;File.WriteAllText(dlg.FileName,JsonConvert.SerializeObject(_all,Formatting.Indented),System.Text.Encoding.UTF8);}catch(Exception ex){MessageBox.Show("导出失败："+ex.Message);}}\n        private void OnKnowledgeBaseChanged(object sender,EventArgs e){if(Dispatcher.CheckAccess())RefreshData();else Dispatcher.BeginInvoke(new Action(RefreshData));}\n')
write("src/Bot/Knowledge/KnowledgeManagerControl.cs", knowledge_manager)

replace_once(
    "src/Bot/Bot.csproj",
    '    <Compile Include="AssistWindow\\Widget\\Robot\\CtlConversation.xaml.cs"><DependentUpon>CtlConversation.xaml</DependentUpon></Compile>\n',
    '    <Compile Include="AssistWindow\\Widget\\Robot\\CtlConversation.xaml.cs"><DependentUpon>CtlConversation.xaml</DependentUpon></Compile>\n    <Compile Include="AssistWindow\\Widget\\Robot\\ConversationEditWindow.cs" />\n')
replace_once(
    "src/Bot/Bot.csproj",
    '    <Compile Include="ChromeNs\\MyOpenAI.cs" />\n',
    '    <Compile Include="ChromeNs\\MyOpenAI.cs" />\n    <Compile Include="ChromeNs\\KnowledgeLearningService.cs" />\n')

static_test = r'''from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_message_menu_has_resend_and_edit():
    source = text("src/Bot/AssistWindow/Widget/Robot/CtlConversation.xaml.cs")
    assert 'Header = "重发"' in source
    assert 'Header = "修改"' in source
    assert "EditRequested" in source


def test_local_first_and_source_labels():
    source = text("src/Bot/ChromeNs/MyOpenAI.cs")
    assert "TryFindLocalAnswer" in source
    assert '"本地"' in source
    assert '"AI生成"' in source


def test_manual_reply_guard_and_learning():
    rpa = text("src/Bot/ChromeNs/QNRpa.cs")
    service = text("src/Bot/ChromeNs/KnowledgeLearningService.cs")
    assert "TryBlockForManualReply" in rpa
    assert "客服已人工回复" in service
    assert "人工回复" in service
    assert "AllowNextManualSend" in service


def test_learning_dedup_and_sensitive_redaction():
    source = text("src/Bot/ChromeNs/KnowledgeLearningService.cs")
    assert "ContentHash" in source
    assert "人工确认答案更新知识库" in source
    assert "[手机号]" in source
    assert "[API_KEY]" in source


def test_knowledge_manager_refreshes_after_learning():
    source = text("src/Bot/Knowledge/KnowledgeManagerControl.cs")
    assert "KnowledgeBaseChanged" in source
    assert "OnKnowledgeBaseChanged" in source
'''
write("tests/test_knowledge_learning_static.py", static_test)

# Remove one-shot patch infrastructure from the final tree.
(ROOT / "tools/apply_knowledge_learning_patch.py").unlink(missing_ok=True)
(ROOT / ".github/workflows/apply-knowledge-learning.yml").unlink(missing_ok=True)
print("KNOWLEDGE_LEARNING_PATCH=PASS")
