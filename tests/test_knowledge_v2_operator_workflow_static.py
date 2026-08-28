from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(rel):
    return (ROOT / rel).read_text(encoding="utf-8-sig")


def test_manual_new_knowledge_is_one_sentence_ai_generated():
    svc = read("src/Bot/Knowledge/KnowledgeV2NaturalLanguageService.cs")
    ui = read("src/Bot/Knowledge/KnowledgeV2OperatorUiBridge.cs")
    assert "AI一句话新增" in ui
    assert "legacyAdd.Visibility = Visibility.Collapsed" in ui
    assert "MyOpenAI.CallStructuredChat" in svc
    for field in ["title", "type", "intent", "subject", "predicate", "entities", "aliases", "answer", "conditions", "exclusions", "required_context", "product_ids", "risk_level", "confidence", "authority", "status"]:
        assert '"' + field + '"' in svc
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


def test_operator_bridge_is_bootstrapped():
    bootstrap = read("src/Bot/Knowledge/KnowledgeEngineV2GovernanceBootstrap.cs")
    assert "KnowledgeV2OperatorUiBridge.Initialize" in bootstrap
