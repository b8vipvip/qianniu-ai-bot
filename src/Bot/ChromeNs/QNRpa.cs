using BotLib.Extensions;
using BotLib.Wpf.Extensions;
using BotLib;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using System;
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

        public string LastSetPlainText { get; private set; }

        private QN _qn;

        public QNRpa(QN qn)
        {
            _qn = qn;
            automationApplication = FlaUI.Core.Application.Attach(Desk.Inst.ProcessId);
            uia3Automation = new UIA3Automation();
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
                Util.WaitFor(() => _qn.Buyer.Nick == buyer, 5000, 10, false);
            }
            if (_qn.Buyer.Nick == buyer)
            {
                if (!Desk.Inst.IsVisible)
                {
                    Desk.Inst.Show();
                    Util.WaitFor(new Func<bool>(() => Desk.Inst.IsVisible), 3000, 10, false);
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
                // 不再按 Esc。千牛在输入框有草稿时按 Esc 会弹出“您还未回复买家/关闭会话确认”，反而阻断发送。
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
                // 千牛“发送”是分裂按钮，右侧小箭头会打开“按Enter/按Ctrl+Enter发送”菜单。
                // 这里只点击按钮左侧正文区域，避开右侧下拉箭头。
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
            // 首选 Enter。当前千牛菜单已勾选“按Enter发送”，这比点击分裂按钮更稳定，也不会误点下拉箭头。
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
                Desk.Inst.BringTop();
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

        private async Task<bool> VerifyCurrentBuyerAsync(string buyer, string stage)
        {
            try
            {
                if (_qn == null || _qn.CDP == null)
                {
                    SetSendFailure(stage, "千牛消息连接不可用");
                    return false;
                }
                var current = await _qn.GetCurrentConversationID();
                var currentNick = current == null || current.Result == null
                    ? string.Empty
                    : (current.Result.Nick ?? string.Empty).Trim();
                if (!string.Equals(currentNick, (buyer ?? string.Empty).Trim(), StringComparison.Ordinal))
                {
                    SetSendFailure(stage, "目标买家=" + buyer + "，当前买家=" + currentNick);
                    return false;
                }
                _qn.SetActiveConversationByNick(
                    _qn.Seller == null ? string.Empty : _qn.Seller.Nick,
                    currentNick,
                    stage);
                return true;
            }
            catch (Exception ex)
            {
                SetSendFailure(stage, ex.Message);
                return false;
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
            try
            {
                Log.Info("自动发送开始: buyer=" + buyer + ", text=" + text + ", current=" + (_qn.Buyer == null ? "" : _qn.Buyer.Nick));

                if (_qn.Buyer == null || _qn.Buyer.Nick != buyer)
                {
                    _qn.OpenChat(buyer);
                    await Task.Delay(500);
                    var conv = await _qn.GetCurrentConversationID();
                    if (conv != null && conv.Result != null && !string.IsNullOrWhiteSpace(conv.Result.Nick))
                    {
                        _qn.SetActiveConversationByNick(_qn.Seller == null ? string.Empty : _qn.Seller.Nick, conv.Result.Nick, "beforeSend");
                    }
                }

                if (_qn.Buyer == null || _qn.Buyer.Nick != buyer)
                {
                    SetSendFailure("会话确认", "当前会话不是目标买家；target=" + buyer
                        + ", current=" + (_qn.Buyer == null ? "" : _qn.Buyer.Nick));
                    return false;
                }

                if (!await VerifyCurrentBuyerAsync(buyer, "写入前会话确认"))
                {
                    return false;
                }

                if (!Desk.Inst.IsVisible)
                {
                    Desk.Inst.Show();
                    Util.WaitFor(new Func<bool>(() => Desk.Inst.IsVisible), 3000, 10, false);
                }

                await RefreshChatControlsAsync(true);
                var setOk = SetPlainText(text);
                if (!setOk)
                {
                    Log.Info("RPA写入输入框失败，改用CDP insertText2Inputbox。buyer=" + buyer + ", text=" + text);
                    setOk = await TrySetPlainTextByCdpAsync(buyer, text);
                }

                if (!setOk)
                {
                    SetSendFailure("写入输入框", "UIA与CDP均未严格确认目标文本");
                    return false;
                }

                await Task.Delay(120);
                if (!await VerifyCurrentBuyerAsync(buyer, "发送前会话确认"))
                {
                    return false;
                }
                if (!HasExpectedDraft(text))
                {
                    SetSendFailure("发送前文本确认", "输入框内容已变化或无法确认，已阻止发送");
                    return false;
                }

                var sendStart = DateTime.Now;
                sendResult = TryClickSendButton(buyer, text, sendStart);
                if (!sendResult && string.IsNullOrWhiteSpace(LastSendFailureReason))
                {
                    SetSendFailure("发送确认", "Enter与发送按钮均未确认消息送达");
                }
                Log.Info("自动发送完成: result=" + sendResult + ", buyer=" + buyer
                    + ", failure=" + GetSendFailureReason() + ", text=" + text);
            }
            catch (Exception ex)
            {
                SetSendFailure("自动发送异常", ex.Message);
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
