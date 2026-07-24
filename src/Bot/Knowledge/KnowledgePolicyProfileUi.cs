using Bot.ChromeNs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Bot.Knowledge
{
    internal static class KnowledgePolicyProfileUi
    {
        private static int _initialized;

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            EventManager.RegisterClassHandler(
                typeof(KnowledgeManagerControl),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnManagerLoaded),
                true);
        }

        private static void OnManagerLoaded(object sender, RoutedEventArgs e)
        {
            var manager = sender as KnowledgeManagerControl;
            if (manager == null) return;
            var top = FindFirst<WrapPanel>(manager);
            if (top == null) return;
            if (top.Children.OfType<Button>().Any(x => Convert.ToString(x.Tag) == "knowledge-policy-profile")) return;

            var button = new Button
            {
                Content = "知识策略",
                Width = 86,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 6),
                Tag = "knowledge-policy-profile",
                ToolTip = "设置每条知识的回答模式、适用条件和必要上下文，并查看客服修正/撤回形成的可靠度。"
            };
            button.Click += (s, args) =>
            {
                var window = new KnowledgePolicyProfileWindow
                {
                    Owner = Window.GetWindow(manager)
                };
                window.ShowDialog();
            };
            top.Children.Add(button);
        }

        private static T FindFirst<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) return null;
            var direct = root as T;
            if (direct != null) return direct;
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var found = FindFirst<T>(VisualTreeHelper.GetChild(root, i));
                if (found != null) return found;
            }
            if (root is ContentControl)
            {
                var found = FindFirst<T>(((ContentControl)root).Content as DependencyObject);
                if (found != null) return found;
            }
            return null;
        }
    }

    internal sealed class KnowledgePolicyProfileWindow : Window
    {
        private readonly DataGrid _grid;
        private readonly ComboBox _mode;
        private readonly TextBox _intent;
        private readonly TextBox _entities;
        private readonly TextBox _applyWhen;
        private readonly TextBox _doNotApplyWhen;
        private readonly TextBox _requiredContext;
        private readonly TextBlock _stats;
        private readonly List<KnowledgeBaseEntry> _knowledge;
        private List<KnowledgePolicyProfile> _profiles;
        private bool _loading;

        public KnowledgePolicyProfileWindow()
        {
            Title = "知识策略与可靠度";
            Width = 1080;
            Height = 720;
            MinWidth = 880;
            MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            _knowledge = BotFeatureStore.GetKnowledgeBase() ?? new List<KnowledgeBaseEntry>();
            _profiles = KnowledgePolicyProfileService.GetProfilesForKnowledge(_knowledge);

            var root = new Grid { Margin = new Thickness(14) };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star) });
            Content = root;

            var left = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
            Grid.SetColumn(left, 0);
            root.Children.Add(left);
            var intro = new TextBlock
            {
                Text = "可靠度会根据人工客服对 Bot/知识答案的保留、修正和撤回行为自动变化。低可靠度知识会自动失去直接回复资格。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 0, 0, 8)
            };
            DockPanel.SetDock(intro, Dock.Top);
            left.Children.Add(intro);

            _grid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                ItemsSource = _profiles
            };
            _grid.Columns.Add(new DataGridTextColumn { Header = "问题", Binding = new Binding("QuestionSnapshot"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            _grid.Columns.Add(new DataGridTextColumn { Header = "模式", Binding = new Binding("AnswerModeDisplay"), Width = 110 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "可靠度", Binding = new Binding("ReliabilityDisplay"), Width = 75 });
            _grid.SelectionChanged += (s, e) => LoadSelected();
            left.Children.Add(_grid);

            var right = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetColumn(right, 1);
            root.Children.Add(right);
            var form = new StackPanel();
            right.Content = form;

            AddLabel(form, "回答模式");
            _mode = new ComboBox { Height = 30, Margin = new Thickness(0, 0, 0, 10) };
            _mode.Items.Add(new ComboBoxItem { Content = "自动判断", Tag = KnowledgeAnswerModes.Auto });
            _mode.Items.Add(new ComboBoxItem { Content = "优先直答", Tag = KnowledgeAnswerModes.Direct });
            _mode.Items.Add(new ComboBoxItem { Content = "必须结合上下文", Tag = KnowledgeAnswerModes.Contextual });
            _mode.Items.Add(new ComboBoxItem { Content = "仅作为事实约束", Tag = KnowledgeAnswerModes.Constraint });
            form.Children.Add(_mode);

            AddLabel(form, "意图（可选）");
            _intent = AddBox(form, false, 32, "例如 capability / how_to / troubleshoot；留空使用自动识别。");
            AddLabel(form, "业务实体（逗号分隔）");
            _entities = AddBox(form, false, 32, "例如 电视会员,手机端,酷狗；用于知识重排。");
            AddLabel(form, "适用条件");
            _applyWhen = AddBox(form, true, 74, "每行或分号分隔。满足时提高相关度；未满足时不会直接回复。例：当前商品为电视会员");
            AddLabel(form, "禁用条件");
            _doNotApplyWhen = AddBox(form, true, 74, "命中任一条件时，本条知识完全不参与当前回复。例：当前咨询手机端会员");
            AddLabel(form, "必要上下文");
            _requiredContext = AddBox(form, true, 74, "缺少时强制结合上下文，不允许固定直答。例：已确认购买电视端会员");

            _stats = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Background = Brushes.WhiteSmoke,
                Padding = new Thickness(10),
                Margin = new Thickness(0, 4, 0, 12)
            };
            form.Children.Add(_stats);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var save = new Button { Content = "保存策略", Width = 90, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
            save.Click += (s, e) => SaveSelected();
            buttons.Children.Add(save);
            var close = new Button { Content = "关闭", Width = 80, Height = 30 };
            close.Click += (s, e) => Close();
            buttons.Children.Add(close);
            form.Children.Add(buttons);

            if (_profiles.Count > 0) _grid.SelectedIndex = 0;
        }

        private static void AddLabel(Panel panel, string text)
        {
            panel.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 2, 0, 5) });
        }

        private static TextBox AddBox(Panel panel, bool multiline, double height, string tooltip)
        {
            var box = new TextBox
            {
                Height = height,
                AcceptsReturn = multiline,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden,
                ToolTip = tooltip,
                Margin = new Thickness(0, 0, 0, 10)
            };
            panel.Children.Add(box);
            return box;
        }

        private void LoadSelected()
        {
            if (_loading) return;
            var profile = _grid.SelectedItem as KnowledgePolicyProfile;
            if (profile == null) return;
            _loading = true;
            try
            {
                foreach (ComboBoxItem item in _mode.Items)
                {
                    if (string.Equals(Convert.ToString(item.Tag), KnowledgeAnswerModes.Normalize(profile.AnswerMode), StringComparison.Ordinal))
                    {
                        _mode.SelectedItem = item;
                        break;
                    }
                }
                if (_mode.SelectedItem == null) _mode.SelectedIndex = 0;
                _intent.Text = profile.Intent ?? string.Empty;
                _entities.Text = profile.Entities ?? string.Empty;
                _applyWhen.Text = profile.ApplyWhen ?? string.Empty;
                _doNotApplyWhen.Text = profile.DoNotApplyWhen ?? string.Empty;
                _requiredContext.Text = profile.RequiredContext ?? string.Empty;
                _stats.Text = "可靠度：" + profile.ReliabilityDisplay
                    + "\n直答选择：" + profile.DirectSelectedCount
                    + "；上下文使用：" + profile.ContextualSelectedCount
                    + "；人工确认：" + profile.AcceptedCount
                    + "；人工修正：" + profile.SellerCorrectionCount
                    + "；Bot撤回后修正：" + profile.SellerWithdrawCount
                    + "\n最近证据：" + (string.IsNullOrWhiteSpace(profile.LastEvidenceType) ? "暂无" : profile.LastEvidenceType)
                    + "；更新时间：" + (profile.UpdatedAt ?? string.Empty);
            }
            finally
            {
                _loading = false;
            }
        }

        private void SaveSelected()
        {
            var profile = _grid.SelectedItem as KnowledgePolicyProfile;
            if (profile == null) return;
            var entry = _knowledge.FirstOrDefault(x => x != null
                && string.Equals(x.Id ?? string.Empty, profile.KnowledgeId ?? string.Empty, StringComparison.Ordinal));
            if (entry == null)
            {
                entry = _knowledge.FirstOrDefault(x => x != null
                    && KnowledgeAiService.NormalizeQuestion(x.Title) == KnowledgeAiService.NormalizeQuestion(profile.QuestionSnapshot));
            }
            if (entry == null)
            {
                MessageBox.Show("未找到对应知识条目，请刷新知识库后重试。", "知识策略", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var selectedMode = _mode.SelectedItem as ComboBoxItem;
            profile.AnswerMode = selectedMode == null ? KnowledgeAnswerModes.Auto : Convert.ToString(selectedMode.Tag);
            profile.Intent = _intent.Text;
            profile.Entities = _entities.Text;
            profile.ApplyWhen = _applyWhen.Text;
            profile.DoNotApplyWhen = _doNotApplyWhen.Text;
            profile.RequiredContext = _requiredContext.Text;
            KnowledgePolicyProfileService.SaveProfile(entry, profile);

            var selectedId = profile.KnowledgeId;
            _profiles = KnowledgePolicyProfileService.GetProfilesForKnowledge(_knowledge);
            _grid.ItemsSource = _profiles;
            _grid.SelectedItem = _profiles.FirstOrDefault(x => x.KnowledgeId == selectedId);
            LoadSelected();
        }
    }
}
