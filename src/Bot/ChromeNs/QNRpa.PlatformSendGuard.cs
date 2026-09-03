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

            // A modal is part of the already-open conversation. Never navigate while it is present:
            // read the current buyer only and require an exact/equivalent match before authorizing it.
            if (!await VerifyCurrentBuyerWithoutNavigationAsync(
                buyer, "服务态度提醒继续发送前会话确认").ConfigureAwait(false))
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

        /// <summary>
        /// A verified send action followed by a stable empty composer is strong evidence that
        /// Qianniu accepted this exact Bot-owned draft. Live seller echo is still preferred, but a
        /// missing/delayed echo must never cause the same text to be written and sent a second time.
        /// The platform reminder is probed before accepting the empty composer so a late reminder is
        /// auto-confirmed instead of being mistaken for successful delivery.
        /// </summary>
        private async Task<bool> WaitForTextSubmissionAcceptedAsync(
            string buyer,
            string text,
            DateTime sendStart,
            string method,
            int timeoutMs)
        {
            var end = DateTime.Now.AddMilliseconds(Math.Max(900, timeoutMs));
            var emptyObserved = false;
            var emptyObservedAt = DateTime.MinValue;
            var platformProbeAfterAction = false;

            while (DateTime.Now < end)
            {
                try
                {
                    if (_qn != null && _qn.HasRecentSellerEcho(buyer, text, sendStart))
                    {
                        BotConnectionDiagnostics.RecordSendAttempt(true, method + "，卖家消息已回显");
                        Log.Info(method + "发送确认成功：已收到卖家消息回显。buyer=" + buyer);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Log.Info("检查卖家消息回显失败: " + ex.Message);
                }

                var remaining = Math.Max(250, (int)(end - DateTime.Now).TotalMilliseconds);
                var probe = await ProbeInputboxEmptyAsync(
                    method + "提交确认", Math.Min(700, remaining)).ConfigureAwait(false);
                if (probe.Completed && probe.IsEmpty)
                {
                    if (!emptyObserved)
                    {
                        emptyObserved = true;
                        emptyObservedAt = DateTime.Now;
                        Log.Info(method + "发送动作后观察到本次输入框清空，进入稳定提交确认；buyer=" + buyer);
                    }

                    // A service-attitude modal may appear just after Qianniu consumes the composer.
                    // Probe before accepting the clear state; exact reminder/continue is auto-clicked.
                    if (await StopIfPlatformSendBlockedAsync(
                        buyer, method + "提交确认").ConfigureAwait(false))
                    {
                        return false;
                    }
                    platformProbeAfterAction = true;

                    if ((DateTime.Now - emptyObservedAt).TotalMilliseconds >= 220)
                    {
                        var stable = await ProbeInputboxEmptyAsync(
                            method + "稳定清空确认", 650).ConfigureAwait(false);
                        if (stable.Completed && stable.IsEmpty)
                        {
                            // Re-probe after the stability window to catch a reminder whose UI
                            // appeared slightly later than the composer clear event.
                            if (await StopIfPlatformSendBlockedAsync(
                                buyer, method + "稳定清空后平台确认").ConfigureAwait(false))
                            {
                                return false;
                            }
                            if (!await VerifyCurrentBuyerWithoutNavigationAsync(
                                buyer, method + "提交后会话确认").ConfigureAwait(false))
                            {
                                return false;
                            }

                            ResetSendFailure();
                            BotConnectionDiagnostics.RecordSendAttempt(
                                true,
                                method + "，发送动作后输入框稳定清空，按千牛已接收提交处理；卖家回显可异步补证");
                            Log.Info(method + "发送提交确认成功：本次精确草稿在发送动作后稳定清空；"
                                + "禁止因实时回显缺失重新写入同一文本。buyer=" + buyer);
                            ArmLateServiceAttitudeContinuationWatch(buyer, method);
                            return true;
                        }
                    }
                }
                else if (probe.Completed && !probe.IsEmpty
                    && !platformProbeAfterAction
                    && (DateTime.Now - sendStart).TotalMilliseconds >= 350)
                {
                    // Some Qianniu builds keep the draft visible while the reminder is showing.
                    if (await StopIfPlatformSendBlockedAsync(
                        buyer, method + "发送动作后平台确认").ConfigureAwait(false))
                    {
                        return false;
                    }
                    platformProbeAfterAction = true;
                }

                await Task.Delay(100).ConfigureAwait(false);
            }

            if (await StopIfPlatformSendBlockedAsync(
                buyer, method + "超时前平台确认").ConfigureAwait(false))
            {
                return false;
            }

            SetSendFailure(
                "发送确认",
                method + "后既未检测到卖家回显，也未观察到发送动作后的稳定输入框清空；"
                    + "emptyObserved=" + emptyObserved);
            return false;
        }

        private void ArmLateServiceAttitudeContinuationWatch(string buyer, string method)
        {
            var startedAt = DateTime.Now;
            Task.Run(async () =>
            {
                try
                {
                    for (var attempt = 0; attempt < 8; attempt++)
                    {
                        await Task.Delay(attempt == 0 ? 260 : 320).ConfigureAwait(false);
                        if (_lastServiceAttitudeContinueAt >= startedAt) return;
                        var blocked = await StopIfPlatformSendBlockedAsync(
                            buyer, method + "迟到服务态度提醒监控").ConfigureAwait(false);
                        if (blocked) return;
                        if (_lastServiceAttitudeContinueAt >= startedAt) return;
                    }
                }
                catch (Exception ex)
                {
                    Log.Info("迟到服务态度提醒监控异常: " + ex.Message);
                }
            });
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
