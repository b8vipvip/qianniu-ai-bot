from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
QNRPA = ROOT / "src/Bot/ChromeNs/QNRpa.cs"
TEST = ROOT / "tests/test_first_inquiry_fixed_reply_static.py"
DOC = ROOT / "docs/IMSDK_DIRECT_SEND_DISCOVERY.md"


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected exactly one match, got {count}")
    return text.replace(old, new, 1)


source = QNRPA.read_text(encoding="utf-8-sig")
source = replace_once(
    source,
    """        private AutomationElement _sendMessageButton;\n        private System.Drawing.Rectangle _sendMessageButtonRect;\n        private AutomationElement _closeContactButton;""",
    """        private AutomationElement _sendMessageButton;\n        private System.Drawing.Rectangle _sendMessageButtonRect;\n        private bool _lastSendButtonCoordinateClickRejected;\n        private AutomationElement _closeContactButton;""",
    "coordinate rejection field",
)

source = replace_once(
    source,
    """        private bool TryClickCachedSendButtonNow()\n        {\n            if (_sendMessageButton == null && _sendMessageButtonRect.IsEmpty) return false;""",
    """        private bool TryClickCachedSendButtonNow()\n        {\n            _lastSendButtonCoordinateClickRejected = false;\n            if (_sendMessageButton == null && _sendMessageButtonRect.IsEmpty) return false;""",
    "click reset",
)

source = replace_once(
    source,
    """            catch (Exception ex)\n            {\n                Log.Info(\"发送主按钮坐标点击异常: \" + ex.Message);\n                return false;\n            }\n        }\n\n        private async Task<bool> TrySendTextViaUiaAsync(string buyer, string text, DateTime sendStart)""",
    """            catch (Exception ex)\n            {\n                _lastSendButtonCoordinateClickRejected = true;\n                Log.Info(\"发送主按钮坐标点击异常: \" + ex.Message\n                    + \", type=\" + ex.GetType().FullName\n                    + \", hresult=0x\" + ex.HResult.ToString(\"X8\"));\n                return false;\n            }\n        }\n\n        private bool TryInvokeCachedSendButtonNow()\n        {\n            if (_sendMessageButton == null) return false;\n            try\n            {\n                _sendMessageButton.AsButton().Invoke();\n                return true;\n            }\n            catch (Exception ex)\n            {\n                Log.Info(\"发送按钮UIA回退Invoke失败: \" + ex.Message\n                    + \", type=\" + ex.GetType().FullName\n                    + \", hresult=0x\" + ex.HResult.ToString(\"X8\"));\n                return false;\n            }\n        }\n\n        private async Task<bool> TrySendTextViaUiaAsync(string buyer, string text, DateTime sendStart)""",
    "guarded invoke helper",
)

old_send = """                // UIA is used only to locate and cache the verified seller-window send rectangle.\n                // Do not call Invoke() on Qianniu's split button: on current builds that semantic\n                // action can block and/or open the send-mode dropdown instead of sending.\n                if ((_sendMessageButton == null || _sendMessageButtonRect.IsEmpty)\n                    && !await RefreshChatControlsAsync(true).ConfigureAwait(false))\n                {\n                    return false;\n                }\n                if (_sendMessageButton == null || _sendMessageButtonRect.IsEmpty)\n                {\n                    SetSendFailure(\"UIA主发送\", \"当前卖家千牛窗口内未找到可点击的发送主按钮区域\");\n                    return false;\n                }\n                if (!await HasExpectedDraftFastAsync(text, 1000).ConfigureAwait(false))\n                {\n                    SetSendFailure(\"UIA主发送\", \"发送前无法确认输入框仍为本次目标文本\");\n                    return false;\n                }\n\n                Log.Info(\"UIA定位完成，开始点击发送主按钮左侧区域: seller=\" + SellerNick\n                    + \", buyer=\" + buyer + \", text=\" + text);\n                var clicked = await RunUiActionAsync(\n                    () => TryClickCachedSendButtonNow(),\n                    \"发送主按钮坐标点击\",\n                    UiActionTimeoutMs).ConfigureAwait(false);\n                if (!clicked)\n                {\n                    SetSendFailure(\"发送主按钮坐标点击\", \"未能点击已验证发送按钮的左侧主操作区域\");\n                    return false;\n                }\n                return await WaitForTextSendConfirmedAsync(\n                    buyer, text, sendStart, \"发送主按钮坐标\", 3600).ConfigureAwait(false);"""

