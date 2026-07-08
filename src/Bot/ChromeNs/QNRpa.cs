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
    public class QNRpa
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

        public string LastSetPlainText
        {
            get;
            private set;
        }

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
            if (Desk.Inst.IsVisibleAndNotMinimized)
            {
                if (force || (DateTime.Now - _preUpdateChatBrowserRectTime).TotalSeconds >= 3)
                {
                    _preUpdateChatBrowserRectTime = DateTime.Now;
                    if (automationApplication.MainWindowHandle.ToInt32() < 1) return;
                    await Task.Run(() =>
                    {
                        try
                        {
                            var topWnds = automationApplication.GetAllTopLevelWindows(uia3Automation);
                            var mainWnd = topWnds.FirstOrDefault(k => k.ClassName == "MutilChatView");
                            if (mainWnd == null)
                            {
                                BotConnectionDiagnostics.RecordRpaScan(false, false, "未找到MutilChatView");
                                return;
                            }

                            var descendants = mainWnd.FindAllDescendants();
                            var sendMessageButton = descendants.FirstOrDefault(k =>
                            {
                                if (k.Properties.Name.IsSupported && IsSendButtonName(k.Name))
                                {
                                    return true;
                                }
                                return false;
                            });
                            _sendMessageButton = sendMessageButton;

                            var messageInputTextArea = descendants.FirstOrDefault(k =>
                            {
                                if (k.Properties.ClassName.IsSupported && k.ClassName == "TextRichEdit")
                                {
                                    return true;
                                }
                                return false;
                            });
                            if (messageInputTextArea != null)
                            {
                                _messageInputTextArea = messageInputTextArea.AsTextBox();
                            }
                            BotConnectionDiagnostics.RecordRpaScan(_sendMessageButton != null, _messageInputTextArea != null, "UIA扫描完成");
                        }
                        catch (Exception ex)
                        {
                            BotConnectionDiagnostics.RecordRpaScan(false, false, ex.Message);
                            Log.Exception(ex);
                        }
                    });

                }
            }
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
            try
            {
                if (_messageInputTextArea == null) return string.Empty;
                return _messageInputTextArea.Text ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private bool IsEditorEmptySafe()
        {
            return string.IsNullOrWhiteSpace(GetEditorTextSafe());
        }

        private bool TryInvokeSendButton()
        {
            if (_sendMessageButton == null) return false;
            try
            {
                // 优先使用 UIA Invoke，不依赖鼠标坐标，能降低人工移动鼠标导致点偏的概率。
                _sendMessageButton.AsButton().Invoke();
                return true;
            }
            catch (Exception ex)
            {
                Log.Info("发送按钮 Invoke 失败，改用鼠标点击: " + ex.Message);
            }

            try
            {
                _sendMessageButton.Click();
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                return false;
            }
        }

        private bool TryClickSendButton()
        {
            try
            {
                // 每次发送都强制刷新一次控件。千牛发送按钮在发送后可能重建，缓存旧按钮会造成“右侧有答案但没有发出去”。
                UpdateChatBrowserRect(true);
                Thread.Sleep(350);

                if (_sendMessageButton != null)
                {
                    if (TryInvokeSendButton())
                    {
                        Thread.Sleep(700);
                        if (IsEditorEmptySafe())
                        {
                            BotConnectionDiagnostics.RecordSendAttempt(true, "按钮发送");
                            Log.Info("自动发送成功，输入框已清空。text=" + LastSetPlainText);
                            return true;
                        }
                    }

                    Log.Info("触发发送后输入框仍有内容，尝试 Enter 兜底。text=" + LastSetPlainText);
                }
            }
            catch (Exception ex)
            {
                BotConnectionDiagnostics.RecordSendAttempt(false, ex.Message);
                Log.Exception(ex);
            }

            // 兜底：如果千牛按钮文字变成繁体或 UIA 找不到按钮，则聚焦输入框后按 Enter。
            try
            {
                FocusEditor();
                PressEnter();
                Thread.Sleep(700);
                var ok = IsEditorEmptySafe();
                BotConnectionDiagnostics.RecordSendAttempt(ok, ok ? "Enter兜底" : "Enter后输入框未清空");
                Log.Info("Enter 兜底发送结果=" + ok + ", text=" + LastSetPlainText);
                return ok;
            }
            catch (Exception ex)
            {
                BotConnectionDiagnostics.RecordSendAttempt(false, ex.Message);
                Log.Exception(ex);
                return false;
            }
        }

        private bool SetAndSendImage(BitmapImage image)
        {
            bool rt = false;
            if ((DateTime.Now - _preSendPlainTextAndImageTime).TotalSeconds < 1.1
                && _preSendPlainTextAndImageImage == image)
            {
                rt = false;
            }
            else
            {
                _preSendPlainTextAndImageTime = DateTime.Now;
                _preSendPlainTextAndImageImage = image;
                if (SetImage(image))
                {
                    rt = TryClickSendButton();
                }
                else
                {
                    rt = false;
                }
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
                        UpdateChatBrowserRect(true);
                        Thread.Sleep(300);
                    }
                    if (_messageInputTextArea == null) return;

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
                    Log.Exception(e);
                }
            });
            return isok;
        }

        public async Task<bool> SendTextAsync(string buyer, string text)
        {
            return await OpenAndSendText(buyer, text);
        }

        private async Task<bool> OpenAndSendText(string buyer, string text)
        {
            bool sendResult = false;
            try
            {
                // 同一个买家连续发第二条消息时，千牛当前会话虽然没变，但输入区/发送按钮可能已重建。
                // 所以每次发送前都确保聊天窗口在前台，并刷新一次控件。
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
                    Log.Info("自动发送跳过：当前会话不是目标买家。target=" + buyer + ", current=" + (_qn.Buyer == null ? "" : _qn.Buyer.Nick));
                    BotConnectionDiagnostics.RecordSendAttempt(false, "当前会话不是目标买家");
                    return false;
                }

                if (!Desk.Inst.IsVisible)
                {
                    Desk.Inst.Show();
                    Util.WaitFor(new Func<bool>(() => Desk.Inst.IsVisible), 3000, 10, false);
                }

                UpdateChatBrowserRect(true);
                Thread.Sleep(400);

                if (!SetPlainText(text))
                {
                    Log.Info("自动发送失败：写入输入框失败。buyer=" + buyer + ", text=" + text);
                    BotConnectionDiagnostics.RecordSendAttempt(false, "写入输入框失败");
                    return false;
                }

                Thread.Sleep(500);
                sendResult = TryClickSendButton();
                Log.Info("自动发送完成: result=" + sendResult + ", buyer=" + buyer + ", text=" + text);
            }
            catch (Exception ex)
            {
                BotConnectionDiagnostics.RecordSendAttempt(false, ex.Message);
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

                    // 先清掉输入框中可能残留的上一条未发送文本，再粘贴本次 AI 回复。
                    PressCtrlA();
                    Thread.Sleep(80);
                    WinApi.PressCtrlV();

                    LastSetPlainText = text;
                    LatestSetTextTime = DateTime.Now;

                    DateTime now = DateTime.Now;
                    do
                    {
                        var editorText = GetEditorTextSafe();
                        if (!string.IsNullOrWhiteSpace(editorText))
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