using Bot.Options;
using BotLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bot
{
    public partial class App
    {
        private readonly object _handoffSettingsUiBootstrap =
            UpdateNs.HandoffSettingsUiBridge.InitializeForApp();
    }
}

namespace Bot.UpdateNs
{
    /// <summary>
    /// Keeps the legacy settings storage intact while moving the handoff controls to the page
    /// where handoff notifications live. The underlying legacy page key remains discoverable so
    /// old navigation commands that still say “消息通知” continue to work.
    /// </summary>
    internal static class HandoffSettingsUiBridge
    {
        private static bool _initialized;

        public static object InitializeForApp()
        {
            if (_initialized) return new object();
            _initialized = true;
            EventManager.RegisterClassHandler(
                typeof(FeatureSettingsOptionsControl),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnFeatureSettingsLoaded),
                true);
            return new object();
        }

        private static void OnFeatureSettingsLoaded(object sender, RoutedEventArgs e)
        {
            var control = sender as FeatureSettingsOptionsControl;
            if (control == null) return;
            try
            {
                MoveHandoffRuleControls(control);
                RenameVisibleNotificationLabels();
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("迁移转人工策略设置界面失败：" + ex.Message, 10);
            }
        }

        private static void MoveHandoffRuleControls(FeatureSettingsOptionsControl control)
        {
            var type = typeof(FeatureSettingsOptionsControl);
            var legacyField = type.GetField("_legacyWindow", BindingFlags.Instance | BindingFlags.NonPublic);
            var tabsField = type.GetField("_tabs", BindingFlags.Instance | BindingFlags.NonPublic);
            var legacy = legacyField == null ? null : legacyField.GetValue(control);
            var tabs = tabsField == null ? null : tabsField.GetValue(control) as TabControl;
            if (legacy == null || tabs == null) return;

            var notification = tabs.Items.OfType<TabItem>().FirstOrDefault(x =>
            {
                var header = Convert.ToString(x.Header) ?? string.Empty;
                return header.Contains("消息通知") || header.Contains("转人工策略");
            });
            var rules = tabs.Items.OfType<TabItem>().FirstOrDefault(x =>
                string.Equals(Convert.ToString(x.Header), "自动回复规则", StringComparison.Ordinal));
            if (notification == null || rules == null) return;

            // Keep the old key in the hidden tab header for backwards-compatible NavigateTo,
            // but make the new page name the visible/primary one.
            notification.Header = "转人工策略｜消息通知";

            var legacyType = legacy.GetType();
            var ruleEnabled = GetField<CheckBox>(legacyType, legacy, "_rulesEnabled");
            var manualKeywords = GetField<TextBox>(legacyType, legacy, "_manualKeywords");
            var noAutoKeywords = GetField<TextBox>(legacyType, legacy, "_noAutoKeywords");
            var handoffText = GetField<TextBox>(legacyType, legacy, "_handoffText");
            if (ruleEnabled == null) return;

            var destination = FindLargestStack(notification.Content as DependencyObject);
            if (destination == null) return;

            var moving = new List<UIElement>();
            AddDetachableContainer(rules.Content as DependencyObject, ruleEnabled, moving);
            AddDetachableContainer(rules.Content as DependencyObject, manualKeywords, moving);
            AddDetachableContainer(rules.Content as DependencyObject, noAutoKeywords, moving);
            AddDetachableContainer(rules.Content as DependencyObject, handoffText, moving);
            if (moving.Count == 0) return;

            var section = new TextBlock
            {
                Text = "转人工规则",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55)),
                Margin = new Thickness(0, 6, 0, 10)
            };
            var separator = new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                Margin = new Thickness(0, 8, 0, 14)
            };

            destination.Children.Insert(0, separator);
            for (var i = moving.Count - 1; i >= 0; i--)
            {
                destination.Children.Insert(0, moving[i]);
            }
            destination.Children.Insert(0, section);
            Log.Info("设置界面已将“启用转人工规则”及其关键词/话术移动到“转人工策略”。");
        }

        private static T GetField<T>(Type type, object target, string name) where T : class
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            return field == null ? null : field.GetValue(target) as T;
        }

        private static void AddDetachableContainer(
            DependencyObject root,
            DependencyObject target,
            IList<UIElement> output)
        {
            if (root == null || target == null) return;
            Panel parent;
            UIElement directChild;
            if (!FindDirectPanelChild(root, target, out parent, out directChild)
                || parent == null
                || directChild == null)
            {
                return;
            }
            parent.Children.Remove(directChild);
            if (!output.Contains(directChild)) output.Add(directChild);
        }

        private static bool FindDirectPanelChild(
            DependencyObject root,
            DependencyObject target,
            out Panel parent,
            out UIElement directChild)
        {
            parent = null;
            directChild = null;
            var panel = root as Panel;
            if (panel != null)
            {
                foreach (UIElement child in panel.Children)
                {
                    if (ReferenceEquals(child, target) || Contains(child, target))
                    {
                        parent = panel;
                        directChild = child;
                        return true;
                    }
                }
            }

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                if (FindDirectPanelChild(
                    VisualTreeHelper.GetChild(root, i),
                    target,
                    out parent,
                    out directChild)) return true;
            }
            return false;
        }

        private static bool Contains(DependencyObject root, DependencyObject target)
        {
            if (root == null || target == null) return false;
            if (ReferenceEquals(root, target)) return true;
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                if (Contains(VisualTreeHelper.GetChild(root, i), target)) return true;
            }
            return false;
        }

        private static StackPanel FindLargestStack(DependencyObject root)
        {
            StackPanel best = root as StackPanel;
            var bestCount = best == null ? -1 : best.Children.Count;
            if (root == null) return best;
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var candidate = FindLargestStack(VisualTreeHelper.GetChild(root, i));
                if (candidate != null && candidate.Children.Count > bestCount)
                {
                    best = candidate;
                    bestCount = candidate.Children.Count;
                }
            }
            return best;
        }

        private static void RenameVisibleNotificationLabels()
        {
            if (Application.Current == null) return;
            foreach (Window window in Application.Current.Windows)
            {
                ReplaceExactText(window, "消息通知", "转人工策略");
            }
        }

        private static void ReplaceExactText(DependencyObject root, string oldText, string newText)
        {
            if (root == null) return;
            var tb = root as TextBlock;
            if (tb != null && string.Equals((tb.Text ?? string.Empty).Trim(), oldText, StringComparison.Ordinal))
            {
                tb.Text = newText;
            }
            var button = root as Button;
            if (button != null && string.Equals(Convert.ToString(button.Content), oldText, StringComparison.Ordinal))
            {
                button.Content = newText;
            }
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                ReplaceExactText(VisualTreeHelper.GetChild(root, i), oldText, newText);
            }
        }
    }
}
