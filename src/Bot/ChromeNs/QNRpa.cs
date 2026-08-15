using BotLib.Extensions;
using BotLib.Wpf.Extensions;
using BotLib;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Bot.Automation.ChatDeskNs;
using System.Windows;
using Bot.Automation;

namespace Bot.ChromeNs
{
    public partial class QNRpa
    {
        private const int CdpQuickProbeTimeoutMs = 900;
        private const int CdpActionTimeoutMs = 4500;
        private const int UiActionTimeoutMs = 1800;

        private sealed class CdpInputboxProbe
        {
            public bool Completed;
            public bool IsEmpty;
        }

        private DateTime _preUpdateChatBrowserRectTime;
        private DateTime _preSendPlainTextAndImageTime;
        private BitmapImage _preSendPlainTextAndImageImage;
        public DateTime LatestSetTextTime;

        private AutomationElement _sendMessageButton;
        private System.Drawing.Rectangle _sendMessageButtonRect;
        private AutomationElement _closeContactButton;
        private TextBox _messageInputTextArea;

        private FlaUI.Core.Application automationApplication;
        private UIA3Automation uia3Automation;

        private static readonly ConcurrentDictionary<string, DateTime> AnswerAttemptStartedAt =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);

        public string LastSetPlainText { get; private set; }

        private readonly QN _qn;

        public QNRpa(QN qn)
        {
            _qn = qn ?? throw new ArgumentNullException("qn");
            uia3Automation = new UIA3Automation();
            if (!EnsureSellerDeskBinding(false))
            {
                Log.Info("RPA初始化时卖家窗口尚未就绪，延后绑定: seller=" + SellerNick);
            }
            UpdateChatBrowserRect(true);
        }

        private bool IsSendButtonName(string name)
        {
            name = (name ?? string.Empty).Trim();
            return name == "发送" || name == "發送" || name.Equals("Send", StringComparison.OrdinalIgnoreCase);
        }

        public async void UpdateChatBrowserRect(bool force = false)
        {
            await RefreshChatControlsAsync(force).ConfigureAwait(false);
        }

        private static void PressCtrlA()
        {
            WinApi.Api.keybd_event(0x11, 0, 0, 0);
            Thread.Sleep(30);
            WinApi.Api.keybd_event(0x41, 0, 0, 0);
            Thread.Sleep(30);
            WinApi.Api.keybd_event(0x41, 0, 2, 0);
            Thread.Sleep(30);
            WinApi.Api.keybd_event(0x11, 0, 2, 0);
        }

        private static void PressBackspace()
        {
            WinApi.Api.keybd_event(0x08, 0, 0, 0);
            Thread.Sleep(50);
            WinApi.Api.keybd_event(0x08, 0, 2, 0);
        }

        private string GetEditorTextSafe()
        {
            string text;
            return TryGetEditorText(out text) ? text : string.Empty;
        }

        private bool IsEditorEmptySafe()
        {
            string text;
            return TryGetEditorText(out text) && string.IsNullOrWhiteSpace(text);
        }

        private bool HasOwnedRecentDraft(string text)
        {
            text = (text ?? string.Empty).Trim();
            return text.Length > 0
                && string.Equals((LastSetPlainText ?? string.Empty).Trim(), text, StringComparison.Ordinal)
                && LatestSetTextTime != DateTime.MinValue
                && (DateTime.Now - LatestSetTextTime).TotalSeconds <= 20;
        }

        private async Task<CdpInputboxProbe> ProbeInputboxEmptyAsync(string stage, int timeoutMs)
        {
            var result = new CdpInputboxProbe();
            if (_qn == null || _qn.CDP == null) return result;

            Task<bool> probeTask;
            try
            {
                probeTask = _qn.IsInputboxEmpty();
            }
            catch (Exception ex)
            {
                Log.Info("CDP检查输入框启动失败: stage=" + stage + ", " + ex.Message);
                return result;
            }

            var winner = await Task.WhenAny(probeTask, Task.Delay(Math.Max(250, timeoutMs))).ConfigureAwait(false);
            if (winner != probeTask)
            {
                Log.Info("CDP检查输入框超时，已放弃等待且不会阻塞UI线程: stage=" + stage
                    + ", timeoutMs=" + timeoutMs + ", seller=" + SellerNick);
                return result;
            }

            try
            {
                result.IsEmpty = await probeTask.ConfigureAwait(false);
                result.Completed = true;
            }
            catch (Exception ex)
            {
                Log.Info("CDP检查输入框失败: stage=" + stage + ", " + ex.Message);
            }
            return result;
        }