new_send = """                // Keep the verified left side of Qianniu's split send button as the primary\n                // action. Some Windows integrity/session combinations reject FlaUI's physical\n                // coordinate injection with Win32 access denied even though UIA read/write works.\n                // In that specific pre-action failure only, revalidate the owned draft and use a\n                // single UIA Invoke fallback. Never issue a second action after a coordinate click\n                // was accepted but delivery confirmation is merely late/ambiguous.\n                if ((_sendMessageButton == null || _sendMessageButtonRect.IsEmpty)\n                    && !await RefreshChatControlsAsync(true).ConfigureAwait(false))\n                {\n                    return false;\n                }\n                if (_sendMessageButton == null || _sendMessageButtonRect.IsEmpty)\n                {\n                    SetSendFailure(\"UIA主发送\", \"当前卖家千牛窗口内未找到可点击的发送主按钮区域\");\n                    return false;\n                }\n                if (!await HasExpectedDraftFastAsync(text, 1000).ConfigureAwait(false))\n                {\n                    SetSendFailure(\"UIA主发送\", \"发送前无法确认输入框仍为本次目标文本\");\n                    return false;\n                }\n\n                Log.Info(\"UIA定位完成，开始点击发送主按钮左侧区域: seller=\" + SellerNick\n                    + \", buyer=\" + buyer + \", text=\" + text);\n                var clicked = await RunUiActionAsync(\n                    () => TryClickCachedSendButtonNow(),\n                    \"发送主按钮坐标点击\",\n                    UiActionTimeoutMs).ConfigureAwait(false);\n                if (clicked)\n                {\n                    return await WaitForTextSendConfirmedAsync(\n                        buyer, text, sendStart, \"发送主按钮坐标\", 3600).ConfigureAwait(false);\n                }\n\n                if (!_lastSendButtonCoordinateClickRejected)\n                {\n                    SetSendFailure(\"发送主按钮坐标点击\", \"未能点击已验证发送按钮的左侧主操作区域\");\n                    return false;\n                }\n\n                Log.Info(\"发送主按钮坐标输入被系统拒绝，准备仅回退一次UIA Invoke: seller=\"\n                    + SellerNick + \", buyer=\" + buyer);\n\n                // Fail closed if the draft changed/disappeared while the coordinate action failed.\n                // It may mean the click actually reached Qianniu before the input API reported an\n                // exception. In that case only observe delivery; never perform a second send action.\n                if (!await HasExpectedDraftFastAsync(text, 900).ConfigureAwait(false))\n                {\n                    Log.Info(\"坐标点击异常后目标草稿已不存在或无法确认，禁止UIA二次动作: buyer=\" + buyer);\n                    return await WaitForTextSendConfirmedAsync(\n                        buyer, text, sendStart, \"坐标点击异常后确认\", 1800).ConfigureAwait(false);\n                }\n\n                var invoked = await RunUiActionAsync(\n                    () => TryInvokeCachedSendButtonNow(),\n                    \"发送按钮UIA回退调用\",\n                    UiActionTimeoutMs).ConfigureAwait(false);\n                if (!invoked)\n                {\n                    SetSendFailure(\"发送按钮UIA回退\", \"坐标输入被系统拒绝且UIA Invoke未完成\");\n                    return false;\n                }\n\n                return await WaitForTextSendConfirmedAsync(\n                    buyer, text, sendStart, \"发送按钮UIA回退\", 3600).ConfigureAwait(false);"""
source = replace_once(source, old_send, new_send, "text send fallback")
QNRPA.write_text(source, encoding="utf-8-sig")


test = TEST.read_text(encoding="utf-8-sig")n