using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Bot.Knowledge
{
    internal static class KnowledgeV2GovernanceUiBridge
    {
        private static readonly ConditionalWeakTable<KnowledgeCenterWindow, object> Installed =
            new ConditionalWeakTable<KnowledgeCenterWindow, object>();
        private static int _initialized;

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            EventManager.RegisterClassHandler(typeof(KnowledgeCenterWindow), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnLoaded), true);
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            var window = sender as KnowledgeCenterWindow;
            if (window == null) return;
            object marker;
            if (Installed.TryGetValue(window, out marker)) return;
            try { Installed.Add(window, new object()); } catch { return; }
            window.Dispatcher.BeginInvoke(new Action(() => InjectButton(window)));
        }

        private static void InjectButton(KnowledgeCenterWindow window)
        {
            try
            {
                var toolbar = FindHeaderToolbar(window.Content as DependencyObject);
                if (toolbar == null) return;
                if (toolbar.Children.OfType<Button>().Any(x => string.Equals(Convert.ToString(x.Content), "治理", StringComparison.Ordinal))) return;
                var button = new Button
                {
                    Content = "治理",
                    Width = 74,
                    Height = 30,
                    Margin = new Thickness(0, 0, 8, 0),
                    ToolTip = "统一处理低质量、冲突、待修订、长期未验证知识，并评估修订前后真实效果"
                };
                button.Click += delegate
                {
                    var seller = KnowledgeCenterV2Context.ResolveSeller(window);
                    new KnowledgeV2GovernanceWindow(window, seller).ShowDialog();
                };
                toolbar.Children.Insert(Math.Max(0, toolbar.Children.Count - 1), button);
            }
            catch { }
        }

        private static WrapPanel FindHeaderToolbar(DependencyObject root)
        {
            if (root == null) return null;
            var wrap = root as WrapPanel;
            if (wrap != null)
            {
                var labels = wrap.Children.OfType<Button>().Select(x => Convert.ToString(x.Content)).ToList();
                if (labels.Contains("刷新") && labels.Contains("测试台") && labels.Contains("导入导出")) return wrap;
            }
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var found = FindHeaderToolbar(VisualTreeHelper.GetChild(root, i));
                if (found != null) return found;
            }
            return null;
        }
    }

    internal sealed class KnowledgeV2GovernanceWindow : Window
    {
        private sealed class GovernanceLoadResult
        {
            public List<KnowledgeV2GovernanceIssue> Issues;
            public List<KnowledgeV2RevisionImpactItem> Impacts;
            public List<KnowledgeV2GovernanceAuditEntry> Audits;
            public KnowledgeV2GovernanceSettings Settings;
        }

        private readonly string _seller;
        private readonly ObservableCollection<KnowledgeV2GovernanceIssue> _issueView =
            new ObservableCollection<KnowledgeV2GovernanceIssue>();
        private readonly ObservableCollection<KnowledgeV2RevisionImpactItem> _impactView =
            new ObservableCollection<KnowledgeV2RevisionImpactItem>();
        private readonly ObservableCollection<KnowledgeV2GovernanceAuditEntry> _auditView =
            new ObservableCollection<KnowledgeV2GovernanceAuditEntry>();
        private List<KnowledgeV2GovernanceIssue> _allIssues = new List<KnowledgeV2GovernanceIssue>();
        private List<KnowledgeV2RevisionImpactItem> _allImpacts = new List<KnowledgeV2RevisionImpactItem>();
        private List<KnowledgeV2GovernanceAuditEntry> _allAudits = new List<KnowledgeV2GovernanceAuditEntry>();
        private DataGrid _issueGrid;
        private DataGrid _impactGrid;
        private DataGrid _auditGrid;
        private TextBox _issueDetails;
        private TextBox _impactDetails;
        private TextBox _auditDetails;
        private TextBox _search;
        private TextBox _auditSearch;
        private ComboBox _filter;
        private ComboBox _auditFilter;
        private TextBlock _issueSummary;
        private TextBlock _impactSummary;
        private TextBlock _auditSummary;
        private TextBlock _settingsStatus;
        private TextBox _normalVerificationDays;
        private TextBox _highRiskVerificationDays;
        private TextBox _unusedStaleDays;
        private Button _verify;
        private Button _disable;
        private Button _rollback;
        private Button _refresh;

        public KnowledgeV2GovernanceWindow(Window owner, string seller)
        {
            Owner = owner;
            _seller = seller ?? string.Empty;
            Title = "Knowledge Center V2 - 自适应知识治理";
            Width = 1320;
            Height = 840;
            MinWidth = 1080;
            MinHeight = 680;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Build();
            Loaded += delegate { RefreshAll(); };
        }

        private void Build()
        {
            var root = new Grid { Margin = new Thickness(14) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var title = new StackPanel();
            title.Children.Add(new TextBlock
            {
                Text = "Knowledge V2 自适应知识治理",
                FontSize = 22,
                FontWeight = FontWeights.SemiBold
            });
            title.Children.Add(new TextBlock
            {
                Text = "把低质量、事实冲突、待复核修订、长期未验证/未使用知识统一放进治理队列；修订效果按真实发送与纠错证据比较。任何停用、确认或回滚都必须人工触发。",
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 5, 0, 10),
                TextWrapping = TextWrapping.Wrap
            });
            root.Children.Add(title);

            var tabs = new TabControl();
            tabs.Items.Add(new TabItem { Header = "治理队列", Content = BuildGovernanceTab() });
            tabs.Items.Add(new TabItem { Header = "修订效果", Content = BuildImpactTab() });
            tabs.Items.Add(new TabItem { Header = "治理历史", Content = BuildAuditTab() });
            tabs.Items.Add(new TabItem { Header = "治理设置", Content = BuildSettingsTab() });
            Grid.SetRow(tabs, 1);
            root.Children.Add(tabs);
            Content = root;
        }

        private UIElement BuildGovernanceTab()
        {
            var root = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(190) });

            var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            _refresh = Btn("刷新治理", 86);
            _refresh.Click += delegate { RefreshAll(); };
            var generate = Btn("生成修订候选", 112);
            generate.Click += async delegate { await GenerateRevisionCandidatesAsync(); };
            var openRevision = Btn("打开修订", 86);
            openRevision.Click += delegate
            {
                var win = new KnowledgeV2RevisionWindow(this, _seller);
                win.ShowDialog();
                RefreshAll();
            };
            _verify = Btn("确认仍有效", 94);
            _verify.IsEnabled = false;
            _verify.Click += delegate { MarkSelectedVerified(); };
            _disable = Btn("停用所选", 86);
            _disable.IsEnabled = false;
            _disable.Click += delegate { DisableSelected(); };
            _filter = new ComboBox { Width = 128, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
            foreach (var value in new[] { "全部", "紧急/高", "修订效果退化", "知识冲突", "低质量", "待复核修订", "验证已过期", "长期未使用" })
                _filter.Items.Add(value);
            _filter.SelectedIndex = 0;
            _filter.SelectionChanged += delegate { ApplyIssueFilter(); };
            _search = new TextBox
            {
                Width = 220,
                Height = 30,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = "搜索知识标题、问题类型、证据或建议"
            };
            _search.TextChanged += delegate { ApplyIssueFilter(); };
            _issueSummary = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(8, 6, 0, 0)
            };
            toolbar.Children.Add(_refresh);
            toolbar.Children.Add(generate);
            toolbar.Children.Add(openRevision);
            toolbar.Children.Add(_verify);
            toolbar.Children.Add(_disable);
            toolbar.Children.Add(_filter);
            toolbar.Children.Add(_search);
            toolbar.Children.Add(_issueSummary);
            root.Children.Add(toolbar);

            _issueGrid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                ItemsSource = _issueView,
                SelectionMode = DataGridSelectionMode.Single
            };
            _issueGrid.Columns.Add(Col("优先级", "SeverityText", 68));
            _issueGrid.Columns.Add(Col("治理项", "IssueTypeText", 112));
            _issueGrid.Columns.Add(Col("知识", "KnowledgeTitle", 260));
            _issueGrid.Columns.Add(Col("类型", "KnowledgeType", 105));
            _issueGrid.Columns.Add(Col("命中", "UseCount", 58));
            _issueGrid.Columns.Add(Col("质量", "QualityText", 62));
            _issueGrid.Columns.Add(Col("最近验证", "LastVerifiedAtText", 92));
            _issueGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "证据",
                Binding = new Binding("Evidence"),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star)
            });
            _issueGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "建议",
                Binding = new Binding("Recommendation"),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star)
            });
            _issueGrid.SelectionChanged += delegate { LoadIssueDetails(_issueGrid.SelectedItem as KnowledgeV2GovernanceIssue); };
            Grid.SetRow(_issueGrid, 1);
            root.Children.Add(_issueGrid);

            _issueDetails = ReadOnlyDetails();
            Grid.SetRow(_issueDetails, 2);
            root.Children.Add(_issueDetails);
            return root;
        }

        private UIElement BuildImpactTab()
        {
            var root = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(230) });

            var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            var refresh = Btn("刷新效果", 86);
            refresh.Click += delegate { RefreshAll(); };
            _rollback = Btn("回滚所选修订", 112);
            _rollback.IsEnabled = false;
            _rollback.Click += delegate { RollbackSelected(); };
            _impactSummary = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(8, 6, 0, 0)
            };
            toolbar.Children.Add(refresh);
            toolbar.Children.Add(_rollback);
            toolbar.Children.Add(_impactSummary);
            root.Children.Add(toolbar);

            _impactGrid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                ItemsSource = _impactView,
                SelectionMode = DataGridSelectionMode.Single
            };
            _impactGrid.Columns.Add(Col("知识", "KnowledgeTitle", 270));
            _impactGrid.Columns.Add(Col("应用时间", "AppliedAtText", 112));
            _impactGrid.Columns.Add(Col("修订前样本", "BeforeSampleText", 105));
            _impactGrid.Columns.Add(Col("修订前负向率", "BeforeNegativeRateText", 92));
            _impactGrid.Columns.Add(Col("修订后样本", "AfterSampleText", 105));
            _impactGrid.Columns.Add(Col("修订后负向率", "AfterNegativeRateText", 92));
            _impactGrid.Columns.Add(Col("状态", "Status", 88));
            _impactGrid.Columns.Add(Col("回滚", "RollbackText", 82));
            _impactGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "评估建议",
                Binding = new Binding("Recommendation"),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star)
            });
            _impactGrid.SelectionChanged += delegate { LoadImpactDetails(_impactGrid.SelectedItem as KnowledgeV2RevisionImpactItem); };
            Grid.SetRow(_impactGrid, 1);
            root.Children.Add(_impactGrid);

            _impactDetails = ReadOnlyDetails();
            Grid.SetRow(_impactDetails, 2);
            root.Children.Add(_impactDetails);
            return root;
        }

        private UIElement BuildAuditTab()
        {
            var root = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(210) });

            var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            var refresh = Btn("刷新历史", 86);
            refresh.Click += delegate { RefreshAll(); };
            _auditFilter = new ComboBox { Width = 132, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
            foreach (var value in new[] { "全部", "确认仍有效", "停用知识", "应用修订", "驳回修订", "回滚修订", "生成修订候选", "更新治理设置" })
                _auditFilter.Items.Add(value);
            _auditFilter.SelectedIndex = 0;
            _auditFilter.SelectionChanged += delegate { ApplyAuditFilter(); };
            _auditSearch = new TextBox
            {
                Width = 240,
                Height = 30,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = "搜索操作、知识标题、摘要或目标ID"
            };
            _auditSearch.TextChanged += delegate { ApplyAuditFilter(); };
            _auditSummary = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(8, 6, 0, 0)
            };
            toolbar.Children.Add(refresh);
            toolbar.Children.Add(_auditFilter);
            toolbar.Children.Add(_auditSearch);
            toolbar.Children.Add(_auditSummary);
            root.Children.Add(toolbar);

            _auditGrid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                ItemsSource = _auditView,
                SelectionMode = DataGridSelectionMode.Single
            };
            _auditGrid.Columns.Add(Col("时间", "CreatedAtText", 150));
            _auditGrid.Columns.Add(Col("操作", "ActionText", 112));
            _auditGrid.Columns.Add(Col("目标类型", "TargetType", 105));
            _auditGrid.Columns.Add(Col("知识/对象", "TargetTitle", 260));
            _auditGrid.Columns.Add(Col("结果", "ResultText", 68));
            _auditGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "摘要",
                Binding = new Binding("Summary"),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star)
            });
            _auditGrid.SelectionChanged += delegate { LoadAuditDetails(_auditGrid.SelectedItem as KnowledgeV2GovernanceAuditEntry); };
            Grid.SetRow(_auditGrid, 1);
            root.Children.Add(_auditGrid);

            _auditDetails = ReadOnlyDetails();
            Grid.SetRow(_auditDetails, 2);
            root.Children.Add(_auditDetails);
            return root;
        }

        private UIElement BuildSettingsTab()
        {
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 10, 0, 0)
            };
            var panel = new StackPanel { MaxWidth = 820, HorizontalAlignment = HorizontalAlignment.Left };
            panel.Children.Add(new TextBlock
            {
                Text = "治理阈值（当前店铺独立）",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            });
            panel.Children.Add(new TextBlock
            {
                Text = "这些设置只决定知识何时进入人工治理队列，不会自动修改、停用或删除知识，也不会放宽修订回滚的安全门槛。",
                Foreground = Brushes.DimGray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 18)
            });

            _normalVerificationDays = SettingTextBox();
            _highRiskVerificationDays = SettingTextBox();
            _unusedStaleDays = SettingTextBox();
            panel.Children.Add(SettingsRow("普通知识验证过期", _normalVerificationDays,
                "30–730 天；默认 180 天。超过该时间未人工确认，会进入“验证已过期”。"));
            panel.Children.Add(SettingsRow("高风险知识验证过期", _highRiskVerificationDays,
                "7–365 天，且不能大于普通知识阈值；默认 60 天。"));
            panel.Children.Add(SettingsRow("长期未使用提醒", _unusedStaleDays,
                "30–730 天；默认 120 天。仅在没有有效使用记录时提示，不会自动删除。"));

            var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            var save = Btn("保存设置", 94);
            save.Click += delegate { SaveGovernanceSettings(); };
            var defaults = Btn("填入默认值", 104);
            defaults.Click += delegate
            {
                LoadSettingsValues(new KnowledgeV2GovernanceSettings
                {
                    NormalVerificationDays = KnowledgeEngineV2GovernanceAuditService.DefaultNormalVerificationDays,
                    HighRiskVerificationDays = KnowledgeEngineV2GovernanceAuditService.DefaultHighRiskVerificationDays,
                    UnusedStaleDays = KnowledgeEngineV2GovernanceAuditService.DefaultUnusedStaleDays
                });
                if (_settingsStatus != null) _settingsStatus.Text = "已填入默认值；点击“保存设置”后才会生效。";
            };
            _settingsStatus = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(8, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            buttons.Children.Add(save);
            buttons.Children.Add(defaults);
            buttons.Children.Add(_settingsStatus);
            panel.Children.Add(buttons);

            panel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(248, 250, 253)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(220, 226, 234)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 22, 0, 0),
                Child = new TextBlock
                {
                    Text = "保存后会立即重新扫描当前店铺治理队列，并在“治理历史”中追加一条设置变更审计。其他店铺保持自己的阈值和审计记录。",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.DimGray
                }
            });
            scroll.Content = panel;
            return scroll;
        }

        private void RefreshAll()
        {
            if (string.IsNullOrWhiteSpace(_seller))
            {
                if (_issueSummary != null) _issueSummary.Text = "未识别当前店铺";
                return;
            }
            if (_refresh != null) _refresh.IsEnabled = false;
            if (_issueSummary != null) _issueSummary.Text = "正在扫描治理队列...";
            if (_impactSummary != null) _impactSummary.Text = "正在计算修订前后效果...";
            if (_auditSummary != null) _auditSummary.Text = "正在读取本店治理历史...";
            if (_settingsStatus != null) _settingsStatus.Text = "正在读取本店治理设置...";
            Task.Run(() => new GovernanceLoadResult
            {
                Issues = KnowledgeEngineV2GovernanceService.Scan(_seller),
                Impacts = KnowledgeEngineV2GovernanceService.GetRevisionImpacts(_seller),
                Audits = KnowledgeEngineV2GovernanceAuditService.GetEntries(_seller, 500),
                Settings = KnowledgeEngineV2GovernanceAuditService.GetSettings(_seller)
            }).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_refresh != null) _refresh.IsEnabled = true;
                if (t.IsFaulted)
                {
                    var message = t.Exception.GetBaseException().Message;
                    if (_issueSummary != null) _issueSummary.Text = "治理扫描失败：" + message;
                    if (_impactSummary != null) _impactSummary.Text = "效果评估失败：" + message;
                    if (_auditSummary != null) _auditSummary.Text = "治理历史读取失败：" + message;
                    if (_settingsStatus != null) _settingsStatus.Text = "治理设置读取失败：" + message;
                    return;
                }
                _allIssues = t.Result.Issues ?? new List<KnowledgeV2GovernanceIssue>();
                _allImpacts = t.Result.Impacts ?? new List<KnowledgeV2RevisionImpactItem>();
                ApplyIssueFilter();
                _impactView.Clear();
                foreach (var item in _allImpacts) _impactView.Add(item);
                UpdateImpactSummary();
                _allAudits = t.Result.Audits ?? new List<KnowledgeV2GovernanceAuditEntry>();
                ApplyAuditFilter();
                LoadSettingsValues(t.Result.Settings);
                if (_settingsStatus != null) _settingsStatus.Text = "当前店铺设置已加载。";
            })));
        }

        private void ApplyIssueFilter()
        {
            var filter = _filter == null ? "全部" : Convert.ToString(_filter.SelectedItem) ?? "全部";
            var search = (_search == null ? string.Empty : _search.Text ?? string.Empty).Trim();
            IEnumerable<KnowledgeV2GovernanceIssue> items = _allIssues ?? new List<KnowledgeV2GovernanceIssue>();
            if (filter == "紧急/高") items = items.Where(x => x.Severity == "critical" || x.Severity == "high");
            else if (filter != "全部") items = items.Where(x => x.IssueTypeText == filter);
            if (search.Length > 0)
            {
                items = items.Where(x => Contains(x.KnowledgeTitle, search)
                    || Contains(x.IssueTypeText, search)
                    || Contains(x.Evidence, search)
                    || Contains(x.Recommendation, search));
            }
            var list = items.ToList();
            _issueView.Clear();
            foreach (var item in list) _issueView.Add(item);
            UpdateIssueSummary();
        }

        private void UpdateIssueSummary()
        {
            if (_issueSummary == null) return;
            var critical = _allIssues.Count(x => x.Severity == "critical");
            var high = _allIssues.Count(x => x.Severity == "high");
            var pending = _allIssues.Count(x => x.IssueType == "pending_revision" || x.IssueType == "multiple_pending_revision");
            var due = _allIssues.Count(x => x.IssueType == "verification_due");
            _issueSummary.Text = "治理项 " + _allIssues.Count + "｜紧急 " + critical + "｜高 " + high
                + "｜待修订 " + pending + "｜验证过期 " + due + "｜当前显示 " + _issueView.Count;
        }

        private void UpdateImpactSummary()
        {
            if (_impactSummary == null) return;
            var rollback = _allImpacts.Count(x => x.RollbackRecommended);
            var observing = _allImpacts.Count(x => x.Status == "观察中");
            var improved = _allImpacts.Count(x => x.Status == "效果改善");
            _impactSummary.Text = "已应用修订 " + _allImpacts.Count + "｜建议回滚 " + rollback
                + "｜观察中 " + observing + "｜效果改善 " + improved;
        }

        private void ApplyAuditFilter()
        {
            var filter = _auditFilter == null ? "全部" : Convert.ToString(_auditFilter.SelectedItem) ?? "全部";
            var search = (_auditSearch == null ? string.Empty : _auditSearch.Text ?? string.Empty).Trim();
            IEnumerable<KnowledgeV2GovernanceAuditEntry> items = _allAudits ?? new List<KnowledgeV2GovernanceAuditEntry>();
            if (filter != "全部") items = items.Where(x => string.Equals(x.ActionText, filter, StringComparison.Ordinal));
            if (search.Length > 0)
            {
                items = items.Where(x => Contains(x.ActionText, search)
                    || Contains(x.TargetTitle, search)
                    || Contains(x.Summary, search)
                    || Contains(x.KnowledgeId, search)
                    || Contains(x.CandidateId, search));
            }
            var list = items.ToList();
            _auditView.Clear();
            foreach (var item in list) _auditView.Add(item);
            UpdateAuditSummary();
        }

        private void UpdateAuditSummary()
        {
            if (_auditSummary == null) return;
            var knowledgeActions = _allAudits.Count(x => x.TargetType == "knowledge");
            var revisionActions = _allAudits.Count(x => x.TargetType == "revision");
            var settingActions = _allAudits.Count(x => x.ActionType == "update_settings");
            _auditSummary.Text = "历史 " + _allAudits.Count + "｜知识动作 " + knowledgeActions
                + "｜修订动作 " + revisionActions + "｜设置变更 " + settingActions + "｜当前显示 " + _auditView.Count;
        }

        private async Task GenerateRevisionCandidatesAsync()
        {
            if (string.IsNullOrWhiteSpace(_seller)) return;
            if (_issueSummary != null) _issueSummary.Text = "正在从真实人工纠正聚类生成修订候选...";
            try
            {
                var result = await Task.Run(() => KnowledgeEngineV2RevisionService.GenerateCandidates(_seller));
                MessageBox.Show(this,
                    "扫描知识 " + result.ScannedKnowledge + " 条；新增候选 " + result.Generated
                    + "；已有待复核 " + result.ExistingPending + "；证据不足 " + result.SkippedInsufficientEvidence + "。",
                    "修订候选生成完成", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshAll();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "生成修订候选失败：" + ex.Message, "知识治理", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MarkSelectedVerified()
        {
            var item = _issueGrid.SelectedItem as KnowledgeV2GovernanceIssue;
            if (item == null) return;
            if (MessageBox.Show(this,
                "请确认你已经人工核对该知识当前仍然有效。确认后会刷新 LastVerifiedAt，但不会修改答案。\n\n知识：" + item.KnowledgeTitle,
                "确认知识仍有效", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            string error;
            if (!KnowledgeEngineV2GovernanceService.MarkVerified(_seller, item.KnowledgeId, out error))
            {
                MessageBox.Show(this, error, "确认失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            RefreshAll();
        }

        private void DisableSelected()
        {
            var item = _issueGrid.SelectedItem as KnowledgeV2GovernanceIssue;
            if (item == null) return;
            if (MessageBox.Show(this,
                "停用后该知识将退出生产召回和本地直答，但不会删除审计记录。确定停用吗？\n\n知识：" + item.KnowledgeTitle,
                "停用知识", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            string error;
            if (!KnowledgeEngineV2GovernanceService.DisableKnowledge(_seller, item.KnowledgeId, out error))
            {
                MessageBox.Show(this, error, "停用失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            RefreshAll();
        }

        private void RollbackSelected()
        {
            var item = _impactGrid.SelectedItem as KnowledgeV2RevisionImpactItem;
            if (item == null || !item.CanRollback) return;
            var warning = item.RollbackRecommended
                ? "系统检测到修订后负向率明显退化，当前达到回滚建议门槛。"
                : "当前没有达到自动建议回滚门槛；如仍需回滚，请确认你已人工核对。";
            if (MessageBox.Show(this,
                warning + "\n\n回滚会把当前答案恢复为该修订审计记录中的原答案，并刷新知识索引。是否继续？",
                "回滚知识修订", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            string error;
            if (!KnowledgeEngineV2GovernanceService.RollbackRevision(_seller, item.CandidateId, out error))
            {
                MessageBox.Show(this, error, "回滚失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                RefreshAll();
                return;
            }
            MessageBox.Show(this, "已恢复修订前答案。系统保留原修订审计记录，并已刷新 Knowledge V2 索引。",
                "回滚完成", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshAll();
        }

        private void SaveGovernanceSettings()
        {
            int normal;
            int highRisk;
            int unused;
            if (!TryReadSetting(_normalVerificationDays, "普通知识验证过期", out normal)
                || !TryReadSetting(_highRiskVerificationDays, "高风险知识验证过期", out highRisk)
                || !TryReadSetting(_unusedStaleDays, "长期未使用提醒", out unused)) return;
            if (normal < KnowledgeEngineV2GovernanceAuditService.MinNormalVerificationDays
                || normal > KnowledgeEngineV2GovernanceAuditService.MaxNormalVerificationDays)
            {
                MessageBox.Show(this, "普通知识验证过期必须为 30–730 天。", "治理设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (highRisk < KnowledgeEngineV2GovernanceAuditService.MinHighRiskVerificationDays
                || highRisk > KnowledgeEngineV2GovernanceAuditService.MaxHighRiskVerificationDays
                || highRisk > normal)
            {
                MessageBox.Show(this, "高风险知识验证过期必须为 7–365 天，且不能大于普通知识阈值。",
                    "治理设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (unused < KnowledgeEngineV2GovernanceAuditService.MinUnusedStaleDays
                || unused > KnowledgeEngineV2GovernanceAuditService.MaxUnusedStaleDays)
            {
                MessageBox.Show(this, "长期未使用提醒必须为 30–730 天。", "治理设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (MessageBox.Show(this,
                "确定保存当前店铺的治理阈值吗？\n\n普通验证：" + normal + " 天\n高风险验证：" + highRisk
                + " 天\n长期未使用：" + unused + " 天\n\n保存后只会重新计算治理提示，不会自动修改生产知识。",
                "保存治理设置", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try
            {
                var saved = KnowledgeEngineV2GovernanceAuditService.SaveSettings(_seller, normal, highRisk, unused);
                LoadSettingsValues(saved);
                if (_settingsStatus != null) _settingsStatus.Text = "设置已保存，正在重新扫描治理队列...";
                RefreshAll();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存治理设置失败：" + ex.Message, "治理设置", MessageBoxButton.OK, MessageBoxImage.Error);
                if (_settingsStatus != null) _settingsStatus.Text = "保存失败：" + ex.Message;
            }
        }

        private void LoadSettingsValues(KnowledgeV2GovernanceSettings settings)
        {
            if (settings == null) return;
            if (_normalVerificationDays != null) _normalVerificationDays.Text = settings.NormalVerificationDays.ToString();
            if (_highRiskVerificationDays != null) _highRiskVerificationDays.Text = settings.HighRiskVerificationDays.ToString();
            if (_unusedStaleDays != null) _unusedStaleDays.Text = settings.UnusedStaleDays.ToString();
        }

        private void LoadIssueDetails(KnowledgeV2GovernanceIssue item)
        {
            if (_issueDetails == null) return;
            _verify.IsEnabled = item != null && item.IssueType == "verification_due";
            _disable.IsEnabled = item != null;
            if (item == null)
            {
                _issueDetails.Text = string.Empty;
                return;
            }
            _issueDetails.Text = "知识：" + item.KnowledgeTitle + Environment.NewLine
                + "优先级：" + item.SeverityText + "　治理项：" + item.IssueTypeText + "　风险：" + item.RiskLevel + Environment.NewLine
                + "类型：" + item.KnowledgeType + "　命中：" + item.UseCount + "　质量：" + item.QualityText
                + "　待修订：" + item.PendingRevisionCount + "　最近验证：" + item.LastVerifiedAtText + Environment.NewLine
                + Environment.NewLine + "【证据】" + Environment.NewLine + item.Evidence
                + Environment.NewLine + Environment.NewLine + "【治理建议】" + Environment.NewLine + item.Recommendation;
        }

        private void LoadImpactDetails(KnowledgeV2RevisionImpactItem item)
        {
            if (_impactDetails == null) return;
            _rollback.IsEnabled = item != null && item.CanRollback;
            if (item == null)
            {
                _impactDetails.Text = string.Empty;
                return;
            }
            _impactDetails.Text = "知识：" + item.KnowledgeTitle + Environment.NewLine
                + "应用时间：" + item.AppliedAtText + "　状态：" + item.Status + "　风险：" + item.RiskLevel + Environment.NewLine
                + "修订前30天：发送=" + item.BeforeSent + "，确认=" + item.BeforeAccepted + "，负向=" + item.BeforeNegative
                + "，负向率=" + item.BeforeNegativeRateText + Environment.NewLine
                + "修订后30天：发送=" + item.AfterSent + "，确认=" + item.AfterAccepted + "，负向=" + item.AfterNegative
                + "，负向率=" + item.AfterNegativeRateText + Environment.NewLine
                + "评估：" + item.Recommendation + Environment.NewLine
                + Environment.NewLine + "【修订前答案】" + Environment.NewLine + (item.OriginalAnswer ?? string.Empty)
                + Environment.NewLine + Environment.NewLine + "【修订后答案】" + Environment.NewLine + (item.ProposedAnswer ?? string.Empty)
                + Environment.NewLine + Environment.NewLine + "【当前知识答案】" + Environment.NewLine + (item.CurrentAnswer ?? string.Empty);
        }

        private void LoadAuditDetails(KnowledgeV2GovernanceAuditEntry item)
        {
            if (_auditDetails == null) return;
            if (item == null)
            {
                _auditDetails.Text = string.Empty;
                return;
            }
            _auditDetails.Text = "时间：" + item.CreatedAtText + "　操作：" + item.ActionText + "　结果：" + item.ResultText + Environment.NewLine
                + "店铺客服：" + (item.Seller ?? string.Empty) + "　目标类型：" + (item.TargetType ?? string.Empty) + Environment.NewLine
                + "对象：" + (item.TargetTitle ?? string.Empty) + Environment.NewLine
                + "Knowledge ID：" + (item.KnowledgeId ?? string.Empty) + "　Candidate ID：" + (item.CandidateId ?? string.Empty) + Environment.NewLine
                + Environment.NewLine + "【操作摘要】" + Environment.NewLine + (item.Summary ?? string.Empty)
                + Environment.NewLine + Environment.NewLine + "【操作前状态】" + Environment.NewLine + (item.BeforeState ?? string.Empty)
                + Environment.NewLine + Environment.NewLine + "【操作后状态】" + Environment.NewLine + (item.AfterState ?? string.Empty);
        }

        private static TextBox ReadOnlyDetails()
        {
            return new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 8, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(248, 250, 253))
            };
        }

        private static TextBox SettingTextBox()
        {
            return new TextBox
            {
                Width = 90,
                Height = 30,
                HorizontalContentAlignment = HorizontalAlignment.Right,
                VerticalContentAlignment = VerticalAlignment.Center
            };
        }

        private static UIElement SettingsRow(string title, TextBox input, string description)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var label = new TextBlock
            {
                Text = title,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold
            };
            row.Children.Add(label);
            Grid.SetColumn(input, 1);
            row.Children.Add(input);
            var help = new TextBlock
            {
                Text = description,
                Foreground = Brushes.DimGray,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(help, 2);
            row.Children.Add(help);
            return row;
        }

        private bool TryReadSetting(TextBox input, string label, out int value)
        {
            value = 0;
            if (input != null && int.TryParse((input.Text ?? string.Empty).Trim(), out value)) return true;
            MessageBox.Show(this, label + "必须填写整数天数。", "治理设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (input != null) input.Focus();
            return false;
        }

        private static DataGridTextColumn Col(string header, string path, double width)
        {
            return new DataGridTextColumn { Header = header, Binding = new Binding(path), Width = width };
        }

        private static Button Btn(string text, double width)
        {
            return new Button { Content = text, Width = width, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
        }

        private static bool Contains(string value, string query)
        {
            return (value ?? string.Empty).IndexOf(query ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
