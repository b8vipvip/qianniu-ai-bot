using Bot.ChromeNs;
using Bot.Common;
using BotLib;
using BotLib.Extensions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Bot.Automation.ChatDeskNs.Automators
{
    public class QnAccountFinder
    {
        public virtual string ChatWindowTitlePattern
        {
            get { return "千牛接待台"; }
        }

        private static QnChatWnd currenrQNChatWnd;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetWindowTextLengthW(IntPtr hWnd);

        private sealed class ReceptionCandidate
        {
            public int Pid { get; set; }
            public int Hwnd { get; set; }
            public string Title { get; set; }
            public string Seller { get; set; }
            public int Score { get; set; }
        }

        static QnAccountFinder()
        {
            currenrQNChatWnd = null;
        }

        private static HashSet<int> GetAliWorkbenchPids()
        {
            var pids = new HashSet<int>();
            var aliWorkbenchPs = Process.GetProcessesByName("AliWorkbench");
            foreach (var p in aliWorkbenchPs.xSafeForEach())
            {
                pids.Add(p.Id);
            }
            return pids;
        }

        /// <summary>
        /// Read only a top-level native window caption without sending WM_GETTEXT into Qianniu.
        /// Recent Qt reception windows can reject or stall cross-process SendMessage(WM_GETTEXT),
        /// which previously produced a continuous SendForGetText failure once per desk scan and
        /// caused the verified Desk/current-buyer monitor to disappear. GetWindowTextW is the
        /// bounded Win32 caption API for top-level windows and does not enter the target UI thread.
        /// </summary>
        public static string ReadNativeWindowTitle(int hwnd)
        {
            if (hwnd == 0) return string.Empty;
            try
            {
                var length = GetWindowTextLengthW(new IntPtr(hwnd));
                var capacity = Math.Max(256, Math.Min(4096, length + 1));
                var sb = new StringBuilder(capacity);
                var copied = GetWindowTextW(new IntPtr(hwnd), sb, sb.Capacity);
                return copied > 0 ? sb.ToString().Trim() : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        public static bool IsGenericReceptionTitle(string value)
        {
            value = (value ?? string.Empty).Trim();
            return value.Length == 0
                || value.Equals("千牛接待台", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsSystemNotificationTitle(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length == 0) return false;
            return value.Equals("千牛系统消息", StringComparison.OrdinalIgnoreCase)
                || value.Equals("千牛系统通知", StringComparison.OrdinalIgnoreCase)
                || value.Equals("千牛消息通知", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Login/workbench shells are top-level Qt windows too, but they are not reception desks.
        /// They can temporarily expose a seller identity and used to race the real 千牛接待台 HWND,
        /// producing a second Desk for the same seller. Reject them before seller binding.
        /// </summary>
        public static bool IsNonReceptionWorkbenchTitle(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length == 0) return false;
            if (value.IndexOf("接待", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("客服", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;

            return value.IndexOf("千牛登录", StringComparison.OrdinalIgnoreCase) >= 0
                || value.Equals("千牛工作台", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith("-千牛工作台", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith(" - 千牛工作台", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Resolve one native reception window to the authenticated QN seller. A direct
        /// seller token in this HWND's own title is the strongest evidence. Process-wide
        /// evidence is only accepted when exactly one authenticated seller matches, because
        /// current Qianniu can host several logged-in sellers in one AliWorkbench process.
        /// </summary>
        public static string ResolveSellerNameForWindow(
            int pid,
            int hwnd,
            string nativeWindowTitle)
        {
            if (IsSystemNotificationTitle(nativeWindowTitle) || IsNonReceptionWorkbenchTitle(nativeWindowTitle))
                return string.Empty;
            var qns = GetRuntimeQns();
            var direct = MatchUniqueSellerFromTitle(nativeWindowTitle, qns);
            if (direct.Length > 0) return direct;
            if (qns.Count == 0) return (nativeWindowTitle ?? string.Empty).Trim();

            var matches = qns
                .Where(qn => HasSellerWindowEvidence(pid, qn))
                .Select(qn => (qn.Seller.Nick ?? string.Empty).Trim())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (matches.Count == 1)
            {
                return matches[0];
            }

            // Preserve the historical single-shop behavior only when there is one live QN
            // and one visible reception candidate. Never guess between two online shops.
            if (qns.Count == 1 && GetReceptionCandidateCount() == 1)
            {
                return (qns[0].Seller.Nick ?? string.Empty).Trim();
            }

            if (matches.Count > 1)
            {
                Log.ErrorWithMaxCount(
                    "同一千牛进程匹配到多个客服身份，等待窗口级证据后再绑定: pid=" + pid
                    + ", hwnd=" + hwnd
                    + ", sellers=" + string.Join(",", matches),
                    10);
            }
            return (nativeWindowTitle ?? string.Empty).Trim();
        }

        private static IList<QN> GetRuntimeQns()
        {
            try
            {
                return QN.GetRuntimeSafetySnapshot()
                    .Where(qn => qn != null
                        && qn.Seller != null
                        && !string.IsNullOrWhiteSpace(qn.Seller.Nick))
                    .GroupBy(qn => qn.Seller.Nick.Trim(), StringComparer.Ordinal)
                    .Select(group => group.First())
                    .ToList();
            }
            catch
            {
                try
                {
                    if (QN.QNSet == null) return new List<QN>();
                    return QN.QNSet
                        .Where(qn => qn != null
                            && qn.Seller != null
                            && !string.IsNullOrWhiteSpace(qn.Seller.Nick))
                        .GroupBy(qn => qn.Seller.Nick.Trim(), StringComparer.Ordinal)
                        .Select(group => group.First())
                        .ToList();
                }
                catch
                {
                    return new List<QN>();
                }
            }
        }

        private static string MatchUniqueSellerFromTitle(string title, IList<QN> qns)
        {
            title = (title ?? string.Empty).Trim();
            if (title.Length == 0 || qns == null || qns.Count == 0) return string.Empty;

            var matches = qns
                .Where(qn => SellerTokens(qn).Any(token => ContainsIdentity(title, token)))
                .Select(qn => (qn.Seller.Nick ?? string.Empty).Trim())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return matches.Count == 1 ? matches[0] : string.Empty;
        }

        private static IList<string> SellerTokens(QN qn)
        {
            if (qn == null || qn.Seller == null) return new List<string>();
            return new[]
            {
                qn.Seller.Nick,
                qn.Seller.Display,
                qn.Seller.TargetId
            }
                .Select(x => (x ?? string.Empty).Trim())
                .Where(x => x.Length >= 3 && !IsGenericReceptionTitle(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool HasSellerWindowEvidence(int pid, QN qn)
        {
            if (pid <= 0 || qn == null || qn.Seller == null) return false;
            var tokens = SellerTokens(qn);
            if (tokens.Count == 0) return false;

            try
            {
                var process = Process.GetProcessById(pid);
                var mainTitle = (process.MainWindowTitle ?? string.Empty).Trim();
                if (!IsNonReceptionWorkbenchTitle(mainTitle)
                    && tokens.Any(token => ContainsIdentity(mainTitle, token)))
                    return true;
            }
            catch
            {
            }

            // Do not pass seller text as a regex pattern. Enumerate all Qt top-level windows
            // in this AliWorkbench process and compare their native captions locally. Avoid the
            // legacy WinApi.GetText/WM_GETTEXT path here because a hung Qt HWND must never block
            // or poison the reception-window scan.
            var found = false;
            try
            {
                WinApi.FindAllDesktopWindowByClassNameAndTitlePattern(
                    "Qt5152QWindowIcon",
                    null,
                    (windowHwnd, ignoredTitle) =>
                    {
                        if (windowHwnd == 0 || found) return;
                        var title = ReadNativeWindowTitle(windowHwnd);
                        if (IsNonReceptionWorkbenchTitle(title)) return;
                        if (tokens.Any(token => ContainsIdentity(title, token))) found = true;
                    },
                    pid);
            }
            catch
            {
                found = false;
            }
            return found;
        }

        private static bool ContainsIdentity(string title, string identity)
        {
            title = (title ?? string.Empty).Trim();
            identity = (identity ?? string.Empty).Trim();
            if (title.Length == 0 || identity.Length == 0) return false;
            return title.IndexOf(identity, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Qianniu no longer guarantees that every reception HWND exposes exactly the title
        /// "千牛接待台". Enumerate all visible Qt top-level windows in AliWorkbench first,
        /// reject known login/workbench shells, then keep only the strongest candidate per seller.
        /// An empty full-size title remains a compatibility fallback but can never beat an explicit
        /// reception/seller-title window for the same authenticated seller.
        /// </summary>
        public virtual IList<QnChatWnd> GetOpenChatWnds()
        {
            var candidates = new List<ReceptionCandidate>();
            var handles = new HashSet<int>();
            var qns = GetRuntimeQns();
            foreach (var pid in GetAliWorkbenchPids().OrderBy(x => x))
            {
                try
                {
                    WinApi.FindAllDesktopWindowByClassNameAndTitlePattern(
                        "Qt5152QWindowIcon",
                        null,
                        (qnHwnd, ignoredTitle) =>
                        {
                            if (qnHwnd == 0 || !WinApi.IsVisible(qnHwnd)) return;
                            if (!handles.Add(qnHwnd)) return;

                            var nativeTitle = ReadNativeWindowTitle(qnHwnd);
                            if (!IsReceptionCandidate(qnHwnd, nativeTitle, qns)) return;

                            var seller = ResolveSellerNameForWindow(pid, qnHwnd, nativeTitle);
                            if (string.IsNullOrWhiteSpace(seller)) return;
                            candidates.Add(new ReceptionCandidate
                            {
                                Pid = pid,
                                Hwnd = qnHwnd,
                                Title = nativeTitle,
                                Seller = seller.Trim(),
                                Score = GetReceptionEvidenceScore(nativeTitle, qns)
                            });
                        },
                        pid);
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("枚举千牛接待窗口失败: pid=" + pid + ", " + ex.Message, 10);
                }
            }

            return candidates
                .GroupBy(candidate => candidate.Seller, StringComparer.Ordinal)
                .Select(group => group
                    .OrderByDescending(candidate => candidate.Score)
                    .ThenBy(candidate => candidate.Pid)
                    .ThenBy(candidate => candidate.Hwnd)
                    .First())
                .Select(candidate => new QnChatWnd(candidate.Seller, candidate.Hwnd, candidate.Pid))
                .OrderBy(x => x.Pid)
                .ThenBy(x => x.Hwnd)
                .ToList();
        }

        private static int GetReceptionEvidenceScore(string title, IList<QN> qns)
        {
            title = (title ?? string.Empty).Trim();
            if (title.Equals("千牛接待台", StringComparison.OrdinalIgnoreCase)) return 100;
            if (title.IndexOf("接待", StringComparison.OrdinalIgnoreCase) >= 0) return 95;
            if (title.IndexOf("客服", StringComparison.OrdinalIgnoreCase) >= 0) return 90;
            if (MatchUniqueSellerFromTitle(title, qns).Length > 0) return 85;
            if (title.Length == 0) return 20;
            return 40;
        }

        private static bool IsReceptionCandidate(int hwnd, string title, IList<QN> qns)
        {
            try
            {
                if (!WinApi.IsVisible(hwnd) || WinApi.IsWindowMinimized(hwnd)) return false;
                if (IsSystemNotificationTitle(title) || IsNonReceptionWorkbenchTitle(title)) return false;
                var rect = WinApi.GetWindowRectangle(hwnd);
                if (rect.Width < 560 || rect.Height < 380) return false;

                if (IsGenericReceptionTitle(title)) return true;
                if (MatchUniqueSellerFromTitle(title, qns).Length > 0) return true;

                title = (title ?? string.Empty).Trim();
                if (title.IndexOf("接待", StringComparison.OrdinalIgnoreCase) >= 0
                    || title.IndexOf("客服", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;

                // Some recent Qianniu builds expose an empty accessible title for a full-size
                // reception window. Tiny account/status floating cards are excluded by size.
                return title.Length == 0 && rect.Width >= 700 && rect.Height >= 480;
            }
            catch
            {
                return false;
            }
        }

        private static int GetReceptionCandidateCount()
        {
            var count = 0;
            var qns = GetRuntimeQns();
            foreach (var pid in GetAliWorkbenchPids())
            {
                try
                {
                    WinApi.FindAllDesktopWindowByClassNameAndTitlePattern(
                        "Qt5152QWindowIcon",
                        null,
                        (hwnd, ignoredTitle) =>
                        {
                            if (hwnd == 0) return;
                            var title = ReadNativeWindowTitle(hwnd);
                            if (IsReceptionCandidate(hwnd, title, qns)) count++;
                        },
                        pid);
                }
                catch
                {
                }
            }
            return count;
        }

        /// <summary>
        /// Legacy compatibility API. New runtime code must use GetOpenChatWnds().
        /// </summary>
        public virtual (QnChatWnd, QnChatWnd) GetSingleChatWnd()
        {
            var previous = currenrQNChatWnd;
            var all = GetOpenChatWnds();
            if (currenrQNChatWnd != null)
            {
                var current = all.FirstOrDefault(x => x.Hwnd == currenrQNChatWnd.Hwnd);
                if (current != null)
                {
                    currenrQNChatWnd = current;
                    return (current, previous);
                }
            }

            currenrQNChatWnd = all.FirstOrDefault();
            return (currenrQNChatWnd, previous);
        }
    }
}
