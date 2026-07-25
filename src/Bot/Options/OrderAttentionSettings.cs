using BotLib;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Bot.Options
{
    internal static class OrderAttentionSettings
    {
        private const string Scope = "feature";
        private const string EnabledKey = "EnableNewOrderAutoFocus";
        private const string HumanProtectionKey = "NewOrderHumanProtectionSeconds";
        private const string SwitchIntervalKey = "NewOrderAutoFocusIntervalSeconds";
        private const bool DefaultEnabled = true;
        private const int DefaultHumanProtectionSeconds = 12;
        private const int DefaultSwitchIntervalSeconds = 5;
        private const string EnabledTag = "EnableNewOrderAutoFocusCheckBox";
        private const string HumanTag = "NewOrderHumanProtectionSecondsTextBox";
        private const string IntervalTag = "NewOrderAutoFocusIntervalSecondsTextBox";
        private static bool _initialized;

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            EventManager.RegisterClassHandler(
                typeof(FeatureSettingsWindow),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnLoaded),
                true);
            EventManager.RegisterClassHandler(
                typeof(FeatureSettingsWindow),
                Button.ClickEvent,
                new RoutedEventHandler(OnButtonClick),
                true);
        }

        public static bool IsEnabled()
        {
            var raw = BotLib.Db.Sqlite.PersistentParams.GetParam2Key(
                EnabledKey,
                Scope,
                DefaultEnabled ? "1" : "0");
            return string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
        }

        public static int GetHumanProtectionSeconds()
        {
            return ReadInt(HumanProtectionKey, DefaultHumanProtectionSeconds, 3, 120);
        }

        public static int GetSwitchIntervalSeconds()
        {
            return ReadInt(SwitchIntervalKey, DefaultSwitchIntervalSeconds, 2, 60);
        }

        private static void Save(bool enabled, int humanSeconds, int intervalSeconds)
        {
            BotLib.Db.Sqlite.PersistentParams.TrySaveParam2Key(EnabledKey, Scope, enabled ? "1" : "0");
            BotLib.Db.Sqlite.PersistentParams.TrySaveParam2Key(
                HumanProtectionKey,
                Scope,
                Clamp(humanSeconds, 3, 120).ToString());
            BotLib.Db.Sqlite.PersistentParams.TrySaveParam2Key(
                SwitchIntervalKey,
                Scope,
                Clamp(intervalSeconds, 2, 60).ToString());
        }

        private static int ReadInt(string key, int fallback, int min, int max)
        {
            var raw = BotLib.Db.Sqlite.PersistentParams.GetParam2Key(key, Scope, fallback.ToString());
            int value;
            if (!int.TryParse(raw, out value)) value = fallback;
            return Clamp(value, min, max);
        }

        private static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var window = sender as FeatureSettingsWindow;
                if (window == null) return;
                var panel = FindOrderPlacedSectionPanel(window);
                if (panel == null || FindByTag<CheckBox>(window, EnabledTag) != null) return;

                var border = new Border
                {
                    BorderBrush = System.Windows.Media.Brushes.LightGray,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(10),
                    Margin = new Thickness(0, 4, 0, 8),
                    Tag = "NewOrderAttentionSettingsPanel"
                };
                var body = new StackPanel();
                body.Children.Add(new TextBlock
                {
                    Text = "新订单识别与空闲自动切换",
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 0, 0, 8)
                });
                body.Children.Add(new CheckBox
                {
                    Content = "当前无任务时自动切换到新下单买家",
                    IsChecked = IsEnabled(),
                    Tag = EnabledTag,
                    Margin = new Thickness(0, 0, 0, 8),
                    ToolTip = "发现新订单后先进入待处理队列；只有Bot无回复/视觉/发送任务、输入框为空且人工保护期已结束时才切换。"
                });

                var humanRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                humanRow.Children.Add(new TextBlock
                {
                    Text = "人工操作保护（秒）",
                    Width = 125,
                    VerticalAlignment = VerticalAlignment.Center
                });
                humanRow.Children.Add(new TextBox
                {
                    Text = GetHumanProtectionSeconds().ToString(),
                    Width = 60,
                    Height = 26,
                    Tag = HumanTag,
                    ToolTip = "检测到客服输入、发送消息或手动切换后，多少秒内不自动切走窗口。范围 3-120 秒。"
                });
                humanRow.Children.Add(new TextBlock
                {
                    Text = "默认12秒",
                    Foreground = System.Windows.Media.Brushes.Gray,
                    Margin = new Thickness(10, 4, 0, 0)
                });
                body.Children.Add(humanRow);

                var intervalRow = new StackPanel { Orientation = Orientation.Horizontal };
                intervalRow.Children.Add(new TextBlock
                {
                    Text = "最短切换间隔（秒）",
                    Width = 125,
                    VerticalAlignment = VerticalAlignment.Center
                });
                intervalRow.Children.Add(new TextBox
                {
                    Text = GetSwitchIntervalSeconds().ToString(),
                    Width = 60,
                    Height = 26,
                    Tag = IntervalTag,
                    ToolTip = "多个订单同时到达时，两次自动切换之间的最短间隔。范围 2-60 秒。"
                });
                intervalRow.Children.Add(new TextBlock
                {
                    Text = "默认5秒",
                    Foreground = System.Windows.Media.Brushes.Gray,
                    Margin = new Thickness(10, 4, 0, 0)
                });
                body.Children.Add(intervalRow);
                border.Child = body;

                var insertIndex = panel.Children.Count;
                var delayRow = panel.Children
                    .Cast<UIElement>()
                    .Select((x, index) => new { Element = x, Index = index })
                    .FirstOrDefault(x => string.Equals(Convert.ToString((x.Element as FrameworkElement)?.Tag), "OrderPlacedReplyDelayRow", StringComparison.Ordinal));
                if (delayRow != null) insertIndex = delayRow.Index + 1;
                panel.Children.Insert(Math.Min(insertIndex, panel.Children.Count), border);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("初始化新订单自动切换设置失败：" + ex.Message, 10);
            }
        }

        private static void OnButtonClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var button = e.OriginalSource as Button;
                if (button == null || !string.Equals(Convert.ToString(button.Content), "保存全部", StringComparison.Ordinal)) return;
                var window = sender as FeatureSettingsWindow;
                if (window == null) return;
                var enabled = FindByTag<CheckBox>(window, EnabledTag);
                var human = FindByTag<TextBox>(window, HumanTag);
                var interval = FindByTag<TextBox>(window, IntervalTag);
                if (enabled == null || human == null || interval == null) return;

                int humanSeconds;
                int intervalSeconds;
                if (!int.TryParse((human.Text ?? string.Empty).Trim(), out humanSeconds)) humanSeconds = DefaultHumanProtectionSeconds;
                if (!int.TryParse((interval.Text ?? string.Empty).Trim(), out intervalSeconds)) intervalSeconds = DefaultSwitchIntervalSeconds;
                humanSeconds = Clamp(humanSeconds, 3, 120);
                intervalSeconds = Clamp(intervalSeconds, 2, 60);
                human.Text = humanSeconds.ToString();
                interval.Text = intervalSeconds.ToString();
                Save(enabled.IsChecked == true, humanSeconds, intervalSeconds);
                Log.Info("新订单自动切换设置已保存: enabled=" + (enabled.IsChecked == true)
                    + ", humanProtectionSeconds=" + humanSeconds
                    + ", switchIntervalSeconds=" + intervalSeconds);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("保存新订单自动切换设置失败：" + ex.Message, 10);
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

        private static T FindByTag<T>(DependencyObject root, string tag) where T : FrameworkElement
        {
            foreach (var child in LogicalChildren(root))
            {
                var typed = child as T;
                if (typed != null && string.Equals(Convert.ToString(typed.Tag), tag, StringComparison.Ordinal)) return typed;
                var nested = FindByTag<T>(child, tag);
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
