from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_history_scan_button_and_options():
    manager = text("src/Bot/Knowledge/KnowledgeManagerControl.cs")
    window = text("src/Bot/Knowledge/ChatHistoryScanWindow.cs")
    assert "扫描历史聊天记录" in manager
    assert "全部扫描" in window
    assert "按时间段扫描" in window
    assert "DatePicker" in window


def test_history_scan_opens_manager_and_reads_contacts():
    source = text("src/Bot/Knowledge/ChatHistoryScanService.cs")
    assert "TryOpenMessageManagerAsync" in source
    assert '"消息管理器"' in source
    assert "ReadVisibleMessageManagerContactsAsync" in source
    assert "mtop.taobao.wireless.amp2.im.relation.rebase" in source


def test_history_scan_pages_remote_history():
    source = text("src/Bot/Knowledge/ChatHistoryScanService.cs")
    assert "im.singlemsg.GetRemoteHisMsg" in source
    assert "msgid = cursorId" in source
    assert "msgtime = cursorTime" in source
    assert "MaxHistoryPages" in source


def test_history_scan_reuses_smart_import_batches():
    source = text("src/Bot/Knowledge/ChatHistoryScanService.cs")
    assert "KnowledgeAiService" in source
    assert "ImportAsync" in source
    assert "GetSmartImportTimeoutSeconds" in source
    assert '"历史聊天扫描"' in source


def test_history_scan_filters_and_redacts():
    source = text("src/Bot/Knowledge/ChatHistoryScanService.cs")
    assert "IsPlatformSystemTip" in source
    assert "IsWithdrawalNotice" in source
    assert "[手机号]" in source
    assert "[敏感编号]" in source
    assert "[API_KEY]" in source
    assert "IsUsefulPair" in source
