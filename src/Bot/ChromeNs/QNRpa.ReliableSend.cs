using Bot.Automation.ChatDeskNs;
using BotLib;
using FlaUI.Core.AutomationElements;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    public partial class QNRpa
    {
        internal const string ChatInputAutomationId = "UIWindow.mutilcentralwidget.stackedWidget.SingleChatView.centralwidget.stackedWidget.SubChatView.ChatDisplayWidget.ChatContentView.splitter.sendMsgWidget.chatInputArea.plainTextEdit";
        internal const string SendButtonAutomationId = "UIWindow.mutilcentralwidget.stackedWidget.SingleChatView.centralwidget.stackedWidget.SubChatView.ChatDisplayWidget.ChatContentView.splitter.sendMsgWidget.enterAreaKeyWidget.sendMsg";

        public string LastSendFailureReason { get; private set; } = string.Empty;

        internal void ResetSendFailure()
        {
            LastSendFailureReason = string.Empty;
        }

        internal void SetSendFailure(string stage, string detail)
        {
            stage = (stage ?? string.Empty).Trim();
            detail = (detail ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            LastSendFailureReason = string.IsNullOrWhiteSpace(detail) ? stage : stage + "：" + detail;
            BotConnectionDiagnostics.RecordSendAttempt(false, LastSendFailureReason);
            Log.Info("发送阶段失败: " + LastSendFailureReason);
        }

        internal void InvalidateChatControls()
        {
            _messageInputTextArea = null;
            _sendMessageButton = null;
            _preUpdateChatBrowserRectTime = DateTime.MinValue;
        }

        internal string GetSendFailureReason()
        {
            return string.IsNullOrWhiteSpace(LastSendFailureReason) ? "未知发送失败" : LastSendFailureReason;
        }

        internal async Task<bool> RefreshChatControlsAsync(bool force)
        {
            if (!force
                && _messageInputTextArea != null
                && (DateTime.Now - _preUpdateChatBrowserRectTime).TotalSeconds < 3)
            {
                return true;
            }

            _preUpdateChatBrowserRectTime = DateTime.Now;
            if (Desk.Inst == null)
            {
                SetSendFailure("UIA扫描", "千牛接待台实例不存在");
                return false;
            }
            if (!Desk.Inst.IsVisibleAndNotMinimized)
            {
                try { Desk.Inst.Show(); }
                catch (Exception ex) { Log.Info("显示千牛接待台失败: " + ex.Message); }
            }
            if (automationApplication == null || automationApplication.MainWindowHandle.ToInt64() < 1)
            {
                SetSendFailure("UIA扫描", "千牛主窗口句柄无效");
                return false;
            }

            return await Task.Run(() =>
            {
                try
                {
                    var topWnds = automationApplication.GetAllTopLevelWindows(uia3Automation);
                    var mainWnd = topWnds.FirstOrDefault(k => string.Equals(k.ClassName, "MutilChatView", StringComparison.Ordinal));
                    if (mainWnd == null)
                    {
                        InvalidateChatControls();
                        SetSendFailure("UIA扫描", "未找到 MutilChatView");
                        BotConnectionDiagnostics.RecordRpaScan(false, false, "未找到MutilChatView");
                        return false;
                    }

                    var descendants = mainWnd.FindAllDescendants();
                    var inputElement = descendants.FirstOrDefault(k => string.Equals(SafeAutomationId(k), ChatInputAutomationId, StringComparison.Ordinal))
                        ?? descendants.FirstOrDefault(k => string.Equals(SafeClassName(k), "TextRichEdit", StringComparison.Ordinal));
                    var sendElement = descendants.FirstOrDefault(k => string.Equals(SafeAutomationId(k), SendButtonAutomationId, StringComparison.Ordinal))
                        ?? descendants.FirstOrDefault(k => IsSendButtonName(SafeName(k)));

                    _messageInputTextArea = inputElement == null ? null : inputElement.AsTextBox();
                    _sendMessageButton = sendElement;
                    var inputFound = _messageInputTextArea != null;
                    var sendFound = _sendMessageButton != null;
                    BotConnectionDiagnostics.RecordRpaScan(sendFound, inputFound,
                        "UIA稳定扫描 input=" + inputFound + ", send=" + sendFound);
                    if (!inputFound)
                    {
                        SetSendFailure("UIA扫描", "未找到聊天输入框；AutomationId=" + ChatInputAutomationId);
                    }
                    else
                    {
                        Log.Info("UIA控件刷新成功: inputAutomationId=" + SafeAutomationId(inputElement)
                            + ", sendAutomationId=" + SafeAutomationId(sendElement));
                    }
                    return inputFound;
                }
                catch (Exception ex)
                {
                    InvalidateChatControls();
                    SetSendFailure("UIA扫描异常", ex.Message);
                    Log.Exception(ex);
                    return false;
                }
            });
        }

        internal bool TryGetEditorText(out string text)
        {
            text = string.Empty;
            try
            {
                if (_messageInputTextArea == null) return false;
                text = _messageInputTextArea.Text ?? string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                Log.Info("读取输入框失败，控件可能已失效: " + ex.Message);
                InvalidateChatControls();
                return false;
            }
        }

        internal static bool EditorMatchesExpectedText(string actual, string expected)
        {
            return string.Equals(NormalizeEditorText(actual), NormalizeEditorText(expected), StringComparison.Ordinal);
        }

        internal bool HasExpectedDraft(string expected)
        {
            string text;
            return TryGetEditorText(out text) && EditorMatchesExpectedText(text, expected);
        }

        private static string NormalizeEditorText(string value)
        {
            return (value ?? string.Empty)
                .Replace("\u200B", string.Empty)
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Trim();
        }

        private static string SafeAutomationId(AutomationElement element)
        {
            try
            {
                return element != null && element.Properties.AutomationId.IsSupported
                    ? (element.AutomationId ?? string.Empty)
                    : string.Empty;
            }
            catch { return string.Empty; }
        }

        private static string SafeClassName(AutomationElement element)
        {
            try
            {
                return element != null && element.Properties.ClassName.IsSupported
                    ? (element.ClassName ?? string.Empty)
                    : string.Empty;
            }
            catch { return string.Empty; }
        }

        private static string SafeName(AutomationElement element)
        {
            try
            {
                return element != null && element.Properties.Name.IsSupported
                    ? (element.Name ?? string.Empty)
                    : string.Empty;
            }
            catch { return string.Empty; }
        }
    }
}
