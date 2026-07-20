using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Bot.ChromeNs;
using Bot.Options;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace Bot.Knowledge
{
    public class KnowledgeManagerControl : UserControl
    {
        private List<KnowledgeBaseEntry> _all = new List<KnowledgeBaseEntry>();
        private readonly ObservableCollection<KnowledgeBaseEntry> _view = new ObservableCollection<KnowledgeBaseEntry>();
        private TextBox _search;
        private ComboBox _cat;
        private TextBlock _count;
        private DataGrid _grid;

        public KnowledgeManagerControl()
        {
            Build();
            RefreshData();
            Loaded += (s, e) => KnowledgeLearningService.KnowledgeBaseChanged += OnKnowledgeBaseChanged;
            Unloaded += (s, e) => KnowledgeLearningService.KnowledgeBaseChanged -= OnKnowledgeBaseChanged;
        }

        private void Build()
        {
            var root = new DockPanel { Margin = new Thickness(10) };
            Content = root;

            var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);

            _search = new TextBox
            {
                Width = 220,
                Height = 28,
                Text = string.Empty,
                ToolTip = "请输入问题、答案、关键词..."
            };
            _search.TextChanged += (s, e) => ApplyFilter();
            top.Children.Add(_search);

            _cat = new ComboBox
            {
                Width = 150,
                Height = 28,
                Margin = new Thickness(8, 0, 8, 0)
            };
            _cat.SelectionChanged += (s, e) => ApplyFilter();
            top.Children.Add(_cat);

            AddBtn(top, "搜索", 65, (s, e) => ApplyFilter());
            AddBtn(top, "清空", 65, (s, e) =>
            {
                _search.Text = string.Empty;
                _cat.SelectedIndex = 0;
                ApplyFilter();
            });
            AddBtn(top, "新增问答", 86, (s, e) => AddNew());
            AddBtn(top, "导入JSON", 86, (s, e) => ImportJson());
            AddBtn(top, "导出JSON", 86, (s, e) => ExportJson());

            _count = new TextBlock { Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(_count, Dock.Bottom);
            root.Children.Add(_count);

            _grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                ItemsSource = _view,
                IsReadOnly = true
            };
            _grid.MouseDoubleClick += (s, e) => EditSelected();
            root.Children.Add(_grid);

            _grid.Columns.Add(new DataGridCheckBoxColumn { Header = "启用", Binding = new Binding("Enabled"), Width = 55 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "分类", Binding = new Binding("Category"), Width = 130 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "问题", Binding = new Binding("Title"), Width = 220 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "答案", Binding = new Binding("Answer"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            _grid.Columns.Add(new DataGridTextColumn { Header = "关键词", Binding = new Binding("Keywords"), Width = 160 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "来源", Binding = new Binding("SourceType"), Width = 110 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "更新时间", Binding = new Binding("UpdatedAt"), Width = 140 });
            _grid.Columns.Add(new DataGridTemplateColumn { Header = "操作", Width = 120, CellTemplate = OpTemplate() });
        }

        private static void AddBtn(Panel panel, string text, double width, RoutedEventHandler handler)
        {
            var button = new Button
            {
                Content = text,
                Width = width,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 6)
            };
            button.Click += handler;
            panel.Children.Add(button);
        }

        private DataTemplate OpTemplate()
        {
            var template = new DataTemplate();
            var panel = new FrameworkElementFactory(typeof(StackPanel));
            panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

            var edit = new FrameworkElementFactory(typeof(Button));
            edit.SetValue(Button.ContentProperty, "编辑");
            edit.SetValue(Button.MarginProperty, new Thickness(0, 0, 4, 0));
            edit.AddHandler(Button.ClickEvent, new RoutedEventHandler((s, e) =>
            {
                _grid.SelectedItem = ((FrameworkElement)s).DataContext;
                EditSelected();
            }));
            panel.AppendChild(edit);

            var delete = new FrameworkElementFactory(typeof(Button));
            delete.SetValue(Button.ContentProperty, "删除");
            delete.AddHandler(Button.ClickEvent, new RoutedEventHandler((s, e) =>
            {
                _grid.SelectedItem = ((FrameworkElement)s).DataContext;
                DeleteSelected();
            }));
            panel.AppendChild(delete);

            template.VisualTree = panel;
            return template;
        }

        public void RefreshData()
        {
            _all = BotFeatureStore.GetKnowledgeBase();
            RefreshCategories();
            ApplyFilter();
        }

        public bool LocateEntry(
            string seller,
            string buyer,
            string question,
            string answer)
        {
            RefreshData();
            var item = ResolveEntry(seller, buyer, question, answer);
            if (item == null)
            {
                _cat.SelectedIndex = 0;
                _search.Text = !string.IsNullOrWhiteSpace(answer)
                    ? answer.Trim()
                    : (question ?? string.Empty).Trim();
                ApplyFilter();
                _search.Focus();
                return false;
            }

            _cat.SelectedIndex = 0;
            _search.Text = string.IsNullOrWhiteSpace(item.Title)
                ? item.Answer ?? string.Empty
                : item.Title;
            ApplyFilter();

            var selected = _view.FirstOrDefault(x => SameEntry(x, item));
            if (selected == null)
            {
                _search.Text = string.Empty;
                ApplyFilter();
                selected = _view.FirstOrDefault(x => SameEntry(x, item));
            }
            if (selected == null) return false;

            _grid.SelectedItem = selected;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _grid.ScrollIntoView(selected);
                _grid.Focus();
            }));
            return true;
        }

        private KnowledgeBaseEntry ResolveEntry(
            string seller,
            string buyer,
            string question,
            string answer)
        {
            var answerKey = Canonical(answer);
            if (!string.IsNullOrWhiteSpace(answerKey))
            {
                var byAnswer = _all.FirstOrDefault(x => x != null
                    && (Canonical(x.Answer) == answerKey
                        || Canonical(BotFeatureStore.ApplyOutputPolicy(x.Answer)) == answerKey));
                if (byAnswer != null) return byAnswer;
            }

            var questionKey = KnowledgeAiService.NormalizeQuestion(question);
            if (!string.IsNullOrWhiteSpace(questionKey))
            {
                var byQuestion = _all.FirstOrDefault(x => x != null
                    && KnowledgeAiService.NormalizeQuestion(x.Title) == questionKey);
                if (byQuestion != null) return byQuestion;
            }

            KnowledgeBaseEntry matched;
            double score;
            if (KnowledgeLearningService.TryFindLocalAnswer(
                seller,
                buyer,
                question,
                out matched,
                out score))
            {
                var byId = _all.FirstOrDefault(x => SameEntry(x, matched));
                if (byId != null) return byId;
            }

            var previousAssistant = ConversationContextStore
                .GetRecentTurns(seller, buyer, question, 12)
                .Where(x => x != null
                    && x.Role == "assistant"
                    && !x.Withdrawn
                    && !string.IsNullOrWhiteSpace(x.Text))
                .OrderByDescending(x => x.Timestamp)
                .FirstOrDefault();
            if (previousAssistant != null)
            {
                var previousKey = Canonical(previousAssistant.Text);
                var byPreviousAnswer = _all.FirstOrDefault(x => x != null
                    && (Canonical(x.Answer) == previousKey
                        || Canonical(BotFeatureStore.ApplyOutputPolicy(x.Answer)) == previousKey));
                if (byPreviousAnswer != null) return byPreviousAnswer;
            }

            return null;
        }

        private static bool SameEntry(KnowledgeBaseEntry left, KnowledgeBaseEntry right)
        {
            if (left == null || right == null) return false;
            if (!string.IsNullOrWhiteSpace(left.Id)
                && !string.IsNullOrWhiteSpace(right.Id))
            {
                return string.Equals(left.Id, right.Id, StringComparison.Ordinal);
            }
            return KnowledgeAiService.NormalizeQuestion(left.Title)
                == KnowledgeAiService.NormalizeQuestion(right.Title)
                && Canonical(left.Answer) == Canonical(right.Answer);
        }

        private static string Canonical(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ");
        }

        private void RefreshCategories()
        {
            var old = _cat.SelectedItem as string;
            _cat.Items.Clear();
            _cat.Items.Add("全部分类");
            foreach (var category in _all
                .Select(x => x.Category ?? string.Empty)
                .Where(x => x.Length > 0)
                .Distinct()
                .OrderBy(x => x))
            {
                _cat.Items.Add(category);
            }
            _cat.SelectedItem = !string.IsNullOrWhiteSpace(old) && _cat.Items.Contains(old)
                ? old
                : "全部分类";
        }

        private void ApplyFilter()
        {
            if (_view == null) return;
            var query = (_search == null ? string.Empty : _search.Text).Trim();
            var category = _cat == null ? "全部分类" : (_cat.SelectedItem as string ?? "全部分类");
            var list = _all.Where(x => Match(x, query, category)).ToList();
            _view.Clear();
            foreach (var item in list) _view.Add(item);
            if (_count != null) _count.Text = "共 " + _all.Count + " 条知识，当前显示 " + _view.Count + " 条";
        }

        private static bool Match(KnowledgeBaseEntry item, string query, string category)
        {
            if (category != "全部分类" && (item.Category ?? string.Empty) != category) return false;
            if (string.IsNullOrWhiteSpace(query)) return true;
            return ((item.Category ?? string.Empty)
                + " " + (item.Title ?? string.Empty)
                + " " + (item.Answer ?? string.Empty)
                + " " + (item.Keywords ?? string.Empty)
                + " " + (item.SourceType ?? string.Empty))
                .IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void AddNew()
        {
            OpenEditor(new KnowledgeBaseEntry { Enabled = true, Category = "通用" }, true);
        }

        private void EditSelected()
        {
            var item = _grid.SelectedItem as KnowledgeBaseEntry;
            if (item != null) OpenEditor(item, false);
        }

        private void OpenEditor(KnowledgeBaseEntry item, bool add)
        {
            var window = new KnowledgeEditWindow(item, _all.Select(x => x.Category))
            {
                Owner = Window.GetWindow(this)
            };
            if (window.ShowDialog() != true) return;
            if (add) _all.Add(item);
            BotFeatureStore.SaveKnowledgeBase(_all);
            RefreshCategories();
            ApplyFilter();
        }

        private void DeleteSelected()
        {
            var item = _grid.SelectedItem as KnowledgeBaseEntry;
            if (item == null) return;
            if (MessageBox.Show(
                "确定删除这条知识吗？",
                "确认删除",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _all.Remove(item);
            BotFeatureStore.SaveKnowledgeBase(_all);
            RefreshCategories();
            ApplyFilter();
        }

        private void ImportJson()
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Title = "导入知识库",
                    Filter = "JSON文件 (*.json)|*.json|所有文件 (*.*)|*.*"
                };
                if (dialog.ShowDialog() != true) return;

                var list = JsonConvert.DeserializeObject<List<KnowledgeBaseEntry>>(
                    File.ReadAllText(dialog.FileName, System.Text.Encoding.UTF8));
                if (list == null) throw new Exception("JSON中没有知识库数据");

                var overwrite = MessageBox.Show(
                    "是否覆盖全部知识？\n\n选择“否”将追加导入并自动按问题去重。",
                    "导入方式",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);
                if (overwrite == MessageBoxResult.Cancel) return;
                if (overwrite == MessageBoxResult.Yes)
                {
                    _all = list;
                }
                else
                {
                    var seen = new HashSet<string>(_all.Select(x => KnowledgeAiService.NormalizeQuestion(x.Title)));
                    foreach (var item in list)
                    {
                        var key = KnowledgeAiService.NormalizeQuestion(item.Title);
                        if (seen.Contains(key)) continue;
                        _all.Add(item);
                        seen.Add(key);
                    }
                }

                BotFeatureStore.SaveKnowledgeBase(_all);
                RefreshCategories();
                ApplyFilter();
            }
            catch (Exception ex)
            {
                MessageBox.Show("导入失败：" + ex.Message);
            }
        }

        private void ExportJson()
        {
            try
            {
                var dialog = new SaveFileDialog
                {
                    Title = "导出知识库",
                    FileName = "qianniu-knowledge-base.json",
                    Filter = "JSON文件 (*.json)|*.json|所有文件 (*.*)|*.*"
                };
                if (dialog.ShowDialog() != true) return;
                File.WriteAllText(
                    dialog.FileName,
                    JsonConvert.SerializeObject(_all, Formatting.Indented),
                    System.Text.Encoding.UTF8);
            }
            catch (Exception ex)
            {
                MessageBox.Show("导出失败：" + ex.Message);
            }
        }

        private void OnKnowledgeBaseChanged(object sender, EventArgs e)
        {
            if (Dispatcher.CheckAccess()) RefreshData();
            else Dispatcher.BeginInvoke(new Action(RefreshData));
        }
    }
}
