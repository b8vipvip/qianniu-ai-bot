using Bot.Options;
using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bot
{
    public partial class App
    {
        private readonly object _botUpdateSettingsUiBootstrap =
            UpdateNs.BotUpdateSettingsUi.InitializeForApp();
    }
}

namespace Bot.UpdateNs
{
    internal static class BotUpdateSettingsUi
    {
        private static bool _initialized;

        public static object InitializeForApp()
        {
            if (_initialized) return new object();
            _initialized = true;
            EventManager.RegisterClassHandler(
                typeof(BotUpdateOptionsControl),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnLoaded),
                true);
            return new object();
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            var control = sender as BotUpdateOptionsControl;
            if (control == null) return;
            try
            {
                var type = typeof(BotUpdateOptionsControl);
                var autoCheck = GetField<CheckBox>(type, control, "_autoCheck");
                if (autoCheck != null)
                {
                    autoCheck.Content = "接收服务端新版本通知（客户端不主动检查版本）";
                }
                var interval = GetField<ComboBox>(type, control, "_interval");
                if (interval != null)
                {
                    var row = interval.Parent as UIElement;
                    if (row != null) row.Visibility = Visibility.Collapsed;
                }
                var status = GetField<TextBlock>(type, control, "_status");
                if (status != null && BotUpdateService.LastResult == null)
                {
                    status.Text = "等待服务端主动下发版本通知；客户端不会后台轮询版本。仍可手动检查。";
                }
                ReplaceText(control, "自动检查与更新设置", "服务端通知与自动更新设置");
            }
            catch { }
        }

        private static T GetField<T>(Type type, object target, string name) where T : class
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            return field == null ? null : field.GetValue(target) as T;
        }

        private static void ReplaceText(DependencyObject root, string oldText, string newText)
        {
            if (root == null) return;
            var text = root as TextBlock;
            if (text != null && string.Equals(text.Text, oldText, StringComparison.Ordinal))
                text.Text = newText;
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
                ReplaceText(VisualTreeHelper.GetChild(root, i), oldText, newText);
        }
    }
}
