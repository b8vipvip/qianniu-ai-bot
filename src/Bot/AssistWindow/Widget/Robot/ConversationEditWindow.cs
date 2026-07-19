using System;
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
