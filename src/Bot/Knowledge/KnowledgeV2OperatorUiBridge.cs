using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bot.Knowledge
{
    /// <summary>
    /// Operator-facing Knowledge V2 additions: one-sentence AI entry creation and the restored
    /// V1 history-chat organizer. Manual structured editing remains available for existing rows,
    /// but new rows are created through AI so operators no longer have to fill every V2 field.
    /// </summary>
    internal static class KnowledgeV2OperatorUiBridge
    {
        private static readonly ConditionalWeakTable<KnowledgeV2RecordsPage, object> Pages = new ConditionalWeakTable<KnowledgeV2RecordsPage, object>();
        private static readonly ConditionalWeakTable<KnowledgeCenterWindow, object> Windows = new ConditionalWeakTable<KnowledgeCenterWindow, object>();
        private static int _initialized;

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            EventManager.RegisterClassHandler(typeof(KnowledgeV2RecordsPage), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnRecordsPageLoaded), true);
            EventManager.RegisterClassHandler(typeof(KnowledgeCenterWindow), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnKnowledgeWindowLoaded), true);
        }

        private static void OnRecordsPageLoaded(object sender, RoutedEventArgs e)
        {
            var page = sender as KnowledgeV2RecordsPage; if (page == null) return;
            object marker; if (Pages.TryGetValue(page, out marker)) return;
            try { Pages.Add(page, new object()); } catch { return; }
            page.Dispatcher.BeginInvoke(new Action(() => InjectAiCreate(page)));
        }

        private static void InjectAiCreate(KnowledgeV2RecordsPage page)
        {
            var toolbar = FindToolbar(page);
            if (toolbar == null) return;
            var legacyAdd = toolbar.Children.OfType<Button>().FirstOrDefault(x => string.Equals(Convert.ToString(x.Content), "新增知识", StringComparison.Ordinal));
            if (legacyAdd == null) return;
            legacyAdd.Visibility = Visibility.Collapsed;

            var input = new TextBox
            {
                Width = 330, Height = 30, Margin = new Thickness(0, 0, 8, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "只需输入一句业务知识，例如：电视端会员支持酷狗TV登录，购买后在电视端使用手机号登录即可。AI会自动生成标题、类型、Intent、Subject、Predicate、实体、同义问法、标准答案、条件、风险和可信度等字段。"
            };
            var create = new Button { Content = "AI一句话新增", Width = 112, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
            var index = toolbar.Children.IndexOf(legacyAdd);
            toolbar.Children.Insert(index, input);
            toolbar.Children.Insert(index + 1, create);

            create.Click += async delegate
            {
                var sentence = (input.Text ?? string.Empty).Trim();
                if (sentence.Length == 0)
                {
                    MessageBox.Show(Window.GetWindow(page), "请输入一句要新增的知识。", "AI一句话新增", MessageBoxButton.OK, MessageBoxImage.Information);
                    input.Focus(); return;
                }
                var seller = KnowledgeCenterV2Context.ResolveSeller(Window.GetWindow(page));
                if (string.IsNullOrWhiteSpace(seller))
                {
                    MessageBox.Show(Window.GetWindow(page), "未识别当前店铺，无法新增知识。", "AI一句话新增", MessageBoxButton.OK, MessageBoxImage.Warning); return;
                }
                create.IsEnabled = false; input.IsEnabled = false; create.Content = "AI生成中...";
                try
                {
                    var mode = ResolveMode(page);
                    var record = await KnowledgeV2NaturalLanguageService.GenerateAsync(sentence, mode, CancellationToken.None);
                    await Task.Run(() =>
                    {
                        KnowledgeEngineV2Repository.Save(seller, record);
                        KnowledgeEngineV2Service.Warm(seller);
                    });
                    input.Clear(); page.RefreshView();
                    MessageBox.Show(Window.GetWindow(page), "已由 AI 自动生成全部结构化字段并加入 Knowledge Center V2。\n\n标题：" + record.Title + "\n类型：" + record.Type + "\n标准答案：" + record.Answer, "新增完成", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(Window.GetWindow(page), "AI 新增失败：" + ex.Message + "\n\n本次没有写入知识库。", "AI一句话新增", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                finally { create.Content = "AI一句话新增"; create.IsEnabled = true; input.IsEnabled = true; }
            };
        }

        private static KnowledgeV2RecordsPageMode ResolveMode(KnowledgeV2RecordsPage page)
        {
            var field = typeof(KnowledgeV2RecordsPage).GetField("_mode", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return field == null ? KnowledgeV2RecordsPageMode.All : (KnowledgeV2RecordsPageMode)field.GetValue(page);
        }

        private static void OnKnowledgeWindowLoaded(object sender, RoutedEventArgs e)
        {
            var window = sender as KnowledgeCenterWindow; if (window == null) return;
            object marker; if (Windows.TryGetValue(window, out marker)) return;
            try { Windows.Add(window, new object()); } catch { return; }
            window.Dispatcher.BeginInvoke(new Action(() => InjectHistoryButton(window)));
        }

        private static void InjectHistoryButton(KnowledgeCenterWindow window)
        {
            var toolbar = FindHeaderToolbar(window.Content as DependencyObject); if (toolbar == null) return;
            if (toolbar.Children.OfType<Button>().Any(x => string.Equals(Convert.ToString(x.Content), "历史聊天整理", StringComparison.Ordinal))) return;
            var button = new Button { Content = "历史聊天整理", Width = 108, Height = 30, Margin = new Thickness(0, 0, 8, 0), ToolTip = "恢复知识中心V1的历史聊天自动读取与AI整理功能；新生成知识会同步进入Knowledge Center V2。" };
            button.Click += delegate
            {
                var seller = KnowledgeCenterV2Context.ResolveSeller(window);
                var scan = new ChatHistoryScanWindow { Owner = window };
                scan.ShowDialog();
                try
                {
                    var added = KnowledgeV2LegacyDeltaImportService.ImportMissingHistoryKnowledge(seller);
                    if (added > 0) MessageBox.Show(window, "历史聊天整理新增的 " + added + " 条知识已同步到 Knowledge Center V2。", "历史聊天整理", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(window, "历史聊天已扫描，但同步到 V2 时失败：" + ex.Message, "历史聊天整理", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            var helpIndex = toolbar.Children.OfType<Button>().Select((x, i) => new { Button = x, Index = i }).FirstOrDefault(x => string.Equals(Convert.ToString(x.Button.Content), "使用帮助", StringComparison.Ordinal));
            toolbar.Children.Insert(helpIndex == null ? toolbar.Children.Count : helpIndex.Index, button);
        }

        private static WrapPanel FindToolbar(DependencyObject root)
        {
            if (root == null) return null;
            var wrap = root as WrapPanel;
            if (wrap != null && wrap.Children.OfType<Button>().Any(x => string.Equals(Convert.ToString(x.Content), "新增知识", StringComparison.Ordinal))) return wrap;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var found = FindToolbar(VisualTreeHelper.GetChild(root, i)); if (found != null) return found; }
            return null;
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
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var found = FindHeaderToolbar(VisualTreeHelper.GetChild(root, i)); if (found != null) return found; }
            return null;
        }
    }
}
