using Bot.ChromeNs;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace Bot.Options
{
    internal enum BulkImportMode
    {
        Cancel,
        Replace,
        Merge,
        Append
    }

    internal sealed class BulkImportModeWindow : Window
    {
        public BulkImportMode SelectedMode { get; private set; }

        private BulkImportModeWindow(string subject, int count)
        {
            SelectedMode = BulkImportMode.Cancel;
            Title = "选择导入方式";
            Width = 540;
            Height = 330;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var root = new StackPanel { Margin = new Thickness(20) };
            Content = root;
            root.Children.Add(new TextBlock
            {
                Text = "准备导入 " + count + " 条" + subject + "，请选择处理方式：",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 14)
            });
            AddChoice(root, "覆盖全部", "导入内容替换当前全部配置；操作前自动备份。", BulkImportMode.Replace);
            AddChoice(root, "合并更新", "相同ID、关键词或问题更新，新记录追加；未出现在文件中的记录保留。", BulkImportMode.Merge);
            AddChoice(root, "仅追加", "只增加当前不存在的新记录，不修改已有记录。", BulkImportMode.Append);

            var cancel = new Button
            {
                Content = "取消",
                Width = 90,
                Height = 30,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            cancel.Click += (s, e) => Close();
            root.Children.Add(cancel);
        }

        private void AddChoice(Panel panel, string title, string help, BulkImportMode mode)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.Bold });
            content.Children.Add(new TextBlock
            {
                Text = help,
                Foreground = Brushes.DimGray,
                TextWrapping = TextWrapping.Wrap
            });
            var button = new Button
            {
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(12, 8, 12, 8),
                Content = content
            };
            button.Click += (s, e) =>
            {
                SelectedMode = mode;
                DialogResult = true;
            };
            panel.Children.Add(button);
        }

        public static BulkImportMode Choose(Window owner, string subject, int count)
        {
            var window = new BulkImportModeWindow(subject, count) { Owner = owner };
            return window.ShowDialog() == true ? window.SelectedMode : BulkImportMode.Cancel;
        }
    }

    internal static class HandoffPolicyUiBridge
    {
        private static readonly ConditionalWeakTable<FeatureSettingsWindow, object> Attached =
            new ConditionalWeakTable<FeatureSettingsWindow, object>();
        private static int _initialized;

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            EventManager.RegisterClassHandler(
                typeof(FeatureSettingsWindow),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnLoaded),
                true);
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            var window = sender as FeatureSettingsWindow;
            if (window == null) return;
            object ignored;
            if (Attached.TryGetValue(window, out ignored)) return;
            Attached.Add(window, new object());
            window.Dispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                new Action(() => AttachButton(window)));
        }

        private static void AttachButton(FeatureSettingsWindow window)
        {
            var title = FindText(window, "转人工通知");
            var panel = title == null ? null : LogicalTreeHelper.GetParent(title) as Panel;
            if (title == null || panel == null) return;
            var index = panel.Children.IndexOf(title);
            if (index < 0) return;

            var row = new DockPanel { Margin = title.Margin };
            var button = new Button
            {
                Content = "通知策略",
                Width = 92,
                Height = 28,
                Margin = new Thickness(12, 0, 0, 0),
                ToolTip = "在本机管理AI转人工策略；规则不会上传到企业微信控制面。"
            };
            DockPanel.SetDock(button, Dock.Right);
            button.Click += (s, e) =>
            {
                var editor = new HandoffPolicyWindow { Owner = window };
                editor.ShowDialog();
            };
            row.Children.Add(button);
            row.Children.Add(new TextBlock
            {
                Text = "转人工通知",
                FontWeight = title.FontWeight,
                FontSize = title.FontSize,
                Foreground = title.Foreground,
                VerticalAlignment = VerticalAlignment.Center
            });
            panel.Children.RemoveAt(index);
            panel.Children.Insert(index, row);
        }

        private static TextBlock FindText(DependencyObject root, string value)
        {
            if (root == null) return null;
            var block = root as TextBlock;
            if (block != null && string.Equals(block.Text, value, StringComparison.Ordinal)) return block;

            var visualCount = 0;
            try { visualCount = VisualTreeHelper.GetChildrenCount(root); } catch { visualCount = 0; }
            for (var i = 0; i < visualCount; i++)
            {
                var found = FindText(VisualTreeHelper.GetChild(root, i), value);
                if (found != null) return found;
            }
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            {
                var found = FindText(child, value);
                if (found != null) return found;
            }
            return null;
        }
    }

    internal sealed class HandoffPolicyWindow : Window
    {
        private readonly ObservableCollection<RemoteHandoffRule> _rules;
        private readonly DataGrid _grid;
        private readonly TextBlock _status;

        public HandoffPolicyWindow()
        {
            Title = "AI转人工通知策略（本机）";
            Width = 1260;
            Height = 720;
            MinWidth = 980;
            MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            _rules = new ObservableCollection<RemoteHandoffRule>(HandoffRuleRemoteConfigService.GetRules());

            var root = new DockPanel { Margin = new Thickness(14) };
            Content = root;

            var intro = new TextBlock
            {
                Text = "策略只保存在本机 %LocalAppData%\\QianniuAiBot\\data\\handoff-policy.json。企业微信服务端只负责通知通道和人工回复，不再保存或下发AI转人工规则。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 0, 0, 10)
            };
            DockPanel.SetDock(intro, Dock.Top);
            root.Children.Add(intro);

            var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(toolbar, Dock.Top);
            root.Children.Add(toolbar);
            AddButton(toolbar, "新增", 72, (s, e) => AddRule());
            AddButton(toolbar, "全选", 72, (s, e) => SetAll(true));
            AddButton(toolbar, "取消全选", 86, (s, e) => SetAll(false));
            AddButton(toolbar, "删除所选", 86, (s, e) => DeleteSelected());
            AddButton(toolbar, "清空全部", 86, (s, e) => ClearAll());
            AddButton(toolbar, "导入", 72, (s, e) => Import());
            AddButton(toolbar, "导出", 72, (s, e) => Export());
            AddButton(toolbar, "恢复默认", 86, (s, e) => RestoreDefaults());

            _status = new TextBlock
            {
                Margin = new Thickness(0, 0, 0, 8),
                Foreground = Brushes.DimGray
            };
            DockPanel.SetDock(_status, Dock.Top);
            root.Children.Add(_status);

            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);
            AddButton(footer, "保存", 86, (s, e) => Save(false));
            AddButton(footer, "保存并关闭", 108, (s, e) => Save(true));
            AddButton(footer, "关闭", 76, (s, e) => Close());

            _grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                ItemsSource = _rules,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                IsReadOnly = false,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            _grid.Columns.Add(new DataGridCheckBoxColumn
            {
                Header = "选择",
                Binding = TwoWay("IsSelected"),
                Width = 55
            });
            _grid.Columns.Add(new DataGridCheckBoxColumn
            {
                Header = "启用",
                Binding = TwoWay("Enabled"),
                Width = 55
            });
            _grid.Columns.Add(new DataGridComboBoxColumn
            {
                Header = "类型",
                SelectedItemBinding = TwoWay("RuleType"),
                ItemsSource = new[] { "manual", "confirm" },
                Width = 85
            });
            _grid.Columns.Add(new DataGridTextColumn { Header = "关键词", Binding = TwoWay("Keyword"), Width = 110 });
            _grid.Columns.Add(new DataGridComboBoxColumn
            {
                Header = "匹配方式",
                SelectedItemBinding = TwoWay("MatchMode"),
                ItemsSource = new[] { "contains", "sensitive_context" },
                Width = 135
            });
            _grid.Columns.Add(new DataGridTextColumn { Header = "风险语境", Binding = TwoWay("RiskTerms"), Width = 220 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "安全例外", Binding = TwoWay("Exceptions"), Width = 220 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "例外回复", Binding = TwoWay("SafeReply"), Width = 260 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "备注", Binding = TwoWay("Note"), Width = 220 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "顺序", Binding = TwoWay("SortOrder"), Width = 70 });
            root.Children.Add(_grid);
            UpdateStatus();
        }

        private static Binding TwoWay(string path)
        {
            return new Binding(path)
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            };
        }

        private void AddRule()
        {
            var next = _rules.Count == 0 ? 10 : _rules.Max(x => x.SortOrder) + 10;
            var item = new RemoteHandoffRule
            {
                Enabled = true,
                RuleType = "confirm",
                MatchMode = "contains",
                SortOrder = next
            };
            _rules.Add(item);
            _grid.SelectedItem = item;
            _grid.ScrollIntoView(item);
            UpdateStatus("已新增，尚未保存");
        }

        private void SetAll(bool selected)
        {
            Commit();
            foreach (var item in _rules) item.IsSelected = selected;
            _grid.Items.Refresh();
            UpdateStatus();
        }

        private void DeleteSelected()
        {
            Commit();
            var selected = _rules.Where(x => x.IsSelected).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("请先勾选要删除的策略。", "通知策略", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show(
                "确定删除已勾选的 " + selected.Count + " 条策略吗？",
                "删除策略",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            foreach (var item in selected) _rules.Remove(item);
            UpdateStatus("已删除，尚未保存");
        }

        private void ClearAll()
        {
            if (_rules.Count == 0) return;
            if (MessageBox.Show(
                "确定清空全部AI转人工通知策略吗？保存后所有关键词都将停止触发转人工。",
                "清空策略",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _rules.Clear();
            UpdateStatus("已清空，尚未保存");
        }

        private void Import()
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Title = "导入AI转人工通知策略",
                    Filter = "JSON文件 (*.json)|*.json"
                };
                if (dialog.ShowDialog(this) != true) return;
                var incoming = HandoffRuleRemoteConfigService.ParseImport(
                    File.ReadAllText(dialog.FileName, Encoding.UTF8));
                var mode = BulkImportModeWindow.Choose(this, "转人工策略", incoming.Count);
                if (mode == BulkImportMode.Cancel) return;
                Commit();

                List<RemoteHandoffRule> result;
                if (mode == BulkImportMode.Replace)
                {
                    result = incoming.Select(CloneRule).ToList();
                }
                else
                {
                    result = _rules.Select(CloneRule).ToList();
                    foreach (var item in incoming)
                    {
                        var existing = result.FirstOrDefault(x => x.Id > 0 && item.Id > 0 && x.Id == item.Id)
                            ?? result.FirstOrDefault(x => string.Equals(
                                x.Keyword,
                                item.Keyword,
                                StringComparison.OrdinalIgnoreCase));
                        if (existing == null)
                        {
                            result.Add(CloneRule(item));
                        }
                        else if (mode == BulkImportMode.Merge)
                        {
                            CopyRule(item, existing);
                        }
                    }
                }
                _rules.Clear();
                foreach (var item in result.OrderBy(x => x.SortOrder).ThenBy(x => x.Id))
                    _rules.Add(item);
                UpdateStatus("已导入，尚未保存");
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "导入通知策略失败：" + ex.Message,
                    "通知策略",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void Export()
        {
            try
            {
                Commit();
                var dialog = new SaveFileDialog
                {
                    Title = "导出AI转人工通知策略",
                    FileName = "qianniu-handoff-policy-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json",
                    Filter = "JSON文件 (*.json)|*.json"
                };
                if (dialog.ShowDialog(this) != true) return;
                File.WriteAllText(
                    dialog.FileName,
                    HandoffRuleRemoteConfigService.ExportJson(_rules),
                    new UTF8Encoding(false));
                UpdateStatus("已导出：" + Path.GetFileName(dialog.FileName));
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "导出通知策略失败：" + ex.Message,
                    "通知策略",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void RestoreDefaults()
        {
            if (MessageBox.Show(
                "确定恢复内置默认转人工策略吗？当前未保存修改会丢失。",
                "恢复默认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var defaults = HandoffRuleRemoteConfigService.ResetDefaults();
            _rules.Clear();
            foreach (var item in defaults) _rules.Add(item);
            UpdateStatus("已恢复并保存默认策略");
        }

        private void Save(bool close)
        {
            try
            {
                Commit();
                HandoffRuleRemoteConfigService.SaveRules(_rules);
                UpdateStatus("已保存：" + DateTime.Now.ToString("HH:mm:ss"));
                if (close) Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "保存通知策略失败：" + ex.Message,
                    "通知策略",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void Commit()
        {
            try
            {
                _grid.CommitEdit(DataGridEditingUnit.Cell, true);
                _grid.CommitEdit(DataGridEditingUnit.Row, true);
            }
            catch { }
        }

        private void UpdateStatus(string prefix = null)
        {
            _status.Text = (string.IsNullOrWhiteSpace(prefix) ? string.Empty : prefix + " · ")
                + "共 " + _rules.Count + " 条，启用 " + _rules.Count(x => x.Enabled)
                + " 条，已勾选 " + _rules.Count(x => x.IsSelected) + " 条";
        }

        private static void AddButton(Panel panel, string text, double width, RoutedEventHandler click)
        {
            var button = new Button
            {
                Content = text,
                Width = width,
                Height = 30,
                Margin = new Thickness(0, 0, 8, 6)
            };
            button.Click += click;
            panel.Children.Add(button);
        }

        private static RemoteHandoffRule CloneRule(RemoteHandoffRule source)
        {
            var target = new RemoteHandoffRule();
            CopyRule(source, target);
            return target;
        }

        private static void CopyRule(RemoteHandoffRule source, RemoteHandoffRule target)
        {
            target.Id = source.Id;
            target.Enabled = source.Enabled;
            target.RuleType = source.RuleType;
            target.Keyword = source.Keyword;
            target.MatchMode = source.MatchMode;
            target.RiskTerms = source.RiskTerms;
            target.Exceptions = source.Exceptions;
            target.SafeReply = source.SafeReply;
            target.Note = source.Note;
            target.SortOrder = source.SortOrder;
            target.IsSelected = false;
        }
    }
}
