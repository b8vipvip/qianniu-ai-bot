from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(rel):
    return (ROOT / rel).read_text(encoding="utf-8-sig")


def test_manual_new_knowledge_is_one_sentence_ai_generated():
    svc = read("src/Bot/Knowledge/KnowledgeV2NaturalLanguageService.cs")
    ui = read("src/Bot/Knowledge/KnowledgeV2OperatorUiBridge.cs")
    assert "AI一句话新增" in ui
    assert 'legacyAdd.Visibility = Visibility.Collapsed' in ui
    assert "MyOpenAI.CallStructuredChat" in svc
    for field in [
        '"title"', '"type"', '"intent"', '"subject"', '"predicate"',
        '"entities"', '"aliases"', '"answer"', '"conditions"',
        '"exclusions"', '"required_context"', '"product_ids"',
        '"risk_level"', '"confidence"', '"authority"', '"status"'
    ]:
        assert field in svc
    assert 'SourceType = "manual_ai_generated"' in svc
    assert "KnowledgeEngineV2Repository.Save(seller, record)" in ui


def test_v1_history_chat_organizer_is_exposed_in_v2_and_synced_incrementally():
    ui = read("src/Bot/Knowledge/KnowledgeV2OperatorUiBridge.cs")
    delta = read("src/Bot/Knowledge/KnowledgeV2LegacyDeltaImportService.cs")
    assert "历史聊天整理" in ui
    assert "new ChatHistoryScanWindow" in ui
    assert "ImportMissingHistoryKnowledge" in ui
    assert '"历史聊天扫描"' in delta
    assert "KnowledgeEngineV2Repository.LoadAll" in delta
    assert "KnowledgeEngineV2Repository.Save" in delta
    assert "ResetFromLegacy" not in delta
    assert "ReplaceAll" not in delta


def test_legacy_preview_is_real_pre_v2_v1_shell_and_read_only():
    preview = read("src/Bot/Knowledge/LegacyKnowledgePreviewWindow.cs")
    assert "86e0138b2f2e4583530aaf0264b6215a8443f35e" in preview
    assert 'Header = "智能导入"' in preview
    assert 'Header = "问答管理"' in preview
    assert 'Header = "AI优化记录"' in preview
    assert 'Content = "导入知识库完整包"' in preview
    assert 'Content = "导出知识库完整包"' in preview
    assert "new KnowledgeImportControl" in preview
    assert "new KnowledgeManagerControl" in preview
    assert "new AiOptimizationHistoryControl" in preview
    assert "button.IsEnabled = false" in preview
    assert "text.IsReadOnly = true" in preview
    assert "LegacyKnowledgeKey" not in preview
    assert "PreviewRow" not in preview


def test_operator_extensions_are_compiled_and_bootstrapped():
    targets = read("src/Bot/Directory.Build.targets")
    bootstrap = read("src/Bot/Knowledge/KnowledgeEngineV2GovernanceBootstrap.cs")
    for name in [
        "KnowledgeV2NaturalLanguageService.cs",
        "KnowledgeV2OperatorUiBridge.cs",
        "KnowledgeV2LegacyDeltaImportService.cs",
    ]:
        assert name in targets
    assert "KnowledgeV2OperatorUiBridge.Initialize" in bootstrap
