using Bot.ChromeNs;
using Bot.Common;
using BotLib;
using BotLib.Extensions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Bot.Automation.ChatDeskNs.Automators
{
    public class QnAccountFinder
    {
        public virtual string ChatWindowTitlePattern
        {
            get { return "千牛接待台"; }
        }

        private static QnChatWnd currenrQNChatWnd;

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

        public static bool IsGenericReceptionTitle(string value)
        {
            value = (value ?? string.Empty).Trim();
            return value.Length == 0
                || value.Equals("千牛接待台", StringComparison.OrdinalIgnoreCase)
                || value.Equals("千牛工作台", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Resolve one native reception window to the authenticated QN seller that owns
        /// the same AliWorkbench process. The Qt reception window itself is normally named
        /// only "千牛接待台", so that title must never be used as a seller identity.
        ///
        /// Evidence is intentionally fail-closed: we accept a seller only when exactly one
        /// live QN identity is visible in that AliWorkbench process' window titles. In a
        /// historical single-QN/single-window session we keep the old safe fallback.
        /// </summary>
        public static string ResolveSellerNameForWindow(
            int pid,
            int hwnd,
            string nativeWindowTitle)
        {
            var qns = GetRuntimeQns();
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

            // This is the only count-based fallback we permit. It preserves the historical
            // single-shop behavior but never guesses between two online shops.
            if (qns.Count == 1)
            {
                var openCount = 0;
                try
                {
                    foreach (var workbenchPid in GetAliWorkbenchPids())
                    {
                        WinApi.FindAllDesktopWindowByClassNameAndTitlePattern(
                            "Qt5152QWindowIcon",
                            "千牛接待台",
                            (windowHwnd, title) =>
                            {
                                if (windowHwnd != 0 && WinApi.IsVisible(windowHwnd)) openCount++;
                            },
                            workbenchPid);
                    }
                }
                catch
                {
                    openCount = 0;
                }

                if (openCount == 1)
                {
                    return (qns[0].Seller.Nick ?? string.Empty).Trim();
                }
            }

            if (matches.Count > 1)
            {
                Log.ErrorWithMaxCount(
                    "同一千牛进程匹配到多个客服身份，已阻止自动绑定: pid=" + pid
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
                        .ToList();
                }
                catch
                {
                    return new List<QN>();
                }
            }
        }

        private static bool HasSellerWindowEvidence(int pid, QN qn)
        {
            if (pid <= 0 || qn == null || qn.Seller == null) return false;
            var tokens = new[]
            {
                qn.Seller.Nick,
                qn.Seller.Display,
                qn.Seller.TargetId
            }
                .Select(x => (x ?? string.Empty).Trim())
                .Where(x => x.Length >= 3 && !IsGenericReceptionTitle(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (tokens.Count == 0) return false;

            try
            {
                var process = Process.GetProcessById(pid);
                var mainTitle = (process.MainWindowTitle ?? string.Empty).Trim();
                if (tokens.Any(token => ContainsIdentity(mainTitle, token)))
                    return true;
            }
            catch
            {
            }

            // Qianniu may nominate the generic reception window as MainWindow, while its
            // account/status top-level window still carries the authenticated seller name.
            // Probe Qt top-level windows in the same process for exact identity evidence.
            foreach (var token in tokens)
            {
                var found = false;
                try
                {
                    WinApi.FindAllDesktopWindowByClassNameAndTitlePattern(
                        "Qt5152QWindowIcon",
                        token,
                        (windowHwnd, title) =>
                        {
                            if (windowHwnd == 0) return;
                            if (ContainsIdentity(title, token)) found = true;
                        },
                        pid);
                }
                catch
                {
                    found = false;
                }
                if (found) return true;
            }
            return false;
        }

        private static bool ContainsIdentity(string title, string identity)
        {
            title = (title ?? string.Empty).Trim();
            identity = (identity ?? string.Empty).Trim();
            if (title.Length == 0 || identity.Length == 0) return false;
            return title.IndexOf(identity, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Returns every visible Qianniu reception window. The runtime must track HWNDs,
        /// not one process-global "current" window, because several shops can be logged
        /// in on the same Windows session at the same time.
        /// </summary>
        public virtual IList<QnChatWnd> GetOpenChatWnds()
        {
            var result = new List<QnChatWnd>();
            var handles = new HashSet<int>();
            foreach (var pid in GetAliWorkbenchPids().OrderBy(x => x))
            {
                try
                {
                    WinApi.FindAllDesktopWindowByClassNameAndTitlePattern(
                        "Qt5152QWindowIcon",
                        ChatWindowTitlePattern,
                        (qnHwnd, title) =>
                        {
                            if (qnHwnd == 0 || !WinApi.IsVisible(qnHwnd)) return;
                            if (!handles.Add(qnHwnd)) return;
                            var nativeTitle = (title ?? string.Empty).Trim();
                            var seller = ResolveSellerNameForWindow(pid, qnHwnd, nativeTitle);
                            result.Add(new QnChatWnd(seller, qnHwnd, pid));
                        },
                        pid);
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("枚举千牛接待窗口失败: pid=" + pid + ", " + ex.Message, 10);
                }
            }

            return result
                .OrderBy(x => x.Pid)
                .ThenBy(x => x.Hwnd)
                .ToList();
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
