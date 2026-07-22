using BotLib;
using System;
using System.Collections;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Bot.Options
{
    public static class OrderPlacedReplyDelaySettings
    {
        private const string Scope = "feature";
        private const string DelayKey = "OrderPlacedReplyDelaySeconds";
        private const int DefaultDelaySeconds = 1;
        private const int MaxDelaySeconds = 300;
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

        public static int GetSeconds()
        {
            var raw = BotLib.Db.Sqlite.PersistentParams.GetParam2Key(
                DelayKey,
                Scope,
                DefaultDelaySeconds.ToString());
            int seconds;
            if (!int.TryParse(raw, out seconds)) seconds = DefaultDelaySeconds;
            return Clamp(seconds);
        }

        public static void SaveSeconds(int seconds)
        {
            BotLib.Db.Sqlite.PersistentParams.TrySaveParam2Key(
                DelayKey,
                Scope,
                Clamp(seconds).ToString());
        }

        public static int Clamp(int seconds)
        {
            return Math.Max(0, Math.Min(MaxDelaySeconds, seconds));
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
                    Text = "延时发送（秒）",
                    Width = 90,
                    VerticalAlignment = VerticalAlignment.Center
                });
                row.Children.Add(new TextBox
                {
                    Text = GetSeconds().ToString(),
                    Width = 70,
                    Height = 26,
                    Tag = DelayTextBoxTag,
                    ToolTip = "买家下单后等待多少秒再发送；0 表示立即发送，默认 1 秒，最大 300 秒。"
                });
                row.Children.Add(new TextBlock
                {
                    Text = "0=立即发送，默认 1 秒",
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
                Log.ErrorWithMaxCount("初始化下单自动回复延时设置失败：" + ex.Message, 10);
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
                int seconds;
                if (!int.TryParse((box.Text ?? string.Empty).Trim(), out seconds)) seconds = DefaultDelaySeconds;
                seconds = Clamp(seconds);
                box.Text = seconds.ToString();
                SaveSeconds(seconds);
                Log.Info("下单自动回复延时设置已保存: delaySeconds=" + seconds);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("保存下单自动回复延时设置失败：" + ex.Message, 10);
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