        private async Task<bool> RunCdpActionAsync(Action action, string stage, int timeoutMs)
        {
            if (action == null) return false;
            Task worker;
            try
            {
                worker = Task.Run(action);
            }
            catch (Exception ex)
            {
                SetSendFailure(stage, ex.Message);
                return false;
            }

            var winner = await Task.WhenAny(worker, Task.Delay(Math.Max(500, timeoutMs))).ConfigureAwait(false);
            if (winner != worker)
            {
                SetSendFailure(stage, "千牛CDP调用超时，已停止等待以保护Bot界面");
                Log.Info(stage + "超时，后台调用后续由CDP自身超时/重连机制回收。seller=" + SellerNick);
                return false;
            }

            try
            {
                await worker.ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                SetSendFailure(stage, ex.Message);
                Log.Exception(ex);
                return false;
            }
        }

        private async Task<bool> RunUiActionAsync(Func<bool> action, string stage, int timeoutMs)
        {
            if (action == null) return false;
            Task<bool> worker;
            try
            {
                worker = Task.Run(action);
            }
            catch (Exception ex)
            {
                SetSendFailure(stage, ex.Message);
                return false;
            }

            var winner = await Task.WhenAny(worker, Task.Delay(Math.Max(250, timeoutMs))).ConfigureAwait(false);
            if (winner != worker)
            {
                SetSendFailure(stage, "千牛UIA操作超时，已停止等待以保护Bot界面");
                return false;
            }

            try
            {
                return await worker.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SetSendFailure(stage, ex.Message);
                Log.Info(stage + "失败: " + ex.Message);
                return false;
            }
        }

        private async Task<bool> HasExpectedDraftFastAsync(string text, int probeTimeoutMs)
        {
            text = (text ?? string.Empty).Trim();
            if (text.Length == 0) return false;

            var probe = await ProbeInputboxEmptyAsync("草稿确认", probeTimeoutMs).ConfigureAwait(false);
            if (probe.Completed)
            {
                if (probe.IsEmpty) return false;
                if (HasOwnedRecentDraft(text)) return true;
            }
            else if (HasOwnedRecentDraft(text))
            {
                Log.Info("CDP草稿检查超时，使用本次Bot草稿租约继续发送: buyer="
                    + (_qn == null || _qn.Buyer == null ? string.Empty : _qn.Buyer.Nick));
                return true;
            }

            return await RunUiActionAsync(() => HasExpectedDraft(text), "UIA草稿确认", UiActionTimeoutMs).ConfigureAwait(false);
        }

