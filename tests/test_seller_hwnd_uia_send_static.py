from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
RELIABLE = ROOT / "src" / "Bot" / "ChromeNs" / "QNRpa.ReliableSend.cs"


def _source():
    return RELIABLE.read_text(encoding="utf-8-sig")


def test_uia_scan_uses_verified_seller_desk_hwnd_not_global_main_window():
    text = _source()
    assert "var sellerDesk = ResolveSellerDesk();" in text
    assert "EnsureSellerDeskBinding(false)" in text
    assert "uia3Automation.FromHandle(new IntPtr(expectedHwnd))" in text
    assert "automationApplication.GetAllTopLevelWindows" not in text
    assert 'ClassName, "MutilChatView"' not in text


def test_new_qianniu_window_class_is_not_a_hard_gate():
    text = _source()
    assert "FindChatInputElement(mainWnd, descendants)" in text
    assert 'SafeClassName(k), "TextRichEdit"' in text
    assert 'EndsWith("sendMsgWidget.chatInputArea.plainTextEdit"' in text
    assert "当前客服千牛窗口内未找到聊天输入框" in text


def test_send_button_fallback_stays_inside_the_same_seller_hwnd_tree():
    text = _source()
    assert "FindSendButtonElement(descendants, inputElement)" in text
    assert "IsSendButtonName(SafeName(k))" in text
    assert 'EndsWith(\n                    "sendMsgWidget.enterAreaKeyWidget.sendMsg"' in text


def test_seller_window_scan_remains_fail_closed_when_binding_is_ambiguous():
    text = _source()
    assert "未找到当前客服唯一对应的千牛接待窗口" in text
    assert "当前客服的 RPA/千牛窗口绑定尚未就绪" in text
    assert "Desk.Inst" not in text
