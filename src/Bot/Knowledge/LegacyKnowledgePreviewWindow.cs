using Bot.ChromeNs;
using Bot.Options;
using Bot.ShopScope;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace Bot.Knowledge
{
    /// <summary>
    /// Read-only reconstruction of the legacy KnowledgeBaseJson list. This window intentionally
    /// does not host KnowledgeManagerControl/KnowledgeImportControl and never exposes a mutation
    /// path, so opening it cannot reactivate the legacy editor or reply runtime.
    /// </summary>
    internal sealed class LegacyKnowledgePreviewWindow : Window
    {
        private const string LegacyKnowledgeKey = "KnowledgeBaseJson";
        private static readonly ShopScopedPathProvider Paths = new ShopScopedPathProvider();
        private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(37, 99, 235));
        private static readonly Brush Heading = new SolidColorBrush(Color.FromRgb(31, 41, 55));
        private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(91, 103, 122));
        private static readonly Brush Panel = new SolidColorBrush(Color.FromRgb(248, 250, 253));
        private static readonly Brush Border = new SolidColorBrush(Color.FromRgb(220, 226, 234));
        private static readonly Brush WarningBackground = new SolidColorBrush(Color.FromRgb(255, 247, 237));
        private static readonly Brush WarningBorder = new SolidColorBrush(Color.FromRgb(251, 146, 60));
        private static readonly Brush WarningText = new SolidColorBrush(Color.FromRgb(154, 52, 18));

        private sealed class PreviewRow
        {
            public string Id { get; private set; }
            public string EnabledText { get; private set; }
            public string Category { get; private set; }
            public string Title { get; private set; }
            public string Keywords { get; private set; }
            public string Answer { get; private set; }
            public string UpdatedAt { get; private set; }
            public string CreatedAt { get; private set; }
            public string AiGeneratedText { get; private set; }
            public string SourceType { get; private set; }
            public string SearchText { get; private set; }

            public static PreviewRow From(KnowledgeBaseEntry source)
            {
                if (source == null) throw new ArgumentNullException(nameof(source));
                var row = new PreviewRow
                {
                    Id = Clean(source.Id),
                    EnabledText = source.Enabled ? "启用" : "停用",
                    Category = DefaultIfEmpty(source.Category, "未分类"),
                    Title = Clean(source.Title),
                    Keywords = Clean(source.Keywords),
                    Answer = Clean(source.Answer),
                    UpdatedAt = Clean(source.UpdatedAt),
                    CreatedAt = Clean(source.CreatedAt),
                    AiGeneratedText = source.AiGenerated ? "是" : "否",
                    SourceType = DefaultIfEmpty(source.SourceType, "未标注")
                };
                row.SearchText = string.Join("\n", new[]
                {
                    row.Id,
                    row.Category,
                    row.Title,
                    row.Keywords,
                    row.Answer,
                    row.SourceType,
                    row.UpdatedAt,
                    row.CreatedAt
                }).ToLowerInvariant();
                return row;
            }

            private static string Clean(string value)
            {
                return (value ?? string.Empty).Trim();
            }

            private static string DefaultIfEmpty(string value, string fallback)
            {
                value = Clean(value);
                return value.Length == 0 ? fallback : value;
            }
        }

        private readonly ShopContext _shop;
        private readonly ObservableCollection<PreviewRow> _visible =
            new ObservableCollection<PreviewRow>();
        private List<PreviewRow> _all = new List<PreviewRow>();
        private TextBox _search;
        private ComboBox _category;
        private TextBlock _summary;
        private DataGrid _grid;
        private TextBox _detail;
        private bool _updatingCategories;

        private LegacyKnowledgePreviewWindow(ShopContext shop)
        {
            if (shop == null) throw new ArgumentNullException(nameof(shop));
            _shop = shop;
            Title = "旧版知识库预览";
            Width = 1260;
            Height = 780;
            MinWidth = 980;
            MinHeight = 620;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Content = BuildLayout();
            PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key != Key.Escape) return;
                e.Handled = true;
                Close();
            };
            ReloadSnapshot(false);
        }

        internal static void MyShow(Window owner, string seller)
        {
            try
            {
                var shop = ResolveShop(owner, seller);
                var window = new LegacyKnowledgePreviewWindow(shop);
                ShopScopedUiBridge.Attach(window, shop);
                if (owner != null) window.Owner = owner;
                window.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    owner,
                    "无法打开旧版知识库预览：" + ex.Message,
                    "旧版知识库预览",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private static ShopContext ResolveShop(Window owner, string seller)
        {
            var attached = ShopScopedUiBridge.Get(owner);
            if (attached != null) return attached;

            var effectiveSeller = (seller ?? string.Empty).Trim();
            if (effectiveSeller.Length == 0)
            {
                var current = QN.CurQN;
                if (current != null && current.Seller != null)
                    effectiveSeller = (current.Seller.Nick ?? string.Empty).Trim();
            }
            if (effectiveSeller.Length == 0)
                throw new InvalidOperationException("未识别当前店铺。请从对应店铺的设置页面重新打开。");
            return ShopContextLocator.ResolveRuntimeBySellerNick(effectiveSeller);
        }

        private UIElement BuildLayout()
        {
            var root = new Grid { Background = Brushes.White };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = BuildHeader();
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            var warning = new Border
            {
                Background = WarningBackground,
                BorderBrush = WarningBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(16, 12, 16, 0),
                Padding = new Thickness(12, 9, 12, 9),
                Child = new TextBlock
                {
                    Text = "仅供预览参考：此窗口只读取当前店铺的旧版问答快照；不会保存、编辑、删除、导入、导出、AI 优化，也不会启用旧版知识库的检索、匹配或自动回复功能。",
                    Foreground = WarningText,
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = FontWeights.SemiBold
                }
            };
            Grid.SetRow(warning, 1);
            root.Children.Add(warning);

            var filters = BuildFilters();
            Grid.SetRow(filters, 2);
            root.Children.Add(filters);

            var body = BuildBody();
            Grid.SetRow(body, 3);
            root.Children.Add(body);

            var footer = BuildFooter();
            Grid.SetRow(footer, 4);
            root.Children.Add(footer);
            return root;
        }

        private UIElement BuildHeader()
        {
            var border = new Border
            {
                Background = Panel,
                BorderBrush = Border,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(20, 14, 20, 13)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new StackPanel();
            title.Children.Add(new TextBlock
            {
                Text = "旧版知识库预览",
                FontSize = 23,
                FontWeight = FontWeights.SemiBold,
                Foreground = Heading
            });
            title.Children.Add(new TextBlock
            {
                Text = "查看旧版问答、分类、关键词与来源，便于迁移核对和配置参考",
                Foreground = Muted,
                Margin = new Thickness(0, 4, 0, 0)
            });
            grid.Children.Add(title);

            var scope = new Border
            {
                Background = Brushes.White,
                BorderBrush = Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(12, 7, 12, 7),
                VerticalAlignment = VerticalAlignment.Center
            };
            scope.Child = new TextBlock
            {
                Text = "只读参考　当前店铺：" + DisplayShop(_shop),
                Foreground = Accent,
                FontWeight = FontWeights.SemiBold
            };
            Grid.SetColumn(scope, 1);
            grid.Children.Add(scope);
            border.Child = grid;
            return border;
        }

        private UIElement BuildFilters()
        {
            var panel = new DockPanel { Margin = new Thickness(16, 12, 16, 8) };
            _summary = new TextBlock
            {
                Foreground = Muted,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right
            };
            DockPanel.SetDock(_summary, Dock.Right);
            panel.Children.Add(_summary);

            var actions = new WrapPanel();
            _search = new TextBox
            {
                Width = 270,
                Height = 30,
                ToolTip = "搜索问题、答案、关键词、分类、来源或 ID",
                VerticalContentAlignment = VerticalAlignment.Center
            };
            _search.TextChanged += delegate { ApplyFilter(); };
            actions.Children.Add(_search);

            _category = new ComboBox
            {
                Width = 150,
                Height = 30,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            _category.SelectionChanged += delegate
            {
                if (!_updatingCategories) ApplyFilter();
            };
            actions.Children.Add(_category);

            var clear = MakeButton("清空筛选", 88);
            clear.Margin = new Thickness(8, 0, 0, 0);
            clear.Click += delegate
            {
                _search.Text = string.Empty;
                if (_category.Items.Count > 0) _category.SelectedIndex = 0;
                ApplyFilter();
            };
            actions.Children.Add(clear);

            var refresh = MakeButton("刷新预览", 88);
            refresh.Margin = new Thickness(8, 0, 0, 0);
            refresh.ToolTip = "重新读取当前店铺的旧版知识快照；不会保存任何数据";
            refresh.Click += delegate { ReloadSnapshot(true); };
            actions.Children.Add(refresh);
            panel.Children.Add(actions);
            return panel;
        }

        private UIElement BuildBody()
        {
            var body = new Grid { Margin = new Thickness(16, 0, 16, 8) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(7, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });

            _grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                IsReadOnly = true,
                SelectionMode = DataGridSelectionMode.Single,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                ItemsSource = _visible,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                BorderBrush = Border,
                BorderThickness = new Thickness(1)
            };
            _grid.SelectionChanged += delegate { ShowSelectedDetail(); };
            _grid.Columns.Add(TextColumn("状态", "EnabledText", 62));
            _grid.Columns.Add(TextColumn("分类", "Category", 105));
            _grid.Columns.Add(TextColumn("问题", "Title", 230));
            _grid.Columns.Add(TextColumn("答案", "Answer", new DataGridLength(1, DataGridLengthUnitType.Star)));
            _grid.Columns.Add(TextColumn("关键词", "Keywords", 145));
            _grid.Columns.Add(TextColumn("来源", "SourceType", 95));
            _grid.Columns.Add(TextColumn("更新时间", "UpdatedAt", 135));
            body.Children.Add(_grid);

            var detailBorder = new Border
            {
                Background = Panel,
                BorderBrush = Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(10, 0, 0, 0),
                Padding = new Thickness(12)
            };
            var detailPanel = new DockPanel();
            var detailTitle = new TextBlock
            {
                Text = "条目详情（只读）",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = Heading,
                Margin = new Thickness(0, 0, 0, 9)
            };
            DockPanel.SetDock(detailTitle, Dock.Top);
            detailPanel.Children.Add(detailTitle);
            _detail = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = Heading,
                FontSize = 13,
                Padding = new Thickness(0)
            };
            detailPanel.Children.Add(_detail);
            detailBorder.Child = detailPanel;
            Grid.SetColumn(detailBorder, 1);
            body.Children.Add(detailBorder);
            return body;
        }

        private UIElement BuildFooter()
        {
            var footer = new DockPanel { Margin = new Thickness(16, 0, 16, 12) };
            var close = MakeButton("关闭", 82);
            close.IsCancel = true;
            close.Click += delegate { Close(); };
            DockPanel.SetDock(close, Dock.Right);
            footer.Children.Add(close);
            footer.Children.Add(new TextBlock
            {
                Text = "数据源：当前店铺的旧版 KnowledgeBaseJson。列表是打开/刷新时生成的只读快照。按 Esc 可关闭。",
                Foreground = Muted,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            });
            return footer;
        }

        private void ReloadSnapshot(bool showResult)
        {
            try
            {
                var source = ReadShopSnapshot(_shop);
                _all = source.Where(x => x != null).Select(PreviewRow.From).ToList();
                RebuildCategories();
                ApplyFilter();
                if (showResult)
                {
                    MessageBox.Show(
                        this,
                        "已重新读取当前店铺的旧版知识快照，共 " + _all.Count + " 条。未修改任何数据。",
                        "刷新完成",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                _all = new List<PreviewRow>();
                _visible.Clear();
                _detail.Text = "读取失败。此窗口已保持只读且未修改任何数据。";
                _summary.Text = "读取失败";
                MessageBox.Show(
                    this,
                    "读取当前店铺的旧版知识失败：" + ex.Message,
                    "旧版知识库预览",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private static List<KnowledgeBaseEntry> ReadShopSnapshot(ShopContext shop)
        {
            string json;
            var store = new ShopScopedSettingsStore(shop, Paths);
            if (!store.TryGetString(LegacyKnowledgeKey, out json) || string.IsNullOrWhiteSpace(json))
                return new List<KnowledgeBaseEntry>();
            return JsonConvert.DeserializeObject<List<KnowledgeBaseEntry>>(json)
                ?? new List<KnowledgeBaseEntry>();
        }

        private void RebuildCategories()
        {
            var selected = Convert.ToString(_category.SelectedItem) ?? "全部分类";
            var categories = _all
                .Select(x => x.Category)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();

            _updatingCategories = true;
            try
            {
                _category.Items.Clear();
                _category.Items.Add("全部分类");
                foreach (var category in categories) _category.Items.Add(category);
                _category.SelectedItem = _category.Items.Cast<object>()
                    .FirstOrDefault(x => string.Equals(Convert.ToString(x), selected, StringComparison.Ordinal));
                if (_category.SelectedIndex < 0) _category.SelectedIndex = 0;
            }
            finally
            {
                _updatingCategories = false;
            }
        }

        private void ApplyFilter()
        {
            if (_search == null || _category == null || _grid == null) return;
            var query = (_search.Text ?? string.Empty).Trim().ToLowerInvariant();
            var category = Convert.ToString(_category.SelectedItem) ?? "全部分类";
            var selectedId = (_grid.SelectedItem as PreviewRow) == null
                ? string.Empty
                : ((PreviewRow)_grid.SelectedItem).Id;

            var filtered = _all.Where(x =>
                (string.Equals(category, "全部分类", StringComparison.Ordinal)
                    || string.Equals(x.Category, category, StringComparison.Ordinal))
                && (query.Length == 0 || x.SearchText.IndexOf(query, StringComparison.Ordinal) >= 0))
                .ToList();

            _visible.Clear();
            foreach (var row in filtered) _visible.Add(row);
            _summary.Text = "旧版共 " + _all.Count + " 条　当前显示 " + _visible.Count + " 条";

            PreviewRow selected = null;
            if (selectedId.Length > 0)
                selected = _visible.FirstOrDefault(x => string.Equals(x.Id, selectedId, StringComparison.Ordinal));
            _grid.SelectedItem = selected ?? _visible.FirstOrDefault();
            ShowSelectedDetail();
        }

        private void ShowSelectedDetail()
        {
            if (_detail == null) return;
            var row = _grid == null ? null : _grid.SelectedItem as PreviewRow;
            if (row == null)
            {
                _detail.Text = _all.Count == 0
                    ? "当前店铺没有已保存的旧版知识条目。预览不会猜测旧全局数据归属；如有尚未迁移的旧数据，请先在“店铺绑定”确认归属。"
                    : "当前筛选条件下没有条目。";
                return;
            }

            _detail.Text =
                "问题\r\n" + ValueOrDash(row.Title)
                + "\r\n\r\n答案\r\n" + ValueOrDash(row.Answer)
                + "\r\n\r\n分类\r\n" + ValueOrDash(row.Category)
                + "\r\n\r\n关键词\r\n" + ValueOrDash(row.Keywords)
                + "\r\n\r\n旧版启用标记\r\n" + row.EnabledText
                + "（仅展示，不代表已启用旧版运行时）"
                + "\r\n\r\n来源\r\n" + ValueOrDash(row.SourceType)
                + "\r\n\r\nAI 生成\r\n" + row.AiGeneratedText
                + "\r\n\r\n创建时间\r\n" + ValueOrDash(row.CreatedAt)
                + "\r\n\r\n更新时间\r\n" + ValueOrDash(row.UpdatedAt)
                + "\r\n\r\nID\r\n" + ValueOrDash(row.Id);
        }

        private static string ValueOrDash(string value)
        {
            value = (value ?? string.Empty).Trim();
            return value.Length == 0 ? "—" : value;
        }

        private static string DisplayShop(ShopContext shop)
        {
            if (shop == null) return "未识别";
            var display = (shop.DisplayName ?? string.Empty).Trim();
            return display.Length > 0 ? display : shop.ShopKey;
        }

        private static Button MakeButton(string text, double width)
        {
            return new Button
            {
                Content = text,
                Width = width,
                Height = 30,
                Background = Brushes.White,
                Foreground = Heading,
                BorderBrush = Border,
                Cursor = Cursors.Hand
            };
        }

        private static DataGridTextColumn TextColumn(string header, string property, double width)
        {
            return TextColumn(header, property, new DataGridLength(width));
        }

        private static DataGridTextColumn TextColumn(string header, string property, DataGridLength width)
        {
            return new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(property),
                Width = width,
                IsReadOnly = true
            };
        }
    }
}