        private async Task<bool> WaitForTextSendConfirmedAsync(string buyer, string text, DateTime sendStart, string method, int timeoutMs)
        {
            var end = DateTime.Now.AddMilliseconds(timeoutMs);
            var cdpAvailable = true;
            while (DateTime.Now < end)
            {
                try
                {
                    if (_qn != null && _qn.HasRecentSellerEcho(buyer, text, sendStart))
                    {
                        BotConnectionDiagnostics.RecordSendAttempt(true, method + "，卖家消息已回显");
                        Log.Info(method + "发送确认成功：已收到卖家消息回显。buyer=" + buyer + ", text=" + text);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Log.Info("检查卖家消息回显失败: " + ex.Message);
                }

                if (cdpAvailable)
                {
                    var remaining = Math.Max(250, (int)(end - DateTime.Now).TotalMilliseconds);
                    var probe = await ProbeInputboxEmptyAsync(method + "发送确认", Math.Min(1000, remaining)).ConfigureAwait(false);
                    if (probe.Completed)
                    {
                        if (probe.IsEmpty)
                        {
                            BotConnectionDiagnostics.RecordSendAttempt(true, method + "，输入框已清空");
                            Log.Info(method + "发送确认成功：输入框已清空。text=" + text);
                            return true;
                        }
                    }
                    else
                    {
                        cdpAvailable = false;
                    }
                }

                await Task.Delay(150).ConfigureAwait(false);
            }

            SetSendFailure("发送确认", method + "后未确认送达；cdpAvailable=" + cdpAvailable);
            Log.Info(method + "发送未确认，buyer=" + buyer + ", text=" + text);
            return false;
        }

        private bool TryClickCachedSendButtonNow()
        {
            if (_sendMessageButton == null && _sendMessageButtonRect.IsEmpty) return false;
            try
            {
                var sellerDesk = ResolveSellerDesk();
                if (sellerDesk == null || !EnsureSellerDeskBinding(false))
                {
                    Log.Info("发送主按钮点击失败：当前卖家千牛窗口未绑定。seller=" + SellerNick);
                    return false;
                }

                // Coordinate clicks are only safe when the verified seller window is actually on
                // top. Bring that exact seller window forward immediately before clicking; unlike
                // Enter this does not depend on whichever application previously owned keyboard focus.
                sellerDesk.BringTop();
                Thread.Sleep(120);

                var rect = _sendMessageButtonRect;
                if ((rect.Width <= 0 || rect.Height <= 0) && _sendMessageButton != null)
                {
                    rect = _sendMessageButton.BoundingRectangle;
                }
                if (rect.Width <= 0 || rect.Height <= 0) return false;

                // Qianniu's blue control is a split button: the right edge opens the Enter/Ctrl+Enter
                // menu. Never click its center/right edge. Reserve the right-most arrow zone and aim
                // at the middle of the left "发送" main-action area.
                var arrowGuard = Math.Max(18, Math.Min(30, rect.Width / 3));
                var mainWidth = rect.Width - arrowGuard;
                if (mainWidth < 16)
                {
                    Log.Info("发送按钮区域过窄，已阻止可能误点下拉箭头: rect="
                        + rect.Left + "," + rect.Top + "," + rect.Width + "x" + rect.Height);
                    return false;
                }
                var x = rect.Left + Math.Max(8, Math.Min(mainWidth / 2, mainWidth - 8));
                var y = rect.Top + rect.Height / 2;
                Log.Info("发送主按钮左侧区域坐标点击: seller=" + SellerNick
                    + ", rect=" + rect.Left + "," + rect.Top + "," + rect.Width + "x" + rect.Height
                    + ", click=" + x + "," + y + ", arrowGuard=" + arrowGuard);
                FlaUI.Core.Input.Mouse.Click(new System.Drawing.Point { X = x, Y = y });
                return true;
            }
            catch (Exception ex)
            {
                Log.Info("发送主按钮坐标点击异常: " + ex.Message);
                return false;
            }
        }

        private async Task<bool> TrySendTextViaUiaAsync(string buyer, string text, DateTime sendStart)
        {
            try
            {
                // UIA is used only to locate and cache the verified seller-window send rectangle.
                // Do not call Invoke() on Qianniu's split button: on current builds that semantic
                // action can block and/or open the send-mode dropdown instead of sending.
                if ((_sendMessageButton == null || _sendMessageButtonRect.IsEmpty)
                    && !await RefreshChatControlsAsync(true).ConfigureAwait(false))
                {
                    return false;
                }
                if (_sendMessageButton == null || _sendMessageButtonRect.IsEmpty)
                {
                    SetSendFailure("UIA主发送", "当前卖家千牛窗口内未找到可点击的发送主按钮区域");
                    return false;
                }
                if (!await HasExpectedDraftFastAsync(text, 1000).ConfigureAwait(false))
                {
                    SetSendFailure("UIA主发送", "发送前无法确认输入框仍为本次目标文本");
                    return false;
                }

                Log.Info("UIA定位完成，开始点击发送主按钮左侧区域: seller=" + SellerNick
                    + ", buyer=" + buyer + ", text=" + text);
                var clicked = await RunUiActionAsync(
                    () => TryClickCachedSendButtonNow(),
                    "发送主按钮坐标点击",
                    UiActionTimeoutMs).ConfigureAwait(false);
                if (!clicked)
                {
                    SetSendFailure("发送主按钮坐标点击", "未能点击已验证发送按钮的左侧主操作区域");
                    return false;
                }
                return await WaitForTextSendConfirmedAsync(
                    buyer, text, sendStart, "发送主按钮坐标", 3600).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SetSendFailure("UIA主发送异常", ex.Message);
                Log.Exception(ex);
                return false;
            }
        }

        public async Task SendImageAsync(string buyer, string imagePath)
        {
            var image = await Task.Run(() => BitmapImageEx.CreateFromFile(imagePath)).ConfigureAwait(false);
            await OpenAndSendImageAsync(buyer, image).ConfigureAwait(false);
        }

        private async Task<bool> OpenAndSendImageAsync(string buyer, BitmapImage image)
        {
            ResetSendFailure();
            if (_qn.Buyer == null || !IsExpectedBuyer(buyer, _qn.Buyer.Nick))
            {
                if (!await RunCdpActionAsync(() => _qn.OpenChat(buyer), "图片发送打开目标买家", CdpActionTimeoutMs).ConfigureAwait(false))
                    return false;
                await Task.Delay(500).ConfigureAwait(false);
            }
            if (_qn.Buyer == null || !IsExpectedBuyer(buyer, _qn.Buyer.Nick))
            {
                SetSendFailure("图片发送会话确认", "当前会话不是目标买家");
                return false;
            }
            if (!await VerifyCurrentBuyerAsync(buyer, "图片发送前会话确认").ConfigureAwait(false)) return false;

            var sellerDesk = ResolveSellerDesk();
            if (sellerDesk == null || !EnsureSellerDeskBinding(false))
            {
                SetSendFailure("图片发送", "未找到当前卖家对应千牛窗口");
                return false;
            }
            if (!sellerDesk.IsVisible)
            {
                try { sellerDesk.Show(); } catch (Exception ex) { Log.Info("显示图片发送窗口失败: " + ex.Message); }
            }
            if (!await RefreshChatControlsAsync(true).ConfigureAwait(false)) return false;

            var setOk = await RunUiActionAsync(() => SetImage(image), "图片草稿写入", 3500).ConfigureAwait(false);
            if (!setOk) return false;
            return await TrySendImageViaUiaAsync(buyer).ConfigureAwait(false);
        }

        private async Task<bool> TrySendImageViaUiaAsync(string buyer)
        {
            if ((_sendMessageButton == null || _sendMessageButtonRect.IsEmpty)
                && !await RefreshChatControlsAsync(true).ConfigureAwait(false))
            {
                return false;
            }
            if (_sendMessageButton == null || _sendMessageButtonRect.IsEmpty)
            {
                SetSendFailure("图片UIA发送", "当前卖家窗口内未找到可点击的发送主按钮区域");
                return false;
            }

            Log.Info("图片发送开始点击发送主按钮左侧区域: seller=" + SellerNick + ", buyer=" + buyer);
            var clicked = await RunUiActionAsync(
                () => TryClickCachedSendButtonNow(),
                "图片发送主按钮坐标点击",
                UiActionTimeoutMs).ConfigureAwait(false);
            if (!clicked) return false;
            await Task.Delay(500).ConfigureAwait(false);
            var empty = await RunUiActionAsync(() => IsEditorEmptySafe(), "图片坐标发送确认", UiActionTimeoutMs).ConfigureAwait(false);
            if (empty) BotConnectionDiagnostics.RecordSendAttempt(true, "图片发送主按钮坐标，输入框已清空");
            else SetSendFailure("图片发送", "发送主按钮坐标点击后未确认图片草稿已发送");
            return empty;
        }

        private bool SetImage(BitmapImage img)
        {
            var isok = false;
            if ((DateTime.Now - _preSendPlainTextAndImageTime).TotalSeconds < 1.1
                && _preSendPlainTextAndImageImage == img)
            {
                return false;
            }
            _preSendPlainTextAndImageTime = DateTime.Now;
            _preSendPlainTextAndImageImage = img;

            ClipboardEx.UseClipboardWithAutoRestoreInUiThread(() =>
            {
                if (!FocusEditor()) return;
                Clipboard.Clear();
                Clipboard.SetImage(img);
                WinApi.PressCtrlV();
                var started = DateTime.Now;
                do
                {
                    if (_messageInputTextArea != null && !string.IsNullOrEmpty(_messageInputTextArea.Text))
                    {
                        isok = true;
                        break;
                    }
                    DispatcherEx.DoEvents();
                } while ((DateTime.Now - started).TotalSeconds < 2.0);
                Util.WriteTimeElapsed(started, "等待时间");
            });
            return isok;
        }

        public bool FocusEditor()
        {
            var isok = false;
            DispatcherEx.xInvoke(() =>
            {
                var sellerDesk = ResolveSellerDesk();
                if (sellerDesk == null || !EnsureSellerDeskBinding(false))
                {
                    SetSendFailure("聚焦输入框", "未找到当前卖家对应千牛窗口");
                    return;
                }
                sellerDesk.BringTop();
                try
                {
                    if (_messageInputTextArea == null)
                    {
                        SetSendFailure("聚焦输入框", "聊天输入框尚未异步刷新完成");
                        return;
                    }

                    try
                    {
                        _messageInputTextArea.Focus();
                        Thread.Sleep(120);
                        isok = true;
                        return;
                    }
                    catch (Exception ex)
                    {
                        Log.Info("输入框 Focus 失败，改用鼠标点击: " + ex.Message);
                    }

                    var point = _messageInputTextArea.GetClickablePoint();
                    FlaUI.Core.Input.Mouse.Click(new System.Drawing.Point { X = point.X + 5, Y = point.Y + 5 });
                    Thread.Sleep(120);
                    isok = true;
                }
                catch (Exception e)
                {
                    SetSendFailure("聚焦输入框", e.Message);
                    Log.Exception(e);
                }
            });
            return isok;
        }

        public async Task<bool> SendTextAsync(string buyer, string text)
        {
            // Drop the caller's WPF SynchronizationContext before any CDP/UIA operation. Text
            // sending itself never presses Enter. UIA locates the verified seller-window split
            // button and the actual action clicks only its left "发送" region.
            await Task.Delay(180).ConfigureAwait(false);
            string manualQuestion;
            string manualAnswer;
            if (KnowledgeLearningService.TryBlockForManualReply(_qn, buyer, text, out manualQuestion, out manualAnswer)) return false;
            return await OpenAndSendText(buyer, text).ConfigureAwait(false);
        }

        private string SellerNick
        {
            get { return _qn == null || _qn.Seller == null ? string.Empty : (_qn.Seller.Nick ?? string.Empty).Trim(); }
        }

        private static string AttemptKey(string seller, string buyer, string text)
        {
            return (seller ?? string.Empty).Trim() + "#" + (buyer ?? string.Empty).Trim() + "#" + (text ?? string.Empty).Trim();
        }

        private DateTime GetOrCreateAttemptStartedAt(string buyer, string text)
        {
            CleanupAttemptLeases();
            return AnswerAttemptStartedAt.GetOrAdd(AttemptKey(SellerNick, buyer, text), _ => DateTime.Now);
        }

        private void CompleteAttemptLease(string buyer, string text)
        {
            DateTime ignored;
            AnswerAttemptStartedAt.TryRemove(AttemptKey(SellerNick, buyer, text), out ignored);
        }

        private static void CleanupAttemptLeases()
        {
            var threshold = DateTime.Now.AddMinutes(-2);
            foreach (var pair in AnswerAttemptStartedAt)
            {
                if (pair.Value >= threshold) continue;
                DateTime ignored;
                AnswerAttemptStartedAt.TryRemove(pair.Key, out ignored);
            }
        }

        private bool VerifyAnswerFreshness(string buyer, string text, DateTime attemptStartedAt, string stage)
        {
            if (ResponseProgressTracker.IsMandatoryOrderAnswer(SellerNick, buyer, text))
            {
                Log.Info("下单固定预设受保护，买家后续消息不会取消本次优先发送: seller="
                    + SellerNick + ", buyer=" + buyer + ", stage=" + stage);
                return true;
            }
            if (_qn == null || !_qn.HasBuyerMessageAfter(SellerNick, buyer, attemptStartedAt)) return true;
            SetSendFailure(stage, "买家已发送更新消息，旧答案不会发送");
            CompleteAttemptLease(buyer, text);
            Log.Info("旧答案发送/重试已取消: seller=" + SellerNick + ", buyer=" + buyer
                + ", stage=" + stage + ", reason=买家已发送更新消息");
            return false;
        }

        private bool IsExpectedBuyer(string expected, string current)
        {
            expected = (expected ?? string.Empty).Trim();
            current = (current ?? string.Empty).Trim();
            if (expected.Length == 0 || current.Length == 0) return false;
            if (string.Equals(expected, current, StringComparison.Ordinal)) return true;
            return BuyerIdentityAliasService.AreEquivalent(SellerNick, expected, current);
        }

        private async Task<string> ReadCurrentBuyerNickAsync()
        {
            var current = await _qn.GetCurrentConversationID().ConfigureAwait(false);
            return current == null || current.Result == null
                ? string.Empty
                : (current.Result.Nick ?? string.Empty).Trim();
        }

        private async Task<bool> VerifyCurrentBuyerAsync(string buyer, string stage)
        {
            buyer = (buyer ?? string.Empty).Trim();
            try
            {
                if (_qn == null || _qn.CDP == null)
                {
                    SetSendFailure(stage, "千牛消息连接不可用");
                    return false;
                }

                for (var attempt = 0; attempt < 7; attempt++)
                {
                    var currentNick = await ReadCurrentBuyerNickAsync().ConfigureAwait(false);
                    if (IsExpectedBuyer(buyer, currentNick))
                    {
                        _qn.SetActiveConversationByNick(SellerNick,
                            BuyerIdentityAliasService.ResolveInternalNick(SellerNick, currentNick), stage);
                        return true;
                    }
                    if (!string.IsNullOrWhiteSpace(currentNick))
                    {
                        SetSendFailure(stage, "目标买家=" + buyer + "，当前买家=" + currentNick);
                        return false;
                    }
                    Log.Info("会话确认暂时为空，等待稳定: stage=" + stage + ", buyer=" + buyer
                        + ", attempt=" + (attempt + 1) + "/7");
                    await Task.Delay(180).ConfigureAwait(false);
                }

                Log.Info("会话持续为空，重新打开目标买家后再次确认: stage=" + stage + ", buyer=" + buyer);
                if (!await RunCdpActionAsync(() => _qn.OpenChat(buyer), "重开目标买家", CdpActionTimeoutMs).ConfigureAwait(false))
                    return false;
                await Task.Delay(500).ConfigureAwait(false);
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    var currentNick = await ReadCurrentBuyerNickAsync().ConfigureAwait(false);
                    if (IsExpectedBuyer(buyer, currentNick))
                    {
                        _qn.SetActiveConversationByNick(SellerNick,
                            BuyerIdentityAliasService.ResolveInternalNick(SellerNick, currentNick), stage + "-重开确认");
                        return true;
                    }
                    if (!string.IsNullOrWhiteSpace(currentNick))
                    {
                        SetSendFailure(stage, "目标买家=" + buyer + "，重开后当前买家=" + currentNick);
                        return false;
                    }
                    await Task.Delay(200).ConfigureAwait(false);
                }

                SetSendFailure(stage, "目标买家=" + buyer + "，当前会话持续为空");
                return false;
            }
            catch (Exception ex)
            {
                SetSendFailure(stage, ex.Message);
                return false;
            }
        }

