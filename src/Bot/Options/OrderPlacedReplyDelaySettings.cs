using BotLib;
using System;
using System.Collections;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Bot.Options
{
    /// <summary>
    /// 下单后的预设固定答案属于交易流程引导，必须优先于买家随后发来的普通文本/图片消息进入发送队列。
    /// 旧版本允许配置 0-300 秒延时；该延时会让 Smart Reply 在固定答案之前先回复，造成流程顺序错误。
    /// 现在统一强制为 0 秒，旧 params.db 中已保存的延时值会被忽略并在下次保存设置时归零。
    /// </summary>
    public static class OrderPlacedReplyDelaySettings
    {
        private const string Scope = "feature";
        private const string DelayKey = "OrderPlacedReplyDelaySeconds";
        private const int ForcedDelaySeconds = 0;
        private const string DelayTextBoxTag = "OrderPlacedReplyDelaySecondsTextBox";
        private static bool _initialized;

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            EventManager.RegisterClassHandler(
                typeof(FeatureSettingsWindow),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnFeatureSettingsLoaded),
                true);
            EventManager.RegisterClassHandler(
                typeof(FeatureSettingsWindow),
                Button.ClickEvent,
                new RoutedEventHandler(OnFeatureSettingsButtonClick),
                true);
        }

        /// <summary>
        /// 强制立即发送。不要恢复读取旧延时值，否则直接订单事件桥接与普通买家消息流水线会并发，
        /// Smart Reply 可能先于下单固定答案取得发送机会。
        /// </summary>
        public static int GetSeconds()
        {
            return ForcedDelaySeconds;
        }

        public static void SaveSeconds(int seconds)
        {
            BotLib.Db.Sqlite.PersistentParams.TrySaveParam2Key(
                DelayKey,
                Scope,
                ForcedDelaySeconds.ToString());
        }

        public static int Clamp(int seconds)
        {
            return ForcedDelaySeconds;
        }

        private static void OnFeatureSettingsLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var window = sender as FeatureSettingsWindow;
                if (window == null) return;
                var panel = FindOrderPlacedSectionPanel(window);
                if (panel == null || FindDelayTextBox(window) != null) return;

                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 0, 0, 8),
                    Tag = "OrderPlacedReplyDelayRow"
                };
                row.Children.Add(new TextBlock
                {
                    Text = "发送优先级",
                    Width = 90,
                    VerticalAlignment = VerticalAlignment.Center
                });
                row.Children.Add(new TextBox
                {
                    Text = ForcedDelaySeconds.ToString(),
                    Width = 70,
                    Height = 26,
                    IsReadOnly = true,
                    IsEnabled = false,
                    Tag = DelayTextBoxTag,
                    ToolTip = "下单后的预设固定答案已强制立即发送，旧版本保存的延时值不再生效。"
                });
                row.Children.Add(new TextBlock
                {
                    Text = "强制立即发送（0 秒），优先于后续普通 AI 回复",
                    Margin = new Thickness(12, 4, 0, 0),
                    Foreground = System.Windows.Media.Brushes.Gray
                });

                var insertIndex = panel.Children.Count;
                if (panel.Children.Count > 0)
                {
                    var last = panel.Children[panel.Children.Count - 1] as TextBlock;
                    if (last != null && (last.Text ?? string.Empty).Contains("当前仅在 Bot 运行期间"))
                    {
                        insertIndex = panel.Children.Count - 1;
                    }
                }
                panel.Children.Insert(insertIndex, row);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("初始化下单固定答案优先级设置失败：" + ex.Message, 10);
            }
        }

        private static void OnFeatureSettingsButtonClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var button = e.OriginalSource as Button;
                if (button == null || !string.Equals(Convert.ToString(button.Content), "保存全部", StringComparison.Ordinal)) return;
                var window = sender as FeatureSettingsWindow;
                var box = window == null ? null : FindDelayTextBox(window);
                if (box == null) return;
                box.Text = ForcedDelaySeconds.ToString();
                SaveSeconds(ForcedDelaySeconds);
                Log.Info("下单固定答案发送优先级已保存: delaySeconds=0, mode=forced-immediate");
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("保存下单固定答案优先级设置失败：" + ex.Message, 10);
            }
        }

        private static StackPanel FindOrderPlacedSectionPanel(DependencyObject root)
        {
            foreach (var child in LogicalChildren(root))
            {
                var panel = child as StackPanel;
                if (panel != null && panel.Children.OfType<TextBlock>().Any(x =>
                    string.Equals((x.Text ?? string.Empty).Trim(), "买家下单后自动发送", StringComparison.Ordinal)))
                {
                    return panel;
                }
                var nested = FindOrderPlacedSectionPanel(child);
                if (nested != null) return nested;
            }
            return null;
        }

        private static TextBox FindDelayTextBox(DependencyObject root)
        {
            foreach (var child in LogicalChildren(root))
            {
                var box = child as TextBox;
                if (box != null && string.Equals(Convert.ToString(box.Tag), DelayTextBoxTag, StringComparison.Ordinal)) return box;
                var nested = FindDelayTextBox(child);
                if (nested != null) return nested;
            }
            return null;
        }

        private static DependencyObject[] LogicalChildren(DependencyObject root)
        {
            if (root == null) return new DependencyObject[0];
            try
            {
                return LogicalTreeHelper.GetChildren(root)
                    .Cast<object>()
                    .OfType<DependencyObject>()
                    .ToArray();
            }
            catch
            {
                return new DependencyObject[0];
            }
        }
    }
}
