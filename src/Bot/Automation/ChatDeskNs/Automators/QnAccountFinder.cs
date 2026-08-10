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
                            result.Add(new QnChatWnd((title ?? string.Empty).Trim(), qnHwnd, pid));
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