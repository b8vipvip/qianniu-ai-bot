using Bot.ChromeNs;
using Bot.Options;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace Bot.Knowledge
{
    internal static class BulkListManagementUi
    {
        private sealed class SelectionBucket
        {
            public readonly HashSet<string> Keys = new HashSet<string>(StringComparer.Ordinal);
        }

        private static readonly ConditionalWeakTable<object, SelectionBucket> Selections =
            new ConditionalWeakTable<object, SelectionBucket>();
        private static readonly ConditionalWeakTable<DependencyObject, object> Attached =
            new ConditionalWeakTable<DependencyObject, object>();
        private static int _initialized;

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            EventManager.RegisterClassHandler(
                typeof(KnowledgeManagerControl),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnKnowledgeLoaded),
                true);
            EventManager.RegisterClassHandler(
                typeof(KnowledgePolicyProfileWindow),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnPolicyLoaded),
                true);
            EventManager.RegisterClassHandler(
                typeof(StorePromptProfileWindow),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnStoreLoaded),
                true);
        }

        private static bool MarkAttached(DependencyObject target)
        {
            object marker;
            if (target == null || Attached.TryGetValue(target, out marker)) return false;
            try { Attached.Add(target, new object()); }
            catch { return false; }
            return true;
        }

        private static void OnKnowledgeLoaded(object sender, RoutedEventArgs e)
        {
            var manager = sender as KnowledgeManagerControl;
            if (manager == null || !MarkAttached(manager)) return;
            manager.Dispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                new Action(() => AttachKnowledge(manager)));
        }

        private static void AttachKnowledge(KnowledgeManagerControl manager)
        {
            var grid = GetField<DataGrid>(manager, "_grid");
            var top = FindFirst<WrapPanel>(manager);
            if (grid == null || top == null) return;

            grid.Columns.Insert(0, new DataGridTemplateColumn
            {
                Header = "选择",
                Width = 55,
                IsReadOnly = false,
                CellTemplate = SelectionTemplate(manager, item => KnowledgeKey(item as KnowledgeBaseEntry))
            });

            var oldImport = top.Children.OfType<Button>()
                .FirstOrDefault(x => string.Equals(
                    Convert.ToString(x.Content),
                    "导入JSON",
                    StringComparison.Ordinal));
            if (oldImport != null) oldImport.Visibility = Visibility.Collapsed;

            AddToolbarButton(top, "导入（多模式）", 108, (s, e) => ImportKnowledge(manager));
            AddToolbarButton(top, "全选当前", 86, (s, e) => SelectVisibleKnowledge(manager, true));
            AddToolbarButton(top, "取消全选", 86, (s, e) => SelectVisibleKnowledge(manager, false));
            AddToolbarButton(top, "删除所选", 86, (s, e) => DeleteSelectedKnowledge(manager));
            AddToolbarButton(top, "清空全部", 86, (s, e) => ClearKnowledge(manager));
        }

        private static void ImportKnowledge(KnowledgeManagerControl manager)
        {
            try
            {
                var owner = Window.GetWindow(manager);
                var dialog = new OpenFileDialog
                {
                    Title = "导入知识库",
                    Filter = "JSON文件 (*.json)|*.json"
                };
                if (dialog.ShowDialog(owner) != true) return;

                var incoming = ParseKnowledge(File.ReadAllText(dialog.FileName, Encoding.UTF8));
                var mode = BulkImportModeWindow.Choose(owner, "知识", incoming.Count);
                if (mode == BulkImportMode.Cancel) return;

                var current = GetField<List<KnowledgeBaseEntry>>(manager, "_all")
                    ?? BotFeatureStore.GetKnowledgeBase()
                    ?? new List<KnowledgeBaseEntry>();
                BackupJson("knowledge-before-import", JArray.FromObject(current));
                NormalizeKnowledge(incoming);

                var added = 0;
                var updated = 0;
                var skipped = 0;
                List<KnowledgeBaseEntry> result;
                if (mode == BulkImportMode.Replace)
                {
                    result = incoming;
                    added = result.Count;
                }
                else
                {
                    result = current.ToList();
                    foreach (var item in incoming)
                    {
                        var existing = FindKnowledge(result, item);
                        if (existing == null)
                        {
                            result.Add(item);
                            added++;
                        }
                        else if (mode == BulkImportMode.Merge)
                        {
                            CopyKnowledge(item, existing);
                            updated++;
                        }
                        else
                        {
                            skipped++;
                        }
                    }
                }

                BotFeatureStore.SaveKnowledgeBase(result);
                Selections.GetOrCreateValue(manager).Keys.Clear();
                Invoke(manager, "RefreshData");
                MessageBox.Show(
                    "知识库导入完成。\n新增：" + added + "\n更新：" + updated + "\n跳过：" + skipped,
                    "知识库",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "导入知识库失败：" + ex.Message,
                    "知识库",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private static List<KnowledgeBaseEntry> ParseKnowledge(string json)
        {
            var token = JToken.Parse(json);
            var array = token as JArray;
            var root = token as JObject;
            if (array == null && root != null)
            {
                array = root["knowledge"] as JArray
                    ?? root["items"] as JArray
                    ?? root["entries"] as JArray
                    ?? root["Knowledge"] as JArray;
            }
            if (array == null) throw new Exception("文件中没有知识数组。");
            return array.ToObject<List<KnowledgeBaseEntry>>() ?? new List<KnowledgeBaseEntry>();
        }

        private static void SelectVisibleKnowledge(KnowledgeManagerControl manager, bool selected)
        {
            var view = GetField<ObservableCollection<KnowledgeBaseEntry>>(manager, "_view");
            var bucket = Selections.GetOrCreateValue(manager);
            foreach (var item in view ?? new ObservableCollection<KnowledgeBaseEntry>())
            {
                var key = KnowledgeKey(item);
                if (selected) bucket.Keys.Add(key);
                else bucket.Keys.Remove(key);
            }
            var grid = GetField<DataGrid>(manager, "_grid");
            if (grid != null) grid.Items.Refresh();
        }

        private static void DeleteSelectedKnowledge(KnowledgeManagerControl manager)
        {
            var all = GetField<List<KnowledgeBaseEntry>>(manager, "_all")
                ?? new List<KnowledgeBaseEntry>();
            var bucket = Selections.GetOrCreateValue(manager);
            var deleting = all.Where(x => bucket.Keys.Contains(KnowledgeKey(x))).ToList();
            if (deleting.Count == 0)
            {
                MessageBox.Show("请先勾选要删除的知识。", "知识库", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show(
                "确定删除已勾选的 " + deleting.Count + " 条知识吗？操作前会自动备份。",
                "删除知识",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            BackupJson("knowledge-before-delete", JArray.FromObject(all));
            foreach (var item in deleting) all.Remove(item);
            BotFeatureStore.SaveKnowledgeBase(all);
            bucket.Keys.Clear();
            Invoke(manager, "RefreshData");
        }

        private static void ClearKnowledge(KnowledgeManagerControl manager)
        {
            var all = GetField<List<KnowledgeBaseEntry>>(manager, "_all")
                ?? new List<KnowledgeBaseEntry>();
            if (all.Count == 0) return;
            if (MessageBox.Show(
                "确定清空全部 " + all.Count + " 条知识吗？操作前会自动备份。",
                "清空知识库",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            BackupJson("knowledge-before-clear", JArray.FromObject(all));
            BotFeatureStore.SaveKnowledgeBase(new List<KnowledgeBaseEntry>());
            Selections.GetOrCreateValue(manager).Keys.Clear();
            Invoke(manager, "RefreshData");
        }

        private static void OnPolicyLoaded(object sender, RoutedEventArgs e)
        {
            var window = sender as KnowledgePolicyProfileWindow;
            if (window == null || !MarkAttached(window)) return;
            window.Dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new Action(() => AttachPolicy(window)));
        }

        private static void AttachPolicy(KnowledgePolicyProfileWindow window)
        {
            var grid = GetField<DataGrid>(window, "_grid");
            var save = FindButton(window, "保存策略");
            var panel = save == null ? null : save.Parent as Panel;
            if (grid == null || panel == null) return;

            grid.Columns.Insert(0, new DataGridTemplateColumn
            {
                Header = "选择",
                Width = 55,
                CellTemplate = SelectionTemplate(window, item => PolicyKey(item as KnowledgePolicyProfile))
            });

            foreach (var old in panel.Children.OfType<Button>()
                .Where(x => Convert.ToString(x.Tag) == "knowledge-policies-import")
                .ToList())
            {
                old.Visibility = Visibility.Collapsed;
            }

            var index = panel.Children.IndexOf(save);
            if (index < 0) index = 0;
            InsertButton(panel, index++, "导入（多模式）", 108, (s, e) => ImportPolicies(window));
            InsertButton(panel, index++, "全选当前", 86, (s, e) => SelectVisiblePolicies(window, true));
            InsertButton(panel, index++, "取消全选", 86, (s, e) => SelectVisiblePolicies(window, false));
            InsertButton(panel, index++, "删除所选", 86, (s, e) => ResetSelectedPolicies(window));
            InsertButton(panel, index++, "清空全部", 86, (s, e) => ResetAllPolicies(window));
        }

        private static void ImportPolicies(KnowledgePolicyProfileWindow window)
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Title = "导入知识策略",
                    Filter = "JSON文件 (*.json)|*.json"
                };
                if (dialog.ShowDialog(window) != true) return;

                var token = JToken.Parse(File.ReadAllText(dialog.FileName, Encoding.UTF8));
                var root = token as JObject;
                var array = token as JArray;
                if (array == null && root != null)
                    array = root["profiles"] as JArray ?? root["Profiles"] as JArray;
                if (array == null) throw new Exception("文件中没有 profiles 策略数组。");

                var mode = BulkImportModeWindow.Choose(window, "知识策略", array.Count);
                if (mode == BulkImportMode.Cancel) return;

                BackupFile(KnowledgePolicyPath(), "knowledge-policies-before-import");
                var knowledge = GetField<List<KnowledgeBaseEntry>>(window, "_knowledge")
                    ?? BotFeatureStore.GetKnowledgeBase()
                    ?? new List<KnowledgeBaseEntry>();
                var storedIds = ReadStoredPolicyIds();
                if (mode == BulkImportMode.Replace)
                {
                    foreach (var entry in knowledge) SaveDefaultPolicy(entry);
                }

                var applied = 0;
                var skipped = 0;
                var invalid = 0;
                foreach (var raw in array)
                {
                    var item = raw as JObject;
                    if (item == null)
                    {
                        invalid++;
                        continue;
                    }
                    var id = ReadString(item, "knowledgeId", "KnowledgeId");
                    var question = ReadString(item, "questionSnapshot", "QuestionSnapshot");
                    var entry = FindKnowledgeForPolicy(knowledge, id, question);
                    if (entry == null)
                    {
                        skipped++;
                        continue;
                    }
                    var stable = KnowledgePolicyProfileService.GetProfile(entry).KnowledgeId;
                    if (mode == BulkImportMode.Append && storedIds.Contains(stable))
                    {
                        skipped++;
                        continue;
                    }

                    KnowledgePolicyProfileService.SaveProfile(entry, new KnowledgePolicyProfile
                    {
                        KnowledgeId = stable,
                        QuestionSnapshot = entry.Title,
                        Intent = ReadString(item, "intent", "Intent"),
                        Entities = ReadString(item, "entities", "Entities"),
                        ApplyWhen = ReadString(item, "applyWhen", "ApplyWhen"),
                        DoNotApplyWhen = ReadString(item, "doNotApplyWhen", "DoNotApplyWhen"),
                        RequiredContext = ReadString(item, "requiredContext", "RequiredContext"),
                        AnswerMode = ReadString(item, "answerMode", "AnswerMode"),
                        Confidence = ReadDouble(item, 0.80, "confidence", "Confidence")
                    });
                    applied++;
                }

                Selections.GetOrCreateValue(window).Keys.Clear();
                RefreshPolicies(window, knowledge);
                MessageBox.Show(
                    "知识策略导入完成。\n应用：" + applied
                    + "\n跳过：" + skipped
                    + "\n无效：" + invalid
                    + "\n可靠度学习统计已保留。",
                    "知识策略",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "导入知识策略失败：" + ex.Message,
                    "知识策略",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private static void SelectVisiblePolicies(KnowledgePolicyProfileWindow window, bool selected)
        {
            var grid = GetField<DataGrid>(window, "_grid");
            var bucket = Selections.GetOrCreateValue(window);
            if (grid != null)
            {
                foreach (var item in grid.Items.Cast<object>())
                {
                    var key = PolicyKey(item as KnowledgePolicyProfile);
                    if (key.Length == 0) continue;
                    if (selected) bucket.Keys.Add(key);
                    else bucket.Keys.Remove(key);
                }
                grid.Items.Refresh();
            }
        }

        private static void ResetSelectedPolicies(KnowledgePolicyProfileWindow window)
        {
            var knowledge = GetField<List<KnowledgeBaseEntry>>(window, "_knowledge")
                ?? new List<KnowledgeBaseEntry>();
            var bucket = Selections.GetOrCreateValue(window);
            var selected = knowledge
                .Where(x => bucket.Keys.Contains(KnowledgePolicyProfileService.GetProfile(x).KnowledgeId))
                .ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("请先勾选要删除的知识策略。", "知识策略", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show(
                "删除所选策略会把其可编辑配置恢复为自动默认值，可靠度学习统计保留。是否继续？",
                "删除知识策略",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            BackupFile(KnowledgePolicyPath(), "knowledge-policies-before-delete");
            foreach (var entry in selected) SaveDefaultPolicy(entry);
            bucket.Keys.Clear();
            RefreshPolicies(window, knowledge);
        }

        private static void ResetAllPolicies(KnowledgePolicyProfileWindow window)
        {
            var knowledge = GetField<List<KnowledgeBaseEntry>>(window, "_knowledge")
                ?? new List<KnowledgeBaseEntry>();
            if (MessageBox.Show(
                "确定清空全部知识策略配置并恢复自动默认值吗？可靠度学习统计保留。",
                "清空知识策略",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            BackupFile(KnowledgePolicyPath(), "knowledge-policies-before-clear");
            foreach (var entry in knowledge) SaveDefaultPolicy(entry);
            Selections.GetOrCreateValue(window).Keys.Clear();
            RefreshPolicies(window, knowledge);
        }

        private static void SaveDefaultPolicy(KnowledgeBaseEntry entry)
        {
            KnowledgePolicyProfileService.SaveProfile(entry, new KnowledgePolicyProfile
            {
                KnowledgeId = entry == null ? string.Empty : entry.Id,
                QuestionSnapshot = entry == null ? string.Empty : entry.Title,
                Intent = string.Empty,
                Entities = string.Empty,
                ApplyWhen = string.Empty,
                DoNotApplyWhen = string.Empty,
                RequiredContext = string.Empty,
                AnswerMode = KnowledgeAnswerModes.Auto,
                Confidence = 0.80
            });
        }

        private static void RefreshPolicies(
            KnowledgePolicyProfileWindow window,
            List<KnowledgeBaseEntry> knowledge)
        {
            SetField(
                window,
                "_profiles",
                KnowledgePolicyProfileService.GetProfilesForKnowledge(knowledge));
            Invoke(window, "ApplySearch");
        }

        private static void OnStoreLoaded(object sender, RoutedEventArgs e)
        {
            var window = sender as StorePromptProfileWindow;
            if (window == null || !MarkAttached(window)) return;
            window.Dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new Action(() => AttachStore(window)));
        }

        private static void AttachStore(StorePromptProfileWindow window)
        {
            var save = GetField<Button>(window, "_save");
            var panel = save == null ? null : save.Parent as Panel;
            if (panel == null) return;
            var index = panel.Children.IndexOf(save);
            if (index < 0) index = 0;
            InsertButton(panel, index, "规则列表管理", 108, (s, e) =>
            {
                var editor = new StoreRuleListWindow(window) { Owner = window };
                editor.ShowDialog();
            });
        }

        private static DataTemplate SelectionTemplate(object owner, Func<object, string> keySelector)
        {
            var template = new DataTemplate();
            var factory = new FrameworkElementFactory(typeof(CheckBox));
            factory.SetValue(CheckBox.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            factory.SetValue(CheckBox.VerticalAlignmentProperty, VerticalAlignment.Center);
            factory.AddHandler(FrameworkElement.LoadedEvent, new RoutedEventHandler((s, e) =>
            {
                var check = s as CheckBox;
                var key = check == null ? string.Empty : keySelector(check.DataContext);
                check.IsChecked = key.Length > 0
                    && Selections.GetOrCreateValue(owner).Keys.Contains(key);
            }));
            factory.AddHandler(CheckBox.ClickEvent, new RoutedEventHandler((s, e) =>
            {
                var check = s as CheckBox;
                var key = check == null ? string.Empty : keySelector(check.DataContext);
                if (key.Length == 0) return;
                var keys = Selections.GetOrCreateValue(owner).Keys;
                if (check.IsChecked == true) keys.Add(key);
                else keys.Remove(key);
                e.Handled = true;
            }));
            template.VisualTree = factory;
            return template;
        }

        private static void NormalizeKnowledge(List<KnowledgeBaseEntry> list)
        {
            foreach (var item in (list ?? new List<KnowledgeBaseEntry>()).Where(x => x != null))
            {
                if (string.IsNullOrWhiteSpace(item.Id)) item.Id = Guid.NewGuid().ToString("N");
                if (string.IsNullOrWhiteSpace(item.Category)) item.Category = "通用";
                if (string.IsNullOrWhiteSpace(item.CreatedAt))
                    item.CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                if (string.IsNullOrWhiteSpace(item.UpdatedAt))
                    item.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            }
        }

        private static KnowledgeBaseEntry FindKnowledge(
            IEnumerable<KnowledgeBaseEntry> current,
            KnowledgeBaseEntry incoming)
        {
            if (incoming == null) return null;
            var list = (current ?? Enumerable.Empty<KnowledgeBaseEntry>())
                .Where(x => x != null)
                .ToList();
            if (!string.IsNullOrWhiteSpace(incoming.Id))
            {
                var byId = list.FirstOrDefault(x => string.Equals(
                    x.Id,
                    incoming.Id,
                    StringComparison.Ordinal));
                if (byId != null) return byId;
            }
            var key = KnowledgeAiService.NormalizeQuestion(incoming.Title);
            return key.Length == 0
                ? null
                : list.FirstOrDefault(x => KnowledgeAiService.NormalizeQuestion(x.Title) == key);
        }

        private static void CopyKnowledge(KnowledgeBaseEntry source, KnowledgeBaseEntry target)
        {
            var created = target.CreatedAt;
            var id = target.Id;
            target.Enabled = source.Enabled;
            target.Category = source.Category;
            target.Title = source.Title;
            target.Keywords = source.Keywords;
            target.Answer = source.Answer;
            target.UpdatedAt = string.IsNullOrWhiteSpace(source.UpdatedAt)
                ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                : source.UpdatedAt;
            target.Id = string.IsNullOrWhiteSpace(source.Id) ? id : source.Id;
            target.CreatedAt = string.IsNullOrWhiteSpace(source.CreatedAt) ? created : source.CreatedAt;
            target.AiGenerated = source.AiGenerated;
            target.SourceType = source.SourceType;
        }

        private static string KnowledgeKey(KnowledgeBaseEntry item)
        {
            if (item == null) return string.Empty;
            if (!string.IsNullOrWhiteSpace(item.Id)) return "id:" + item.Id.Trim();
            return "q:" + KnowledgeAiService.NormalizeQuestion(item.Title)
                + "|" + Normalize(item.Answer);
        }

        private static string PolicyKey(KnowledgePolicyProfile item)
        {
            return item == null ? string.Empty : (item.KnowledgeId ?? string.Empty);
        }

        private static KnowledgeBaseEntry FindKnowledgeForPolicy(
            IEnumerable<KnowledgeBaseEntry> knowledge,
            string id,
            string question)
        {
            var list = (knowledge ?? Enumerable.Empty<KnowledgeBaseEntry>())
                .Where(x => x != null)
                .ToList();
            var byId = list.FirstOrDefault(x => !string.IsNullOrWhiteSpace(id)
                && string.Equals(
                    KnowledgePolicyProfileService.GetProfile(x).KnowledgeId,
                    id,
                    StringComparison.Ordinal));
            if (byId != null) return byId;
            var normalized = KnowledgeAiService.NormalizeQuestion(question);
            return normalized.Length == 0
                ? null
                : list.FirstOrDefault(x => KnowledgeAiService.NormalizeQuestion(x.Title) == normalized);
        }

        private static HashSet<string> ReadStoredPolicyIds()
        {
            try
            {
                var path = KnowledgePolicyPath();
                if (!File.Exists(path)) return new HashSet<string>(StringComparer.Ordinal);
                var root = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                var array = root["Profiles"] as JArray ?? root["profiles"] as JArray;
                return new HashSet<string>((array ?? new JArray())
                    .OfType<JObject>()
                    .Select(x => ReadString(x, "KnowledgeId", "knowledgeId"))
                    .Where(x => x.Length > 0), StringComparer.Ordinal);
            }
            catch
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }
        }

        private static string KnowledgePolicyPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QianniuAiBot",
                "data",
                "knowledge-policy-profile.json");
        }

        private static void BackupFile(string source, string prefix)
        {
            if (!File.Exists(source)) return;
            File.Copy(source, BackupPath(prefix), true);
        }

        private static void BackupJson(string prefix, JToken token)
        {
            File.WriteAllText(
                BackupPath(prefix),
                (token ?? new JArray()).ToString(Formatting.Indented),
                new UTF8Encoding(false));
        }

        private static string BackupPath(string prefix)
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QianniuAiBot",
                "data",
                "backups");
            Directory.CreateDirectory(directory);
            return Path.Combine(
                directory,
                prefix + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmssfff") + ".json");
        }

        private static string ReadString(JObject value, params string[] names)
        {
            if (value == null) return string.Empty;
            foreach (var name in names)
            {
                var token = value[name];
                if (token != null && token.Type != JTokenType.Null)
                    return Convert.ToString(token).Trim();
            }
            return string.Empty;
        }

        private static double ReadDouble(JObject value, double fallback, params string[] names)
        {
            double parsed;
            return double.TryParse(ReadString(value, names), out parsed) ? parsed : fallback;
        }

        private static string Normalize(string value)
        {
            return Regex.Replace(
                (value ?? string.Empty).Trim().ToLowerInvariant(),
                @"\s+",
                string.Empty);
        }

        private static void AddToolbarButton(
            Panel panel,
            string text,
            double width,
            RoutedEventHandler click)
        {
            var button = new Button
            {
                Content = text,
                Width = width,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 6)
            };
            button.Click += click;
            panel.Children.Add(button);
        }

        private static void InsertButton(
            Panel panel,
            int index,
            string text,
            double width,
            RoutedEventHandler click)
        {
            var button = new Button
            {
                Content = text,
                Width = width,
                Height = 30,
                Margin = new Thickness(0, 0, 8, 0)
            };
            button.Click += click;
            panel.Children.Insert(
                Math.Max(0, Math.Min(index, panel.Children.Count)),
                button);
        }

        private static T GetField<T>(object target, string name) where T : class
        {
            if (target == null) return null;
            var field = target.GetType().GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return field == null ? null : field.GetValue(target) as T;
        }

        private static void SetField(object target, string name, object value)
        {
            var field = target == null
                ? null
                : target.GetType().GetField(
                    name,
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field == null) throw new Exception("无法刷新窗口字段：" + name);
            field.SetValue(target, value);
        }

        private static object Invoke(object target, string name)
        {
            var method = target == null
                ? null
                : target.GetType().GetMethod(
                    name,
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (method == null) throw new Exception("无法调用窗口方法：" + name);
            return method.Invoke(target, null);
        }

        private static T FindFirst<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) return null;
            var direct = root as T;
            if (direct != null) return direct;
            var count = 0;
            try { count = VisualTreeHelper.GetChildrenCount(root); } catch { count = 0; }
            for (var i = 0; i < count; i++)
            {
                var found = FindFirst<T>(VisualTreeHelper.GetChild(root, i));
                if (found != null) return found;
            }
            return null;
        }

        private static Button FindButton(DependencyObject root, string content)
        {
            if (root == null) return null;
            var button = root as Button;
            if (button != null
                && string.Equals(Convert.ToString(button.Content), content, StringComparison.Ordinal))
                return button;
            var count = 0;
            try { count = VisualTreeHelper.GetChildrenCount(root); } catch { count = 0; }
            for (var i = 0; i < count; i++)
            {
                var found = FindButton(VisualTreeHelper.GetChild(root, i), content);
                if (found != null) return found;
            }
            return null;
        }
    }

    internal sealed class StoreRuleRow
    {
        public bool IsSelected { get; set; }
        public string Id { get; set; }
        public string Title { get; set; }
        public string Category { get; set; }
        public string Scope { get; set; }
        public int Priority { get; set; }
        public bool Enabled { get; set; }
        public string Triggers { get; set; }
        public string Content { get; set; }

        public static StoreRuleRow FromRule(StoreContextRule rule)
        {
            rule = rule ?? new StoreContextRule();
            return new StoreRuleRow
            {
                Id = rule.Id,
                Title = rule.Title,
                Category = rule.Category,
                Scope = rule.Scope,
                Priority = rule.Priority,
                Enabled = rule.Enabled,
                Triggers = string.Join("|", rule.Triggers ?? new List<string>()),
                Content = rule.Content
            };
        }

        public StoreContextRule ToRule()
        {
            return new StoreContextRule
            {
                Id = Id,
                Title = Title,
                Category = Category,
                Scope = Scope,
                Priority = Priority,
                Enabled = Enabled,
                Triggers = (Triggers ?? string.Empty)
                    .Split(new[] { '|', ',', '，', ';', '；', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(x => x.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Content = Content
            };
        }
    }

    internal sealed class StoreRuleListWindow : Window
    {
        private readonly StorePromptProfileWindow _ownerEditor;
        private readonly ObservableCollection<StoreRuleRow> _rows;
        private readonly DataGrid _grid;
        private readonly TextBlock _status;

        public StoreRuleListWindow(StorePromptProfileWindow ownerEditor)
        {
            _ownerEditor = ownerEditor;
            var rulesBox = GetField<TextBox>(ownerEditor, "_rules");
            var rules = StorePromptProfileService.ParseRulesJson(
                rulesBox == null ? "[]" : rulesBox.Text);
            _rows = new ObservableCollection<StoreRuleRow>(rules.Select(StoreRuleRow.FromRule));

            Title = "店铺场景规则列表管理";
            Width = 1240;
            Height = 700;
            MinWidth = 900;
            MinHeight = 540;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var root = new DockPanel { Margin = new Thickness(14) };
            Content = root;
            var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(toolbar, Dock.Top);
            root.Children.Add(toolbar);
            AddButton(toolbar, "新增", 72, (s, e) => Add());
            AddButton(toolbar, "全选", 72, (s, e) => SelectAll(true));
            AddButton(toolbar, "取消全选", 86, (s, e) => SelectAll(false));
            AddButton(toolbar, "删除所选", 86, (s, e) => Delete());
            AddButton(toolbar, "清空全部", 86, (s, e) => Clear());
            AddButton(toolbar, "导入", 72, (s, e) => Import());
            AddButton(toolbar, "导出", 72, (s, e) => Export());

            _status = new TextBlock
            {
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 0, 0, 8)
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
                ItemsSource = _rows,
                IsReadOnly = false,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            _grid.Columns.Add(new DataGridCheckBoxColumn { Header = "选择", Binding = TwoWay("IsSelected"), Width = 55 });
            _grid.Columns.Add(new DataGridCheckBoxColumn { Header = "启用", Binding = TwoWay("Enabled"), Width = 55 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "ID", Binding = TwoWay("Id"), Width = 130 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "标题", Binding = TwoWay("Title"), Width = 170 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "分类", Binding = TwoWay("Category"), Width = 120 });
            _grid.Columns.Add(new DataGridComboBoxColumn
            {
                Header = "范围",
                SelectedItemBinding = TwoWay("Scope"),
                ItemsSource = new[] { "text", "vision", "both" },
                Width = 85
            });
            _grid.Columns.Add(new DataGridTextColumn { Header = "优先级", Binding = TwoWay("Priority"), Width = 75 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "触发词", Binding = TwoWay("Triggers"), Width = 240 });
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = "规则内容",
                Binding = TwoWay("Content"),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star)
            });
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

        private void Add()
        {
            var row = new StoreRuleRow
            {
                Enabled = true,
                Scope = "both",
                Priority = 50,
                Id = Guid.NewGuid().ToString("N").Substring(0, 12)
            };
            _rows.Add(row);
            _grid.SelectedItem = row;
            UpdateStatus("已新增，尚未保存");
        }

        private void SelectAll(bool value)
        {
            Commit();
            foreach (var row in _rows) row.IsSelected = value;
            _grid.Items.Refresh();
            UpdateStatus();
        }

        private void Delete()
        {
            Commit();
            var selected = _rows.Where(x => x.IsSelected).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("请先勾选规则。", "场景规则", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show(
                "确定删除已勾选的 " + selected.Count + " 条场景规则吗？",
                "删除规则",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            foreach (var row in selected) _rows.Remove(row);
            UpdateStatus("已删除，尚未保存");
        }

        private void Clear()
        {
            if (_rows.Count == 0) return;
            if (MessageBox.Show(
                "确定清空全部场景规则吗？",
                "清空规则",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _rows.Clear();
            UpdateStatus("已清空，尚未保存");
        }

        private void Import()
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Title = "导入店铺场景规则",
                    Filter = "JSON文件 (*.json)|*.json"
                };
                if (dialog.ShowDialog(this) != true) return;
                var token = JToken.Parse(File.ReadAllText(dialog.FileName, Encoding.UTF8));
                var array = token as JArray;
                var root = token as JObject;
                if (array == null && root != null)
                {
                    array = root["rules"] as JArray;
                    var profile = root["profile"] as JObject;
                    if (array == null && profile != null) array = profile["rules"] as JArray;
                    if (array == null) array = root["Rules"] as JArray;
                }
                if (array == null) throw new Exception("文件中没有 rules 规则数组。");

                var incoming = StorePromptProfileService.ParseRulesJson(
                        array.ToString(Formatting.None))
                    .Select(StoreRuleRow.FromRule)
                    .ToList();
                var mode = BulkImportModeWindow.Choose(this, "店铺场景规则", incoming.Count);
                if (mode == BulkImportMode.Cancel) return;

                var result = mode == BulkImportMode.Replace
                    ? new List<StoreRuleRow>()
                    : _rows.Select(Clone).ToList();
                foreach (var row in incoming)
                {
                    var existing = result.FirstOrDefault(x => !string.IsNullOrWhiteSpace(row.Id)
                            && string.Equals(x.Id, row.Id, StringComparison.OrdinalIgnoreCase))
                        ?? result.FirstOrDefault(x => string.Equals(
                            (x.Title ?? string.Empty).Trim(),
                            (row.Title ?? string.Empty).Trim(),
                            StringComparison.OrdinalIgnoreCase));
                    if (existing == null) result.Add(Clone(row));
                    else if (mode == BulkImportMode.Merge) Copy(row, existing);
                }

                _rows.Clear();
                foreach (var row in result) _rows.Add(row);
                UpdateStatus("已导入，尚未保存");
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "导入场景规则失败：" + ex.Message,
                    "场景规则",
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
                    Title = "导出店铺场景规则",
                    FileName = "qianniu-store-context-rules.json",
                    Filter = "JSON文件 (*.json)|*.json"
                };
                if (dialog.ShowDialog(this) != true) return;
                File.WriteAllText(
                    dialog.FileName,
                    StorePromptProfileService.SerializeRules(_rows.Select(x => x.ToRule()).ToList()),
                    new UTF8Encoding(false));
                UpdateStatus("已导出");
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "导出场景规则失败：" + ex.Message,
                    "场景规则",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void Save(bool close)
        {
            try
            {
                Commit();
                var raw = GetField<TextBox>(_ownerEditor, "_raw");
                var core = GetField<TextBox>(_ownerEditor, "_core");
                var rulesBox = GetField<TextBox>(_ownerEditor, "_rules");
                var rules = _rows.Select(x => x.ToRule()).ToList();
                StorePromptProfileService.SaveStructured(
                    raw == null ? string.Empty : raw.Text,
                    core == null ? string.Empty : core.Text,
                    rules);
                if (rulesBox != null)
                    rulesBox.Text = StorePromptProfileService.SerializeRules(rules);
                Invoke(_ownerEditor, "LoadProfile");
                UpdateStatus("已保存");
                if (close) Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "保存场景规则失败：" + ex.Message,
                    "场景规则",
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
                + "共 " + _rows.Count + " 条，启用 " + _rows.Count(x => x.Enabled)
                + " 条，已勾选 " + _rows.Count(x => x.IsSelected) + " 条";
        }

        private static StoreRuleRow Clone(StoreRuleRow source)
        {
            var target = new StoreRuleRow();
            Copy(source, target);
            return target;
        }

        private static void Copy(StoreRuleRow source, StoreRuleRow target)
        {
            target.Id = source.Id;
            target.Title = source.Title;
            target.Category = source.Category;
            target.Scope = source.Scope;
            target.Priority = source.Priority;
            target.Enabled = source.Enabled;
            target.Triggers = source.Triggers;
            target.Content = source.Content;
            target.IsSelected = false;
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

        private static T GetField<T>(object target, string name) where T : class
        {
            var field = target == null
                ? null
                : target.GetType().GetField(
                    name,
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return field == null ? null : field.GetValue(target) as T;
        }

        private static object Invoke(object target, string name)
        {
            var method = target == null
                ? null
                : target.GetType().GetMethod(
                    name,
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return method == null ? null : method.Invoke(target, null);
        }
    }
}