        private void ClearExpectedDraft(string expected, string reason)
        {
            try
            {
                if (!HasExpectedDraft(expected)) return;
                DispatcherEx.xInvoke(() =>
                {
                    if (!HasExpectedDraft(expected) || !FocusEditor()) return;
                    PressCtrlA();
                    PressBackspace();
                    LastSetPlainText = string.Empty;
                    Log.Info("已清除过期/不安全发送草稿: reason=" + reason);
                });
            }
            catch (Exception ex)
            {
                Log.Info("清除过期草稿失败: " + ex.Message);
            }
        }

        private async Task<bool> TrySetPlainTextByCdpAsync(string buyer, string text)
        {
            try
            {
                if (_qn == null) return false;

                var before = await ProbeInputboxEmptyAsync("写入前输入框检查", CdpQuickProbeTimeoutMs).ConfigureAwait(false);
                if (!before.Completed)
                {
                    SetSendFailure("CDP写入输入框", "写入前无法确认输入框是否为空，为避免覆盖人工草稿已停止发送");
                    return false;
                }

                if (!before.IsEmpty)
                {
                    // A failed send attempt leaves the exact Bot draft in the composer. The old
                    // retry path called insertText2Inputbox again, which appends the same answer and
                    // produced the duplicated seller echo seen in the field log. Reuse, never append.
                    if (HasOwnedRecentDraft(text))
                    {
                        Log.Info("检测到本次Bot草稿仍在输入框，重试直接复用且不再次追加: buyer=" + buyer);
                        return true;
                    }

                    if (_messageInputTextArea == null)
                    {
                        await RefreshChatControlsAsync(false).ConfigureAwait(false);
                    }
                    var exactExisting = await RunUiActionAsync(
                        () => HasExpectedDraft(text),
                        "已有草稿严格确认",
                        UiActionTimeoutMs).ConfigureAwait(false);
                    if (exactExisting)
                    {
                        LastSetPlainText = text;
                        LatestSetTextTime = DateTime.Now;
                        Log.Info("输入框已存在与本次答案完全一致的草稿，直接接管发送且不追加: buyer=" + buyer);
                        return true;
                    }

                    SetSendFailure("CDP写入输入框", "输入框已有非本次Bot草稿，已阻止覆盖/追加发送");
                    return false;
                }

                Log.Info("准备通过CDP写入输入框: buyer=" + buyer + ", text=" + text);
                if (!await RunCdpActionAsync(() => _qn.InsertText2Inputbox(buyer, text), "CDP写入输入框", CdpActionTimeoutMs).ConfigureAwait(false))
                    return false;

                LastSetPlainText = text;
                LatestSetTextTime = DateTime.Now;

                await Task.Delay(260).ConfigureAwait(false);
                var after = await ProbeInputboxEmptyAsync("写入后输入框检查", CdpQuickProbeTimeoutMs).ConfigureAwait(false);
                if (after.Completed && !after.IsEmpty)
                {
                    Log.Info("CDP写入输入框已由IMSDK确认，进入UIA定位发送主按钮动作: buyer=" + buyer + ", text=" + text);
                    return true;
                }

                await RefreshChatControlsAsync(true).ConfigureAwait(false);
                var uiVerified = await RunUiActionAsync(() => HasExpectedDraft(text), "UIA写入确认", UiActionTimeoutMs).ConfigureAwait(false);
                if (uiVerified)
                {
                    Log.Info("CDP写入由UIA严格确认: buyer=" + buyer + ", text=" + text);
                    return true;
                }

                SetSendFailure("CDP写入输入框", "写入后CDP/UIA均未确认本次目标草稿");
                return false;
            }
            catch (Exception ex)
            {
                SetSendFailure("CDP写入输入框异常", ex.Message);
                Log.Exception(ex);
                return false;
            }
        }

