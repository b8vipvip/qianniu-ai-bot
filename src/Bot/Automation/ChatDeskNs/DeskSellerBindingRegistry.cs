using Bot.Automation.ChatDeskNs.Automators;
using Bot.ChromeNs;
using BotLib;
using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Bot.Automation.ChatDeskNs
{
    /// <summary>
    /// Keeps the runtime seller-to-native-window relationship one-to-one.
    /// A generic reception Desk may exist before the injected QN identity is ready;
    /// once a unique authenticated seller is proven, that Desk is recreated with the real
    /// seller name so existing ShopKey/UI/send routing can keep using seller-named Desks.
    /// </summary>
    internal static class DeskSellerBindingRegistry
    {
        private static readonly object Sync = new object();
        private static readonly ConcurrentDictionary<string, int> SellerToHwnd =
            new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<int, string> HwndToSeller =
            new ConcurrentDictionary<int, string>();

        internal static Desk FindSellerDesk(string seller)
        {
            seller = NormalizeSeller(seller);
            if (seller.Length == 0 || !IsAuthenticatedSeller(seller)) return null;

            var legacy = Desk.FindExistingBySellerNick(seller);
            if (legacy != null)
            {
                Remember(legacy, seller);
                return legacy;
            }

            int hwnd;
            if (!SellerToHwnd.TryGetValue(seller, out hwnd)) return null;
            var desk = Desk.FindExistingByHwnd(hwnd);
            if (desk != null && desk.IsAlive) return desk;

            ForgetSeller(seller, hwnd);
            return null;
        }

        internal static string GetSeller(Desk desk)
        {
            if (desk == null || desk.Hwnd == null) return string.Empty;
            var title = NormalizeSeller(desk.WndTitle);
            if (title.Length > 0
                && !QnAccountFinder.IsGenericReceptionTitle(title)
                && IsAuthenticatedSeller(title))
            {
                Remember(desk, title);
                return title;
            }

            string seller;
            return HwndToSeller.TryGetValue(desk.Hwnd.Handle, out seller)
                && IsAuthenticatedSeller(seller)
                ? NormalizeSeller(seller)
                : string.Empty;
        }

        internal static bool IsSellerForDesk(Desk desk, string seller)
        {
            seller = NormalizeSeller(seller);
            if (desk == null || seller.Length == 0) return false;
            return string.Equals(GetSeller(desk), seller, StringComparison.Ordinal);
        }

        internal static Desk BindResolvedSeller(Desk desk, string seller, string evidence)
        {
            seller = NormalizeSeller(seller);
            if (desk == null || desk.Hwnd == null || seller.Length == 0
                || QnAccountFinder.IsGenericReceptionTitle(seller)
                || !IsAuthenticatedSeller(seller)) return null;

            lock (Sync)
            {
                CleanupStaleBindings();

                var hwnd = desk.Hwnd.Handle;
                int sellerHwnd;
                if (SellerToHwnd.TryGetValue(seller, out sellerHwnd) && sellerHwnd != hwnd)
                {
                    var existingSellerDesk = Desk.FindExistingByHwnd(sellerHwnd);
                    if (existingSellerDesk != null && existingSellerDesk.IsAlive)
                    {
                        Log.ErrorWithMaxCount("店铺窗口绑定被拒绝：同一seller不能绑定两个Desk: seller=" + seller
                            + ", existingHwnd=" + sellerHwnd + ", requestedHwnd=" + hwnd, 20);
                        return null;
                    }
                    ForgetSeller(seller, sellerHwnd);
                }

                string hwndSeller;
                if (HwndToSeller.TryGetValue(hwnd, out hwndSeller)
                    && !string.Equals(hwndSeller, seller, StringComparison.Ordinal))
                {
                    Log.ErrorWithMaxCount("店铺窗口绑定被拒绝：同一Desk不能绑定两个seller: hwnd=" + hwnd
                        + ", existingSeller=" + hwndSeller + ", requestedSeller=" + seller, 20);
                    return null;
                }

                var currentTitle = NormalizeSeller(desk.WndTitle);
                if (currentTitle.Length > 0
                    && !QnAccountFinder.IsGenericReceptionTitle(currentTitle)
                    && IsAuthenticatedSeller(currentTitle)
                    && !string.Equals(currentTitle, seller, StringComparison.Ordinal))
                {
                    Log.ErrorWithMaxCount("店铺窗口绑定被拒绝：Desk已有不同卖家身份: hwnd=" + hwnd
                        + ", deskSeller=" + currentTitle + ", requestedSeller=" + seller, 20);
                    return null;
                }

                if (string.Equals(currentTitle, seller, StringComparison.Ordinal))
                {
                    Remember(desk, seller);
                    return desk;
                }

                var pid = desk.ProcessId;
                try
                {
                    desk.Dispose();
                    var upgraded = Desk.Create(new QnChatWnd(seller, hwnd, pid));
                    if (upgraded == null)
                    {
                        Log.Error("卖家Desk身份升级失败: seller=" + seller + ", pid=" + pid + ", hwnd=" + hwnd);
                        return null;
                    }
                    Remember(upgraded, seller);
                    Log.Info("已绑定卖家与千牛窗口: seller=" + seller + ", pid=" + pid
                        + ", hwnd=" + hwnd + ", evidence=" + (evidence ?? string.Empty));
                    return upgraded;
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("升级卖家专属Desk失败: seller=" + seller + ", hwnd=" + hwnd
                        + ", " + ex.Message, 20);
                    return null;
                }
            }
        }

        internal static Desk BindForegroundSeller(QN qn, string evidence)
        {
            var seller = qn == null || qn.Seller == null
                ? string.Empty
                : NormalizeSeller(qn.Seller.Nick);
            if (seller.Length == 0 || !IsAuthenticatedSeller(seller)) return null;

            var existing = FindSellerDesk(seller);
            if (existing != null) return existing;

            var foreground = Desk.Snapshot()
                .Where(x => x != null && x.IsAlive && x.IsForeground)
                .ToList();
            if (foreground.Count != 1) return null;
            return BindResolvedSeller(foreground[0], seller, evidence);
        }

        private static void Remember(Desk desk, string seller)
        {
            if (desk == null || desk.Hwnd == null) return;
            seller = NormalizeSeller(seller);
            if (seller.Length == 0 || QnAccountFinder.IsGenericReceptionTitle(seller)
                || !IsAuthenticatedSeller(seller)) return;
            SellerToHwnd[seller] = desk.Hwnd.Handle;
            HwndToSeller[desk.Hwnd.Handle] = seller;
        }

        private static bool IsAuthenticatedSeller(string seller)
        {
            seller = NormalizeSeller(seller);
            if (seller.Length == 0) return false;
            try
            {
                return QN.GetRuntimeSafetySnapshot().Any(qn => qn != null && qn.Seller != null
                    && string.Equals((qn.Seller.Nick ?? string.Empty).Trim(), seller, StringComparison.Ordinal));
            }
            catch
            {
                try
                {
                    return QN.QNSet != null && QN.QNSet.Any(qn => qn != null && qn.Seller != null
                        && string.Equals((qn.Seller.Nick ?? string.Empty).Trim(), seller, StringComparison.Ordinal));
                }
                catch
                {
                    return false;
                }
            }
        }

        private static void CleanupStaleBindings()
        {
            foreach (var pair in SellerToHwnd.ToArray())
            {
                var desk = Desk.FindExistingByHwnd(pair.Value);
                if (desk != null && desk.IsAlive) continue;
                ForgetSeller(pair.Key, pair.Value);
            }
        }

        private static void ForgetSeller(string seller, int hwnd)
        {
            int ignoredHwnd;
            SellerToHwnd.TryRemove(seller, out ignoredHwnd);
            string ignoredSeller;
            HwndToSeller.TryRemove(hwnd, out ignoredSeller);
        }

        private static string NormalizeSeller(string value)
        {
            return (value ?? string.Empty).Trim();
        }
    }
}
