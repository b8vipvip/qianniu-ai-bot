using Bot.AssistWindow;
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace Bot
{
    public partial class App
    {
        private readonly object _shopScopedUiBridgeBootstrap =
            ShopScope.ShopScopedUiBridge.InitializeForApp();
    }
}

namespace Bot.ShopScope
{
    internal static class ShopScopedUiBridge
    {
        private sealed class ContextHolder
        {
            public ShopContext Shop;
        }

        private static readonly ConditionalWeakTable<Window, ContextHolder> Contexts =
            new ConditionalWeakTable<Window, ContextHolder>();
        private static int _initialized;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                EventManager.RegisterClassHandler(
                    typeof(Window),
                    FrameworkElement.LoadedEvent,
                    new RoutedEventHandler(OnWindowLoaded),
                    true);
                EventManager.RegisterClassHandler(
                    typeof(ButtonBase),
                    ButtonBase.ClickEvent,
                    new RoutedEventHandler(OnRoutedOperation),
                    true);
                EventManager.RegisterClassHandler(
                    typeof(MenuItem),
                    MenuItem.ClickEvent,
                    new RoutedEventHandler(OnRoutedOperation),
                    true);
                EventManager.RegisterClassHandler(
                    typeof(Window),
                    Keyboard.PreviewKeyDownEvent,
                    new KeyEventHandler(OnPreviewKeyDown),
                    true);
            }
            return new object();
        }

        public static T CreateForSeller<T>(string seller, Func<T> factory) where T : Window
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            var shop = ShopContextLocator.ResolveBySellerNick(seller);
            using (ShopSettingsScope.Enter(shop))
            {
                var window = factory();
                Attach(window, shop);
                return window;
            }
        }

        public static void Attach(Window window, ShopContext shop)
        {
            if (window == null || shop == null) return;
            ContextHolder existing;
            if (Contexts.TryGetValue(window, out existing))
            {
                existing.Shop = shop;
                return;
            }
            try { Contexts.Add(window, new ContextHolder { Shop = shop }); }
            catch { }
        }

        public static ShopContext Get(Window window)
        {
            ContextHolder holder;
            return window != null && Contexts.TryGetValue(window, out holder)
                ? holder.Shop
                : null;
        }

        public static void Run(ShopContext shop, Action action)
        {
            if (action == null) return;
            if (shop == null)
            {
                action();
                return;
            }
            using (ShopSettingsScope.Enter(shop)) action();
        }

        private static void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            var window = sender as Window;
            if (window == null) return;
            var shop = Get(window) ?? GetFromOwner(window) ?? ShopSettingsScope.Current ?? ResolveFromWindow(window);
            if (shop == null) return;
            Attach(window, shop);
            EnterForRoutedOperation(window, shop);
        }

        private static void OnRoutedOperation(object sender, RoutedEventArgs e)
        {
            var source = sender as DependencyObject;
            var window = ResolveWindow(source);
            var shop = Get(window) ?? GetFromOwner(window) ?? ShopSettingsScope.Current ?? ResolveFromWindow(window);
            if (shop == null) return;
            if (window != null) Attach(window, shop);
            EnterForRoutedOperation(window, shop);
        }

        private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            var window = sender as Window;
            var shop = Get(window) ?? GetFromOwner(window) ?? ResolveFromWindow(window);
            if (shop == null) return;
            EnterForRoutedOperation(window, shop);
        }

        private static void EnterForRoutedOperation(Window window, ShopContext shop)
        {
            if (shop == null) return;
            var scope = ShopSettingsScope.Enter(shop);
            var dispatcher = window == null ? Dispatcher.CurrentDispatcher : window.Dispatcher;
            dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(scope.Dispose));
        }

        private static Window ResolveWindow(DependencyObject source)
        {
            if (source == null) return null;
            var direct = source as Window ?? Window.GetWindow(source);
            if (direct != null) return direct;

            var menu = FindContextMenu(source);
            if (menu != null && menu.PlacementTarget != null)
                return Window.GetWindow(menu.PlacementTarget);
            return null;
        }

        private static ContextMenu FindContextMenu(DependencyObject source)
        {
            for (var current = source; current != null; current = LogicalTreeHelper.GetParent(current))
            {
                var menu = current as ContextMenu;
                if (menu != null) return menu;
            }
            return null;
        }

        private static ShopContext GetFromOwner(Window window)
        {
            for (var current = window == null ? null : window.Owner; current != null; current = current.Owner)
            {
                var shop = Get(current) ?? ResolveFromWindow(current);
                if (shop != null) return shop;
            }
            return null;
        }

        private static ShopContext ResolveFromWindow(Window window)
        {
            var assist = window as WndAssist;
            if (assist == null || assist.Desk == null) return null;
            var seller = (assist.Desk.WndTitle ?? string.Empty).Trim();
            if (seller.Length == 0) return null;
            try { return ShopContextLocator.ResolveBySellerNick(seller); }
            catch { return null; }
        }
    }
}
