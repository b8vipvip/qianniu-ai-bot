using BotLib.Extensions;
using BotLib.Wpf.Extensions;
using BotLib;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
        private DateTime _preUpdateChatBrowserRectTime;
        private DateTime _preSendPlainTextAndImageTime;
        private BitmapImage _preSendPlainTextAndImageImage;
        public DateTime LatestSetTextTime;

        private AutomationElement _sendMessageButton;
        private AutomationElement _closeContactButton;
        private TextBox _messageInputTextArea;

        private FlaUI.Core.Application automationApplication;
        private UIA3Automation uia3Automation;

        private static readonly ConcurrentDictionary<string, DateTime> AnswerAttemptStartedAt =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);

        public string LastSetPlainText { get; private set; }

        private QN _qn;

        public QNRpa(QN qn)
        {
            _qn = qn ?? throw new ArgumentNullException("qn");
            uia3Automation = new UIA3Automation();
            // A QN can be created by a WebSocket session before the Qianniu desktop window has
            // been registered. Never dereference the legacy global Desk.Inst here. The seller-
            // scoped binding can safely be established now if available, or later by refresh/send.
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
            await RefreshChatControlsAsync(force);
        }

        public async Task SendImageAsync(string buyer, string imagePath)
        {
            await Task.Run(() =>
            {
                var image = BitmapImageEx.CreateFromFile(imagePath);
                OpenAndSendImage(buyer, image);
            });
        }

        private bool OpenAndSendImage(string buyer, BitmapImage image)
        {
            bool sendResult = false;
            if (_qn.Buyer == null || _qn.Buyer.Nick != buyer)
            {
                _qn.OpenChat(buyer);
                Thread.Sleep(500);
                Util.WaitFor(() => _qn.Buyer != null && _qn.Buyer.Nick == buyer, 5000, 10, false);
            }
            if (_qn.Buyer != null && _qn.Buyer.Nick == buyer)
            {
                var sellerDesk = ResolveSellerDesk();
                if (sellerDesk == null || !EnsureSellerDeskBinding(false))
                {
                    SetSendFailure("图片发送", "未找到当前卖家对应千牛窗口");
                    return false;
                }
                if (!sellerDesk.IsVisible)
                {
                    sellerDesk.Show();
                    Util.WaitFor(new Func<bool>(() => sellerDesk.IsVisible), 3000, 10, false);
                }
                SetAndSendImage(image);
            }
            sendResult = true;
            return sendResult;
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

        private static void PressEnter()
        {
            WinApi.Api.keybd_event(0x0D, 0, 0, 0);
            Thread.Sleep(80);
            WinApi.Api.keybd_event(0x0D, 0, 2, 0);
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

        private bool TryIsInputboxEmptyByCdp(out bool isEmpty)
        {
            isEmpty = false;
            try
            {
                if (_qn == null) return false;
                isEmpty = _qn.IsInputboxEmpty().GetAwaiter().GetResult();
                return true;
            }
            catch (Exception ex)
            {
                Log.Info("CDP检查输入框是否为空失败: " + ex.Message);
                return false;
            }
        }

        private bool IsEditorOrCdpInputboxEmpty()
        {
            if (_messageInputTextArea != null) return IsEditorEmptySafe();

            bool cdpEmpty;
            if (TryIsInputboxEmptyByCdp(out cdpEmpty)) return cdpEmpty;
            return false;
        }

        private bool WaitForSendConfirmed(string buyer, string text, DateTime sendStart, string method, int timeoutMs)
        {
            var end = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < end)
            {
                if (IsEditorOrCdpInputboxEmpty())
                {
                    BotConnectionDiagnostics.RecordSendAttempt(true, method + "，输入框已清空");
                    Log.Info(method + "发送确认成功：输入框已清空。text=" + text);
                    return true;
                }

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

                Thread.Sleep(150);
            }

            var editorText = GetEditorTextSafe();
            bool cdpEmpty;
            var hasCdpEmpty = TryIsInputboxEmptyByCdp(out cdpEmpty);
            SetSendFailure("发送确认", method + "后未确认发送；editorText=" + editorText
                + ", hasCdpEmpty=" + hasCdpEmpty + ", cdpEmpty=" + cdpEmpty);
            Log.Info(method + "发送未确认，editorText=" + editorText + ", hasCdpEmpty=" + hasCdpEmpty + ", cdpEmpty=" + cdpEmpty + ", text=" + text);
            return false;
        }

        private bool TryPressEnterSend(string buyer, string text, DateTime sendStart)
        {
            try
            {
                if (!FocusEditor())
                {
                    SetSendFailure("Enter发送", "无法聚焦聊天输入框");
                    return false;
                }
                if (!HasExpectedDraft(text))
                {
                    SetSendFailure("Enter发送", "发送前未确认输入框仍为目标文本");
                    return false;
                }
                PressEnter();
                return WaitForSendConfirmed(buyer, text, sendStart, "Enter", 3500);
            }
            catch (Exception ex)
            {
                SetSendFailure("Enter发送异常", ex.Message);
                Log.Exception(ex);
                return false;
            }
        }

        private bool TryClickSendButtonLeftPart(string buyer, string text, DateTime sendStart)
        {
            if (_sendMessageButton == null) return false;
            try
            {
                var rect = _sendMessageButton.BoundingRectangle;
                var x = (int)(rect.Left + Math.Min(Math.Max(rect.Width * 0.35, 10), Math.Max(rect.Width - 32, 10)));
                var y = (int)(rect.Top + rect.Height / 2);
                FlaUI.Core.Input.Mouse.Click(new System.Drawing.Point { X = x, Y = y });
                return WaitForSendConfirmed(buyer, text, sendStart, "按钮左侧点击", 4000);
            }
            catch (Exception ex)
            {
                SetSendFailure("发送按钮点击异常", ex.Message);
                Log.Exception(ex);
                return false;
            }
        }

        private bool TryClickSendButton(string buyer, string text, DateTime sendStart)
        {
            if (TryPressEnterSend(buyer, text, sendStart)) return true;

            try
            {
                RefreshChatControlsAsync(true).GetAwaiter().GetResult();
                if (!HasExpectedDraft(text))
                {
                    SetSendFailure("发送按钮回退", "发送前无法严格确认输入框仍为目标文本，已阻止点击发送按钮");
                    return false;
                }
                if (_sendMessageButton != null && TryClickSendButtonLeftPart(buyer, text, sendStart)) return true;
                SetSendFailure("发送按钮回退", _sendMessageButton == null ? "未找到发送按钮" : "点击后未确认发送");
            }
            catch (Exception ex)
            {
                SetSendFailure("发送按钮回退异常", ex.Message);
                Log.Exception(ex);
            }

            return false;
        }

        private bool SetAndSendImage(BitmapImage image)
        {
            bool rt = false;
            if ((DateTime.Now - _preSendPlainTextAndImageTime).TotalSeconds < 1.1 && _preSendPlainTextAndImageImage == image)
            {
                rt = false;
            }
            else
            {
                _preSendPlainTextAndImageTime = DateTime.Now;
                _preSendPlainTextAndImageImage = image;
                if (SetImage(image)) rt = TryClickSendButton(_qn == null || _qn.Buyer == null ? string.Empty : _qn.Buyer.Nick, string.Empty, DateTime.Now);
                else rt = false;
            }
            return rt;
        }

        private bool SetImage(BitmapImage img)
        {
            bool isok = false;
            ClipboardEx.UseClipboardWithAutoRestoreInUiThread(() =>
            {
                FocusEditor();
                Clipboard.Clear();
                Clipboard.SetImage(img);
                WinApi.PressCtrlV();
                DateTime now = DateTime.Now;
                do
                {
                    if (_messageInputTextArea != null && !string.IsNullOrEmpty(_messageInputTextArea.Text))
                    {
                        isok = true;
                        break;
                    }
                    DispatcherEx.DoEvents();
                } while ((DateTime.Now - now).TotalSeconds < 2.0);
                Util.WriteTimeElapsed(now, "等待时间");
            });
            return isok;
        }

        public bool FocusEditor()
        {
            bool isok = false;
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
                        RefreshChatControlsAsync(true).GetAwaiter().GetResult();
                    }
                    if (_messageInputTextArea == null)
                    {
                        SetSendFailure("聚焦输入框", "未找到聊天输入框");
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
            await Task.Delay(180);
            string manualQuestion;
            string manualAnswer;
            if (KnowledgeLearningService.TryBlockForManualReply(_qn, buyer, text, out manualQuestion, out manualAnswer)) return false;
            return await OpenAndSendText(buyer, text);
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
            var current = await _qn.GetCurrentConversationID();
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
                    var currentNick = await ReadCurrentBuyerNickAsync();
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
                    await Task.Delay(180);
                }

                Log.Info("会话持续为空，重新打开目标买家后再次确认: stage=" + stage + ", buyer=" + buyer);
                _qn.OpenChat(buyer);
                await Task.Delay(500);
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    var currentNick = await ReadCurrentBuyerNickAsync();
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
                    await Task.Delay(200);
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

                Log.Info("准备通过CDP写入输入框: buyer=" + buyer + ", text=" + text);
                _qn.InsertText2Inputbox(buyer, text);

                LastSetPlainText = text;
                LatestSetTextTime = DateTime.Now;

                await Task.Delay(800);
                await RefreshChatControlsAsync(true);

                bool cdpEmpty;
                var hasCdpEmpty = TryIsInputboxEmptyByCdp(out cdpEmpty);
                string editorText;
                var editorReadable = TryGetEditorText(out editorText);
                var ok = editorReadable && EditorMatchesExpectedText(editorText, text);
                if (!ok)
                {
                    SetSendFailure("CDP写入输入框", "无法通过UIA严格确认目标文本；hasCdpEmpty="
                        + hasCdpEmpty + ", cdpEmpty=" + cdpEmpty);
                }

                Log.Info("CDP写入输入框结果=" + ok + ", editorReadable=" + editorReadable
                    + ", hasCdpEmpty=" + hasCdpEmpty + ", cdpEmpty=" + cdpEmpty
                    + ", editorText=" + editorText + ", text=" + text);
                return ok;
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
            bool sendResult = false;
            ResetSendFailure();
            var attemptStartedAt = GetOrCreateAttemptStartedAt(buyer, text);
            try
            {
                Log.Info("自动发送开始: buyer=" + buyer + ", text=" + text + ", current=" + (_qn.Buyer == null ? "" : _qn.Buyer.Nick));

                if (!VerifyAnswerFreshness(buyer, text, attemptStartedAt, "写入前答案时效检查")) return false;

                if (_qn.Buyer == null || !IsExpectedBuyer(buyer, _qn.Buyer.Nick))
                {
                    _qn.OpenChat(buyer);
                    await Task.Delay(500);
                    var conv = await _qn.GetCurrentConversationID();
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

                if (!await VerifyCurrentBuyerAsync(buyer, "写入前会话确认"))
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
                    sellerDesk.Show();
                    Util.WaitFor(new Func<bool>(() => sellerDesk.IsVisible), 3000, 10, false);
                }

                // Prefer the Qianniu/CDP input path. The legacy clipboard + WPF Dispatcher path can
                // block for a long time on memory-constrained RDP servers and used to hold the
                // seller-wide send gate indefinitely. UIA remains the strict verifier and fallback.
                var setOk = await TrySetPlainTextByCdpAsync(buyer, text);
                if (!setOk)
                {
                    Log.Info("CDP写入输入框未通过严格确认，回退UIA剪贴板写入。buyer=" + buyer + ", text=" + text);
                    await RefreshChatControlsAsync(true);
                    setOk = SetPlainText(text);
                }

                if (!setOk)
                {
                    SetSendFailure("写入输入框", "CDP与UIA均未严格确认目标文本");
                    SendDeliveryWatchdog.CancelPending(SellerNick, buyer, text, GetSendFailureReason());
                    return false;
                }

                await Task.Delay(120);
                if (!VerifyAnswerFreshness(buyer, text, attemptStartedAt, "发送前答案时效检查"))
                {
                    ClearExpectedDraft(text, GetSendFailureReason());
                    SendDeliveryWatchdog.CancelPending(SellerNick, buyer, text, GetSendFailureReason());
                    return false;
                }
                if (!await VerifyCurrentBuyerAsync(buyer, "发送前会话确认"))
                {
                    ClearExpectedDraft(text, GetSendFailureReason());
                    SendDeliveryWatchdog.CancelPending(SellerNick, buyer, text, GetSendFailureReason());
                    return false;
                }
                if (!HasExpectedDraft(text))
                {
                    SetSendFailure("发送前文本确认", "输入框内容已变化或无法确认，已阻止发送");
                    SendDeliveryWatchdog.CancelPending(SellerNick, buyer, text, GetSendFailureReason());
                    return false;
                }

                SendDeliveryWatchdog.EnsurePending(SellerNick, buyer, text);
                var sendStart = DateTime.Now;
                sendResult = TryClickSendButton(buyer, text, sendStart);
                if (!sendResult && string.IsNullOrWhiteSpace(LastSendFailureReason))
                {
                    SetSendFailure("发送确认", "Enter与发送按钮均未确认消息送达");
                }
                if (sendResult)
                {
                    CompleteAttemptLease(buyer, text);
                }
                Log.Info("自动发送完成: result=" + sendResult + ", buyer=" + buyer
                    + ", failure=" + GetSendFailureReason() + ", text=" + text);
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

        private bool SetPlainText(string text)
        {
            text = text ?? string.Empty;
            var isok = false;
            try
            {
                ClipboardEx.UseClipboardWithAutoRestoreInUiThread(() =>
                {
                    if (!FocusEditor())
                    {
                        Log.Info("SetPlainText: FocusEditor failed.");
                        return;
                    }

                    Clipboard.Clear();
                    Clipboard.SetText(text);
                    PressCtrlA();
                    Thread.Sleep(80);
                    WinApi.PressCtrlV();

                    LastSetPlainText = text;
                    LatestSetTextTime = DateTime.Now;

                    DateTime now = DateTime.Now;
                    do
                    {
                        string editorText;
                        if (TryGetEditorText(out editorText)
                            && EditorMatchesExpectedText(editorText, text))
                        {
                            isok = true;
                            break;
                        }
                        DispatcherEx.DoEvents();
                        Thread.Sleep(80);
                    } while ((DateTime.Now - now).TotalSeconds < 2.5);

                    Log.Info("SetPlainText result=" + isok + ", editorText=" + GetEditorTextSafe() + ", text=" + text);
                });
            }
            catch (Exception e)
            {
                Log.Exception(e);
            }
            return isok;
        }
    }
}
