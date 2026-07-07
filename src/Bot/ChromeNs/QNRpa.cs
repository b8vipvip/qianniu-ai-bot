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
                            if (mainWnd == null) return;

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
                        }
                        catch (Exception ex)
                        {
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

        private bool TryClickSendButton()
        {
            try
            {
                if (_sendMessageButton == null)
                {
                    UpdateChatBrowserRect(true);
                    Thread.Sleep(300);
                }

                if (_sendMessageButton != null)
                {
                    _sendMessageButton.Click();
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }

            // 兜底：如果千牛按钮文字变成繁体或 UIA 找不到按钮，则聚焦输入框后按 Enter。
            try
            {
                FocusEditor();
                WinApi.Api.keybd_event(0x0D, 0, 0, 0);
                Thread.Sleep(80);
                WinApi.Api.keybd_event(0x0D, 0, 2, 0);
                return true;
            }
            catch (Exception ex)
            {
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
                    var point = _messageInputTextArea.GetClickablePoint();
                    FlaUI.Core.Input.Mouse.Click(new System.Drawing.Point { X = point.X + 5, Y = point.Y + 5 });
                    isok = true;
                }
                catch (Exception e)
                {
                    Log.Exception(e);
                }
            });
            return isok;
        }

        public async Task SendTextAsync(string buyer, string text)
        {
            await OpenAndSendText(buyer, text);
        }

        private async Task<bool> OpenAndSendText(string buyer, string text)
        {
            bool sendResult = false;
            if (_qn.Buyer == null || _qn.Buyer.Nick != buyer)
            {
                _qn.OpenChat(buyer);
                await Task.Delay(500);
                await _qn.GetCurrentConversationID();
            }
            if (_qn.Buyer.Nick == buyer)
            {
                if (!Desk.Inst.IsVisible)
                {
                    Desk.Inst.Show();
                    Util.WaitFor(new Func<bool>(() => Desk.Inst.IsVisible), 3000, 10, false);
                }

                try
                {
                    if (!await _qn.IsInputboxEmpty())
                    {
                        Log.Info("千牛输入框已有内容，疑似人工正在编辑或上次内容未发送，跳过自动发送。buyer=" + buyer);
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    Log.Exception(ex);
                }

                _qn.InsertText2Inputbox(buyer, text);
                Thread.Sleep(600);
                sendResult = TryClickSendButton();
            }
            return sendResult;
        }

        private bool SetAndSendText(string text)
        {
            var isok = false;
            try
            {
            }
            catch (Exception e)
            {
                Log.Exception(e);
            }
            return isok;
        }
    }
}