        private async Task<bool> OpenAndSendText(string buyer, string text)
        {
            var sendResult = false;
            ResetSendFailure();
            var attemptStartedAt = GetOrCreateAttemptStartedAt(buyer, text);
            try
            {
                Log.Info("自动发送开始: buyer=" + buyer + ", text=" + text + ", current=" + (_qn.Buyer == null ? "" : _qn.Buyer.Nick));

                if (!VerifyAnswerFreshness(buyer, text, attemptStartedAt, "写入前答案时效检查")) return false;

                if (_qn.Buyer == null || !IsExpectedBuyer(buyer, _qn.Buyer.Nick))
                {
                    if (!await RunCdpActionAsync(() => _qn.OpenChat(buyer), "打开目标买家", CdpActionTimeoutMs).ConfigureAwait(false))
                        return false;
                    await Task.Delay(500).ConfigureAwait(false);
                    var conv = await _qn.GetCurrentConversationID().ConfigureAwait(false);
                    if (conv != null && conv.Result != null && !string.IsNullOrWhiteSpace(conv.Result.Nick))
                    {
                        _qn.SetActiveConversationByNick(SellerNick,
                            BuyerIdentityAliasService.ResolveInternalNick(SellerNick, conv.Result.Nick), "beforeSend");
                    }
                }

                if (_qn.Buyer == null || !IsExpectedBuyer(buyer, _qn.Buyer.Nick))
                {
                    SetSendFailure("会话确认", "当前会话不是目标买家；target=" + buyer
                        + ", current=" + (_qn.Buyer == null ? "" : _qn.Buyer.Nick));
                    SendDeliveryWatchdog.CancelPending(SellerNick, buyer, text, GetSendFailureReason());
                    return false;
                }

                if (!await VerifyCurrentBuyerAsync(buyer, "写入前会话确认").ConfigureAwait(false))
                {
                    SendDeliveryWatchdog.CancelPending(SellerNick, buyer, text, GetSendFailureReason());
                    return false;
                }

                if (!VerifyAnswerFreshness(buyer, text, attemptStartedAt, "写入前答案时效检查"))
                {
                    SendDeliveryWatchdog.CancelPending(SellerNick, buyer, text, GetSendFailureReason());
                    return false;
                }

                var sellerDesk = ResolveSellerDesk();
                if (sellerDesk == null || !EnsureSellerDeskBinding(false))
                {
                    SetSendFailure("发送窗口", "未找到当前卖家对应千牛窗口");
                    SendDeliveryWatchdog.CancelPending(SellerNick, buyer, text, GetSendFailureReason());
                    return false;
                }
                if (!sellerDesk.IsVisible)
                {
                    try { sellerDesk.Show(); } catch (Exception ex) { Log.Info("显示文本发送窗口失败: " + ex.Message); }
                }

                var setOk = await TrySetPlainTextByCdpAsync(buyer, text).ConfigureAwait(false);
                if (!setOk)
                {
                    SendDeliveryWatchdog.CancelPending(SellerNick, buyer, text, GetSendFailureReason());
                    return false;
                }

                await Task.Delay(80).ConfigureAwait(false);
                if (!VerifyAnswerFreshness(buyer, text, attemptStartedAt, "发送前答案时效检查"))
                {
                    ClearExpectedDraft(text, GetSendFailureReason());
                    SendDeliveryWatchdog.CancelPending(SellerNick, buyer, text, GetSendFailureReason());
                    return false;
                }
                if (!await VerifyCurrentBuyerAsync(buyer, "发送前会话确认").ConfigureAwait(false))
                {
                    ClearExpectedDraft(text, GetSendFailureReason());
                    SendDeliveryWatchdog.CancelPending(SellerNick, buyer, text, GetSendFailureReason());
                    return false;
                }
                if (!await HasExpectedDraftFastAsync(text, 1200).ConfigureAwait(false))
                {
                    SetSendFailure("发送前文本确认", "输入框内容已变化或无法确认，已阻止发送");
                    SendDeliveryWatchdog.CancelPending(SellerNick, buyer, text, GetSendFailureReason());
                    return false;
                }

                // Refresh once immediately before the action so the cached split-button rectangle
                // belongs to the current seller/window. There is no Enter or UIA Invoke send path.
                if (!await RefreshChatControlsAsync(true).ConfigureAwait(false))
                {
                    SendDeliveryWatchdog.CancelPending(SellerNick, buyer, text, GetSendFailureReason());
                    return false;
                }

                SendDeliveryWatchdog.EnsurePending(SellerNick, buyer, text);
                var sendStart = DateTime.Now;
                sendResult = await TrySendTextViaUiaAsync(buyer, text, sendStart).ConfigureAwait(false);
                if (!sendResult && string.IsNullOrWhiteSpace(LastSendFailureReason))
                {
                    SetSendFailure("发送确认", "发送主按钮坐标点击后未确认消息送达");
                }
                if (sendResult)
                {
                    CompleteAttemptLease(buyer, text);
                }
                Log.Info("自动发送完成: result=" + sendResult + ", buyer=" + buyer
                    + ", method=UIA定位+发送主按钮坐标, failure=" + GetSendFailureReason() + ", text=" + text);
            }
            catch (Exception ex)
            {
                SetSendFailure("自动发送异常", ex.Message);
                SendDeliveryWatchdog.CancelPending(SellerNick, buyer, text, GetSendFailureReason());
                Log.Exception(ex);
                sendResult = false;
            }
            return sendResult;
        }
    }
}