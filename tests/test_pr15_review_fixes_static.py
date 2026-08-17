from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_text_send_fails_closed_without_exact_draft_or_target_buyer():
    qnrpa = read("src/Bot/ChromeNs/QNRpa.cs")
    native = read("src/Bot/ChromeNs/QNRpa.NativeSend.cs")
    reliable = read("src/Bot/ChromeNs/QNRpa.ReliableSend.cs")

    assert "VerifyCurrentBuyerAsync" in qnrpa
    assert '"写入前会话确认"' in qnrpa
    assert '"发送前会话确认"' in qnrpa
    assert '"发送前文本确认"' in qnrpa
    assert "HasExpectedDraftFastAsync" in qnrpa
    assert "输入框内容已变化或无法确认，已阻止发送" in qnrpa
    assert "写入前无法确认输入框是否为空，为避免覆盖人工草稿已停止发送" in qnrpa
    assert "输入框已有非本次Bot草稿，已阻止覆盖/追加发送" in qnrpa
    assert "UIA写入确认" in qnrpa
    open_send = qnrpa[qnrpa.index("private async Task<bool> OpenAndSendText"):]
    assert "SetPlainText(text)" not in open_send
    assert "TrySendTextNativeFirstAsync" in open_send
    assert "TrySendTextViaUiaAsync(buyer, text, sendStart)" in native
    assert native.count("HasExpectedDraftFastAsync(text") >= 4
    assert "if (string.IsNullOrEmpty(expected))" in reliable


def test_order_auto_reply_cannot_bypass_bot_master_switch():
    source = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    master_switch = source.index("if (!Params.Robot.CanUseRobotReal) return false;")
    config_read = source.index("BotFeatureStore.GetAutoReplyRules()")
    assert master_switch < config_read


def test_real_flow_test_never_falls_back_to_another_seller():
    source = read("src/Bot/ChromeNs/BotFlowTestService.cs")
    assert "QN.FindExistingBySellerNick(candidate.Seller);" in source
    assert "?? QN.CurQN" not in source
    assert "已阻止回退到其他店铺" in source
    assert "已阻止跨店铺执行测试" in source


def test_progress_tracker_does_not_delete_newer_buyer_message_state():
    source = read("src/Bot/ChromeNs/ResponseProgressTracker.cs")
    assert "AnswerReadyAt" in source
    assert "Entries.TryUpdate(key, replacement, entry)" in source
    assert "if (entry.AnswerReadyAt == DateTime.MinValue) return;" in source
    assert "ShouldDeferUnsupportedMediaCard" in source
    assert "SetExactQuestion" in source
