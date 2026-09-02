using BotLib;
using FlaUI.Core.AutomationElements;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    public partial class QNRpa
    {
        private const int PlatformSendBlockProbeTimeoutMs = 900;

        /// <summary>
        /// Detect Qianniu's service-attitude confirmation as a platform policy block. This method
        /// never clicks the dialog. It converts the send into a cancellation so the existing outer
        /// retry loop stops immediately instead of blindly sending the same text again.
        /// </summary>
        private async Task<bool> StopIfPlatformSendBlockedAsync(string buyer, string stage)
        {
            Task<string> probe;
            try
            {
                probe = Task.Run(() =>
                {
                    string detail;
                    return TryDetectServiceAttitudeBlock(out detail) ? detail : string.Empty;
                });
            }
            catch
            {
                return false;
            }

            var winner = await Task.WhenAny(probe, Task.Delay(PlatformSendBlockProbeTimeoutMs)).ConfigureAwait(false);
            if (winner != probe)
            {
                Log.Info("千牛平台发送拦截探测超时，保持原发送失败结果: seller=" + SellerNick
                    + ", buyer=" + buyer + ", stage=" + stage);
                return false;
            }

            string detailText;
            try { detailText = await probe.ConfigureAwait(false); }
            catch (Exception ex)
            {
                Log.Info("千牛平台发送拦截探测失败，保持原发送失败结果: " + ex.Message);
                return false;
            }
            if (string.IsNullOrWhiteSpace(detailText)) return false;

            SetSendCancellation("平台发送拦截", detailText);
            Log.ErrorWithMaxCount(
                "千牛服务态度提醒已阻止自动发送，Bot不会点击“继续发送”也不会盲目重试: seller="
                + SellerNick + ", buyer=" + buyer + ", stage=" + stage,
                50);
            return true;
        }

        private bool TryDetectServiceAttitudeBlock(out string detail)
        {
            detail = string.Empty;
            if (!EnsureSellerDeskBinding(false) || automationApplication == null || uia3Automation == null)
                return false;

            try
            {
                var roots = new List<AutomationElement>();
                var windows = automationApplication.GetAllTopLevelWindows(uia3Automation);
                if (windows != null) roots.AddRange(windows.Where(x => x != null));

                var desk = ResolveSellerDesk();
                if (desk != null && desk.Hwnd != null && desk.Hwnd.Handle > 0)
                {
                    try
                    {
                        var main = uia3Automation.FromHandle(new IntPtr(desk.Hwnd.Handle));
                        if (main != null && !roots.Any(x => x.Equals(main))) roots.Add(main);
                    }
                    catch { }
                }

                foreach (var root in roots)
                {
                    var names = new List<string>();
                    AddPlatformGuardName(names, root);
                    AutomationElement[] descendants;
                    try { descendants = root.FindAllDescendants(); }
                    catch { descendants = new AutomationElement[0]; }
                    foreach (var element in descendants) AddPlatformGuardName(names, element);

                    var combined = string.Join(" ", names);
                    if (combined.IndexOf("服务态度提醒", StringComparison.Ordinal) < 0) continue;
                    var hasContinue = names.Any(x => string.Equals(
                        RegexCompactPlatformGuardText(x),
                        "继续发送",
                        StringComparison.Ordinal));
                    detail = hasContinue
                        ? "检测到千牛“服务态度提醒”及“继续发送”按钮；该平台提示必须由人工判断，Bot禁止自动确认"
                        : "检测到千牛“服务态度提醒”；该平台提示必须由人工判断，Bot禁止自动确认";
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Info("扫描千牛服务态度提醒失败: " + ex.Message);
            }
            return false;
        }

        private static void AddPlatformGuardName(ICollection<string> names, AutomationElement element)
        {
            if (names == null || element == null) return;
            var name = SafeName(element);
            if (!string.IsNullOrWhiteSpace(name)) names.Add(name.Trim());
        }

        private static string RegexCompactPlatformGuardText(string value)
        {
            return string.Concat((value ?? string.Empty).Where(c => !char.IsWhiteSpace(c))).Trim();
        }
    }
}
