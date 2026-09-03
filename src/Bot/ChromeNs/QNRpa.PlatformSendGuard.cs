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
        private static readonly TimeSpan ServiceAttitudeContinueThrottle = TimeSpan.FromMilliseconds(1200);
        private DateTime _lastServiceAttitudeContinueAt = DateTime.MinValue;

        private sealed class ServiceAttitudeProbeResult
        {
            public bool Detected;
            public bool Continued;
            public string Detail = string.Empty;
        }

        /// <summary>
        /// Qianniu can show a service-attitude reminder after the Bot has already invoked Send.
        /// For this product the desktop composer is Bot-owned, so an exact "服务态度提醒" dialog
        /// with an exact "继续发送" action is an expected continuation of the current Bot send.
        /// We auto-confirm only after the current buyer is still proven to be the requested buyer.
        /// Any ambiguous/missing button remains fail-closed and stops the outer retry loop.
        /// </summary>
        private async Task<bool> StopIfPlatformSendBlockedAsync(string buyer, string stage)
        {
            Task<ServiceAttitudeProbeResult> probe;
            try
            {
                probe = Task.Run(() => ProbeServiceAttitudeReminder(false));
            }
            catch
            {
                return false;
            }

            var winner = await Task.WhenAny(probe, Task.Delay(PlatformSendBlockProbeTimeoutMs)).ConfigureAwait(false);
            if (winner != probe)
            {
                Log.Info("千牛平台发送拦截探测超时，保持原发送状态: seller=" + SellerNick
                    + ", buyer=" + buyer + ", stage=" + stage);
                return false;
            }

            ServiceAttitudeProbeResult detected;
            try { detected = await probe.ConfigureAwait(false); }
            catch (Exception ex)
            {
                Log.Info("千牛平台发送拦截探测失败，保持原发送状态: " + ex.Message);
                return false;
            }
            if (detected == null || !detected.Detected) return false;

            // Do not let a modal from a stale/other conversation authorize a click.
            if (!await VerifyCurrentBuyerAsync(buyer, "服务态度提醒继续发送前会话确认").ConfigureAwait(false))
            {
                SetSendCancellation("平台发送拦截", "检测到千牛“服务态度提醒”，但当前会话无法证明仍为目标买家，已拒绝自动确认");
                Log.ErrorWithMaxCount(
                    "千牛服务态度提醒未自动确认：当前会话不是已验证目标买家。seller="
                    + SellerNick + ", buyer=" + buyer + ", stage=" + stage,
                    50);
                return true;
            }

            // The send pipeline can probe several times while the same modal is animating away.
            // Treat the short post-click window as already handled instead of double-invoking it.
            if (_lastServiceAttitudeContinueAt != DateTime.MinValue
                && DateTime.Now - _lastServiceAttitudeContinueAt < ServiceAttitudeContinueThrottle)
            {
                Log.Info("千牛服务态度提醒已在短窗内自动确认，等待平台完成发送: seller="
                    + SellerNick + ", buyer=" + buyer + ", stage=" + stage);
                return false;
            }

            Task<ServiceAttitudeProbeResult> action;
            try
            {
                action = Task.Run(() => ProbeServiceAttitudeReminder(true));
            }
            catch (Exception ex)
            {
                SetSendCancellation("平台发送拦截", "服务态度提醒自动确认启动失败：" + ex.Message);
                return true;
            }

            var actionWinner = await Task.WhenAny(action, Task.Delay(PlatformSendBlockProbeTimeoutMs)).ConfigureAwait(false);
            if (actionWinner != action)
            {
                SetSendCancellation("平台发送拦截", "检测到千牛“服务态度提醒”，但自动点击“继续发送”超时");
                return true;
            }

            ServiceAttitudeProbeResult result;
            try { result = await action.ConfigureAwait(false); }
            catch (Exception ex)
            {
                SetSendCancellation("平台发送拦截", "服务态度提醒自动确认失败：" + ex.Message);
                return true;
            }

            if (result != null && result.Detected && result.Continued)
            {
                _lastServiceAttitudeContinueAt = DateTime.Now;
                ResetSendFailure();
                Log.Info("千牛服务态度提醒已自动点击“继续发送”: seller=" + SellerNick
                    + ", buyer=" + buyer + ", stage=" + stage);
                await Task.Delay(180).ConfigureAwait(false);
                return false;
            }

            var detail = result == null || string.IsNullOrWhiteSpace(result.Detail)
                ? detected.Detail
                : result.Detail;
            SetSendCancellation("平台发送拦截", detail);
            Log.ErrorWithMaxCount(
                "千牛服务态度提醒无法安全自动确认，已停止本次发送且禁止盲目重试: seller="
                + SellerNick + ", buyer=" + buyer + ", stage=" + stage + ", detail=" + detail,
                50);
            return true;
        }

        private ServiceAttitudeProbeResult ProbeServiceAttitudeReminder(bool clickContinue)
        {
            var result = new ServiceAttitudeProbeResult();
            if (!EnsureSellerDeskBinding(false) || automationApplication == null || uia3Automation == null)
                return result;

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
                    var elements = new List<AutomationElement> { root };
                    AutomationElement[] descendants;
                    try { descendants = root.FindAllDescendants(); }
                    catch { descendants = new AutomationElement[0]; }
                    elements.AddRange(descendants.Where(x => x != null));

                    var names = elements
                        .Select(SafeName)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(x => x.Trim())
                        .ToArray();
                    var combined = string.Join(" ", names);
                    if (combined.IndexOf("服务态度提醒", StringComparison.Ordinal) < 0) continue;

                    result.Detected = true;
                    var continueButtons = elements
                        .Where(x => string.Equals(
                            RegexCompactPlatformGuardText(SafeName(x)),
                            "继续发送",
                            StringComparison.Ordinal))
                        .ToArray();

                    if (continueButtons.Length != 1)
                    {
                        result.Detail = continueButtons.Length == 0
                            ? "检测到千牛“服务态度提醒”，但未找到唯一“继续发送”按钮，已拒绝自动确认"
                            : "检测到千牛“服务态度提醒”，但存在多个“继续发送”候选按钮，已拒绝自动确认";
                        return result;
                    }

                    result.Detail = "检测到千牛“服务态度提醒”及唯一“继续发送”按钮";
                    if (!clickContinue) return result;

                    try
                    {
                        continueButtons[0].AsButton().Invoke();
                        result.Continued = true;
                        result.Detail = "已验证并调用千牛服务态度提醒的唯一“继续发送”按钮";
                    }
                    catch (Exception ex)
                    {
                        result.Detail = "检测到唯一“继续发送”按钮，但UIA调用失败：" + ex.Message;
                    }
                    return result;
                }
            }
            catch (Exception ex)
            {
                Log.Info("扫描千牛服务态度提醒失败: " + ex.Message);
            }
            return result;
        }

        private static string RegexCompactPlatformGuardText(string value)
        {
            return string.Concat((value ?? string.Empty).Where(c => !char.IsWhiteSpace(c))).Trim();
        }
    }
}
