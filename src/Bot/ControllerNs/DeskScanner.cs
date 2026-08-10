using Bot.AssistWindow.NotifyIcon;
using Bot.Automation.ChatDeskNs;
using Bot.Automation.ChatDeskNs.Automators;
using Bot.Common;
using BotLib;
using BotLib.BaseClass;
using BotLib.Extensions;
using BotLib.Misc;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Bot.ControllerNs
{
    public class DeskScanner : Disposable
    {
        private const int ScanIntervalMs = 1000;
        private static NoReEnterTimer _timer;
        private static bool _hadDetectSellerEver;
        private static bool _hadTipNoSellerEver;

        static DeskScanner()
        {
            _hadDetectSellerEver = false;
            _hadTipNoSellerEver = false;
        }

        public static void LoopScan()
        {
            _timer = new NoReEnterTimer(Loop, ScanIntervalMs, 0);
        }

        private static async void Loop()
        {
            try
            {
                var opened = GetOpenedChatWnds();
                DetectQianniu(opened.FirstOrDefault());

                var openedHandles = new HashSet<int>(opened.Select(x => x.Hwnd));
                foreach (var chatWnd in opened)
                {
                    if (chatWnd == null || chatWnd.Hwnd == 0) continue;
                    var existing = Desk.FindExistingByHwnd(chatWnd.Hwnd);

                    // On startup the native Qt window may appear before the injected QN
                    // identity is ready, so an attached Desk can initially be named only
                    // "千牛接待台". Once QnAccountFinder has authenticated seller evidence,
                    // recreate only that Desk so all legacy seller-bound routing sees the
                    // real seller instead of the generic window title.
                    if (existing != null
                        && QnAccountFinder.IsGenericReceptionTitle(existing.WndTitle)
                        && !QnAccountFinder.IsGenericReceptionTitle(chatWnd.Name))
                    {
                        Log.Info("千牛窗口身份已解析，升级为卖家专属Desk: old="
                            + existing.WndTitle + ", seller=" + chatWnd.Name
                            + ", pid=" + chatWnd.Pid + ", hwnd=" + chatWnd.Hwnd);
                        existing.Dispose();
                        existing = null;
                    }

                    var desk = existing;
                    if (desk == null)
                    {
                        desk = Desk.Create(chatWnd);
                        if (desk != null)
                        {
                            Log.Info("已注册千牛店铺窗口: seller=" + chatWnd.Name
                                + ", pid=" + chatWnd.Pid + ", hwnd=" + chatWnd.Hwnd);
                        }
                    }

                    // Every visible reception window gets its own attached Bot shell even
                    // while seller identity is still being resolved. Business send routing
                    // remains fail-closed until the seller name is unique.
                    if (desk != null && desk.AssistWindow != null)
                    {
                        desk.AssistWindow.EnsureVisibleForMultiShopAttachedMode();
                    }
                }

                foreach (var desk in Desk.Snapshot().ToList())
                {
                    if (desk == null || desk.Hwnd == null) continue;
                    if (openedHandles.Contains(desk.Hwnd.Handle) && desk.IsAlive) continue;
                    Log.Info("千牛店铺窗口已关闭，释放独立会话: seller=" + desk.WndTitle
                        + ", pid=" + desk.ProcessId + ", hwnd=" + desk.Hwnd.Handle);
                    desk.Dispose();
                }

                if (opened.Count == 0)
                {
                    await Task.Delay(1000);
                }
            }
            catch (Exception e)
            {
                Log.Exception(e);
            }
        }

        private static IList<QnChatWnd> GetOpenedChatWnds()
        {
            return QnAccountFinderFactory.Finder.GetOpenChatWnds()
                ?? new List<QnChatWnd>();
        }

        private static void DetectQianniu(QnChatWnd chatWnd)
        {
            if (chatWnd == null)
            {
                if (!_hadDetectSellerEver && !_hadTipNoSellerEver)
                {
                    var msg = string.Empty;
                    if (Process.GetProcessesByName("AliWorkbench").Length < 1)
                    {
                        msg = string.Format("需要打开千牛【接待窗口】,{0}才能起作用", Params.AppName);
                    }
                    else
                    {
                        msg = string.Format("需要运行千牛，并打开接待窗口，{0}才能起作用!!", Params.AppName);
                    }
                    _hadTipNoSellerEver = true;
                    MsgBox.ShowTrayTip(msg, "没有检测到【千牛接待窗口】", 30);
                }
            }
            else
            {
                _hadDetectSellerEver = true;
            }
        }

        protected override void CleanUp_Managed_Resources()
        {
            _timer.Stop();
            _timer.Dispose();
        }
    }
}
