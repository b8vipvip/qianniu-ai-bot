from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_message_menu_has_resend_and_edit():
    source = text("src/Bot/AssistWindow/Widget/Robot/CtlConversation.xaml.cs")
    assert 'Header = "重发"' in source
    assert 'Header = "修改"' in source
    assert "EditRequested" in source


def test_local_first_and_source_labels():
    source = text("src/Bot/ChromeNs/MyOpenAI.cs")
    assert "TryFindLocalAnswer" in source
    assert '"本地"' in source
    assert '"AI生成"' in source


def test_manual_reply_guard_and_learning():
    rpa = text("src/Bot/ChromeNs/QNRpa.cs")
    service = text("src/Bot/ChromeNs/KnowledgeLearningService.cs")
    assert "TryBlockForManualReply" in rpa
    assert "客服已人工回复" in service
    assert "人工回复" in service
    assert "AllowNextManualSend" in service


def test_learning_dedup_and_sensitive_redaction():
    source = text("src/Bot/ChromeNs/KnowledgeLearningService.cs")
    assert "ContentHash" in source
    assert "人工确认答案更新知识库" in source
    assert "[手机号]" in source
    assert "[API_KEY]" in source


def test_knowledge_manager_refreshes_after_learning():
    source = text("src/Bot/Knowledge/KnowledgeManagerControl.cs")
    assert "KnowledgeBaseChanged" in source
    assert "OnKnowledgeBaseChanged" in source
