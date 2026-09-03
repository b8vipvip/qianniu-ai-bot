using BotLib;
using FlaUI.Core.AutomationElements;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    public partial class QNRpa
    {
        private static readonly TimeSpan ServiceAttitudeContinueThrottle = TimeSpan.FromMilliseconds(1200);
        private readonly SemaphoreSlim _serviceAttitudeProbeGate = new SemaphoreSlim(1, 1);
        private int _lateServiceAttitudeWatchArmed;
        private DateTime _lastServiceAttitudeContinueAt = DateTime.MinValue;

        private sealed class ServiceAttitudeProbeResult
        {
            public bool Detected;
            public bool Continued;
            public string Detail = string.Empty;
            public AutomationElement ContinueButton;
        }

        /// <summary>
        /// Handle Qianniu's service-attitude reminder as one single-flight transaction.
        /// Production evidence from 1.1.1189 proved that abandoning a side-effectful UIA Invoke
        /// behind Task.WhenAny can create a ghost send: the caller records failure while the
        /// abandoned worker later clicks "继续发送". Therefore detection, buyer proof and the
        /// side effect are serialized here, and once the Invoke starts it is always awaited to
        /// completion. No second UIA scan may overlap this transaction.
        /// </summary>
        private async Task<bool> StopIfPlatformSendBlockedAsync(string buyer, string stage)
        {
            // Never queue another expensive UIA traversal behind an in-flight one. The owner of
            // the gate is already responsible for the currently visible reminder; send/late-watch
            // callers may continue their own non-UIA confirmation path without creating scan piles.
            if (!await _serviceAttitudeProbeGate.WaitAsync(0).ConfigureAwait(false))
            {
                Log.Info("千牛服务态度提醒单飞探测已在执行，跳过并发UIA扫描: seller=" + SellerNick
                    + ", buyer=" + buyer + ", stage=" + stage);
                return false;
            }

            try
            {
                ServiceAttitudeProbeResult detected;
                try
                {
                    // Read-only scan is awaited to completion. The implementation first checks
                    // top-level reminder titles and only falls back to the verified seller root,
                    // avoiding the old all-window/all-descendant repeated scans.
                    detected = await Task.Run(() => ProbeServiceAttitudeReminder(false)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Info("千牛平台发送拦截探测失败，保持原发送状态: " + ex.Message);
                    return false;
                }
                if (detected == null || !detected.Detected) return false;

                if (!await VerifyCurrentBuyerWithoutNavigationAsync(
                    buyer, "服务态度提醒继续发送前会话确认").ConfigureAwait(false))
                {
                    SetSendCancellation(
                        "平台发送拦截",
                        "检测到千牛“服务态度提醒”，但当前会话无法证明仍为目标买家，已拒绝自动确认");
                    Log.ErrorWithMaxCount(
                        "千牛服务态度提醒未自动确认：当前会话不是已验证目标买家。seller="
                        + SellerNick + ", buyer=" + buyer + ", stage=" + stage,
                        50);
                    return true;
                }

                if (_lastServiceAttitudeContinueAt != DateTime.MinValue
                    && DateTime.Now - _lastServiceAttitudeContinueAt < ServiceAttitudeContinueThrottle)
                {
                    Log.Info("千牛服务态度提醒已在短窗内自动确认，等待平台完成发送: seller="
                        + SellerNick + ", buyer=" + buyer + ", stage=" + stage);
                    return false;
                }

                if (detected.ContinueButton == null)
                {
                    SetSendCancellation("平台发送拦截", detected.Detail);
                    Log.ErrorWithMaxCount(
                        "千牛服务态度提醒无法安全自动确认，已停止本次发送且禁止盲目重试: seller="
                        + SellerNick + ", buyer=" + buyer + ", stage=" + stage
                        + ", detail=" + detected.Detail,
                        50);
                    return true;
                }

                ServiceAttitudeProbeResult result;
                try
                {
                    // IMPORTANT: there is intentionally no Task.WhenAny timeout here. Once this
                    // side-effectful UIA Invoke starts, the caller must observe its real outcome;
                    // otherwise an abandoned worker can click later after the send was marked failed.
                    result = await Task.Run(() => InvokeServiceAttitudeContinue(detected)).ConfigureAwait(false);
                }
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
                    await Task.Delay(140).ConfigureAwait(false);
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
            finally
            {
                _serviceAttitudeProbeGate.Release();
            }
        }

        /// <summary>
        /// A verified send action followed by a stable empty composer is strong evidence that
        /// Qianniu accepted this exact Bot-owned draft. Live seller echo is still preferred, but a
        /// missing/delayed echo must never cause the same text to be written and sent a second time.
        /// Platform-reminder scans are deliberately bounded by count (not abandoned by timeout):
        /// at most one early check, one stable-clear check, and one late single-flight check.
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
            var earlyPlatformProbeDone = false;
            var stablePlatformProbeDone = false;

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

                    if ((DateTime.Now - emptyObservedAt).TotalMilliseconds >= 220)
                    {
                        var stable = await ProbeInputboxEmptyAsync(
                            method + "稳定清空确认", 650).ConfigureAwait(false);
                        if (stable.Completed && stable.IsEmpty)
                        {
                            // One final platform check at the stable-clear boundary. It is serialized
                            // and fully awaited, so no orphan UIA worker can act after this method exits.
                            if (!stablePlatformProbeDone)
                            {
                                stablePlatformProbeDone = true;
                                if (await StopIfPlatformSendBlockedAsync(
                                    buyer, method + "稳定清空后平台确认").ConfigureAwait(false))
                                {
                                    return false;
                                }
                            }

                            if (!await VerifyCurrentBuyerWithoutNavigationAsync(
                                buyer, method + "提交后会话确认").ConfigureAwait(false))
                            {
                                return false;
                            }

                            ResetSendFailure();
                            var submissionEvidence = method
                                + "发送动作后本次Bot精确草稿稳定清空，且目标买家复核通过";
                            SendDeliveryWatchdog.MarkSubmissionAccepted(
                                SellerNick, buyer, text, submissionEvidence);
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
                    && !earlyPlatformProbeDone
                    && (DateTime.Now - sendStart).TotalMilliseconds >= 350)
                {
                    earlyPlatformProbeDone = true;
                    // Some Qianniu builds keep the draft visible while the reminder is showing.
                    if (await StopIfPlatformSendBlockedAsync(
                        buyer, method + "发送动作后平台确认").ConfigureAwait(false))
                    {
                        return false;
                    }
                }

                await Task.Delay(100).ConfigureAwait(false);
            }

            if (!stablePlatformProbeDone
                && await StopIfPlatformSendBlockedAsync(
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

        /// <summary>
        /// A reminder can animate in shortly after Qianniu consumes the composer. One delayed
        /// single-flight check is sufficient as a safety net. The former 8 x 260/320ms loop was
        /// removed because timed-out UIA workers overlapped and delayed the next order segment.
        /// </summary>
        private void ArmLateServiceAttitudeContinuationWatch(string buyer, string method)
        {
            if (Interlocked.CompareExchange(ref _lateServiceAttitudeWatchArmed, 1, 0) != 0)
            {
                return;
            }

            var startedAt = DateTime.Now;
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(650).ConfigureAwait(false);
                    if (_lastServiceAttitudeContinueAt >= startedAt) return;
                    await StopIfPlatformSendBlockedAsync(
                        buyer, method + "迟到服务态度提醒单次监控").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Info("迟到服务态度提醒单次监控异常: " + ex.Message);
                }
                finally
                {
                    Interlocked.Exchange(ref _lateServiceAttitudeWatchArmed, 0);
                }
            });
        }

        /// <summary>
        /// Locate the reminder without performing a side effect. Fast path checks the names of
        /// application top-level windows first; the screenshot/production reminder is a titled
        /// modal. Only if no titled reminder is found do we scan the already verified seller root.
        /// This avoids repeatedly traversing every Qianniu top-level subtree.
        /// </summary>
        private ServiceAttitudeProbeResult ProbeServiceAttitudeReminder(bool clickContinue)
        {
            var result = new ServiceAttitudeProbeResult();
            if (!EnsureSellerDeskBinding(false) || automationApplication == null || uia3Automation == null)
                return result;

            try
            {
                var reminderRoots = new List<AutomationElement>();
                var windows = automationApplication.GetAllTopLevelWindows(uia3Automation);
                if (windows != null)
                {
                    foreach (var window in windows.Where(x => x != null))
                    {
                        var title = RegexCompactPlatformGuardText(SafeName(window));
                        if (title.IndexOf("服务态度提醒", StringComparison.Ordinal) >= 0)
                        {
                            reminderRoots.Add(window);
                        }
                    }
                }

                if (reminderRoots.Count == 0)
                {
                    var desk = ResolveSellerDesk();
                    if (desk != null && desk.Hwnd != null && desk.Hwnd.Handle > 0)
                    {
                        try
                        {
                            var main = uia3Automation.FromHandle(new IntPtr(desk.Hwnd.Handle));
                            if (main != null && RootContainsServiceAttitudeReminder(main))
                            {
                                reminderRoots.Add(main);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Info("扫描卖家根窗口服务态度提醒失败: " + ex.Message);
                        }
                    }
                }

                if (reminderRoots.Count == 0) return result;
                result.Detected = true;

                var continueButtons = new List<AutomationElement>();
                foreach (var root in reminderRoots)
                {
                    var elements = new List<AutomationElement> { root };
                    try
                    {
                        elements.AddRange(root.FindAllDescendants().Where(x => x != null));
                    }
                    catch (Exception ex)
                    {
                        Log.Info("读取服务态度提醒子控件失败: " + ex.Message);
                    }

                    continueButtons.AddRange(elements.Where(x => string.Equals(
                        RegexCompactPlatformGuardText(SafeName(x)),
                        "继续发送",
                        StringComparison.Ordinal)));
                }

                // De-duplicate the same UIA object if the verified seller root and modal root happen
                // to expose it through equivalent wrappers.
                continueButtons = continueButtons.Distinct().ToList();
                if (continueButtons.Count != 1)
                {
                    result.Detail = continueButtons.Count == 0
                        ? "检测到千牛“服务态度提醒”，但未找到唯一“继续发送”按钮，已拒绝自动确认"
                        : "检测到千牛“服务态度提醒”，但存在多个“继续发送”候选按钮，已拒绝自动确认";
                    return result;
                }

                result.ContinueButton = continueButtons[0];
                result.Detail = "检测到千牛“服务态度提醒”及唯一“继续发送”按钮";
                if (clickContinue) return InvokeServiceAttitudeContinue(result);
                return result;
            }
            catch (Exception ex)
            {
                Log.Info("扫描千牛服务态度提醒失败: " + ex.Message);
                return result;
            }
        }

        private bool RootContainsServiceAttitudeReminder(AutomationElement root)
        {
            if (root == null) return false;
            if (RegexCompactPlatformGuardText(SafeName(root))
                .IndexOf("服务态度提醒", StringComparison.Ordinal) >= 0)
            {
                return true;
            }

            try
            {
                foreach (var element in root.FindAllDescendants())
                {
                    if (RegexCompactPlatformGuardText(SafeName(element))
                        .IndexOf("服务态度提醒", StringComparison.Ordinal) >= 0)
                    {
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private ServiceAttitudeProbeResult InvokeServiceAttitudeContinue(ServiceAttitudeProbeResult detected)
        {
            var result = detected ?? new ServiceAttitudeProbeResult();
            if (!result.Detected || result.ContinueButton == null)
            {
                result.Detail = string.IsNullOrWhiteSpace(result.Detail)
                    ? "服务态度提醒继续发送按钮不可用"
                    : result.Detail;
                return result;
            }

            try
            {
                result.ContinueButton.AsButton().Invoke();
                result.Continued = true;
                result.Detail = "已验证并调用千牛服务态度提醒的唯一“继续发送”按钮";
            }
            catch (Exception ex)
            {
                result.Continued = false;
                result.Detail = "检测到唯一“继续发送”按钮，但UIA调用失败：" + ex.Message;
            }
            return result;
        }

        private static string RegexCompactPlatformGuardText(string value)
        {
            return string.Concat((value ?? string.Empty).Where(c => !char.IsWhiteSpace(c))).Trim();
        }
    }
}
