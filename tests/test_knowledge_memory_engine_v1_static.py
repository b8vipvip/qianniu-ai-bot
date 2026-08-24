from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_memory_engine_keeps_authoritative_knowledge_as_source_of_truth():
    source = read("src/Bot/ChromeNs/KnowledgeMemoryEngine.cs")
    assert "ChatGPT-Memory-inspired local knowledge layer" in source
    assert "does not replace the authoritative knowledge base" in source
    assert "BotFeatureStore.GetKnowledgeBase()" in source
    assert "BuildCard" in source
    assert "KnowledgePolicyProfileService.GetProfile(entry)" in source
    assert "现有知识库已无损升级为Knowledge Memory Engine v1派生记忆卡" in source


def test_memory_engine_has_working_memory_conflict_detection_and_learned_reliability():
    source = read("src/Bot/ChromeNs/KnowledgeMemoryEngine.cs")
    assert "ConversationWorkingMemoryStore" in source
    assert "TimeSpan.FromMinutes(45)" in source
    assert "HasMaterialConflict" in source
    assert "ReliabilityScore" in source
    assert "memoryConfidence" in source
    assert "高分记忆之间存在实质答案冲突" in source
    assert "AnswersEquivalent" in source
    assert "近似同答案候选已合并判断" in source


def test_local_first_memory_can_bypass_ai_but_delegates_when_not_proven():
    source = read("src/Bot/ChromeNs/KnowledgeMemoryEngine.cs")
    assert "KnowledgeMemoryRuntimeBridge" in source
    assert "ReplyModeService.IsLocalFirst" in source
    assert "decision.CanDirectReply" in source
    assert "await inner(lease);" in source
    assert "SendTextWithRetryAsync" in source
    assert "本地记忆" in source
    assert "无AI调用" in source
    assert "KnowledgePolicyProfileService.RecordRouteSelection" in source


def test_memory_direct_gate_requires_meaning_confidence_margin_policy_and_safety():
    source = read("src/Bot/ChromeNs/KnowledgeMemoryEngine.cs")
    assert "DefaultDirectThreshold = 0.88" in source
    assert "DefaultMinConfidence = 0.70" in source
    assert "PolicyAllowsDirect" in source
    assert "effectiveMargin >= 0.055" in source
    assert "strongMeaning" in source
    assert "HighRiskRegex" in source
    assert "ConversationProgressGuardService.RequiresContextualHandling" in source


def test_memory_semantic_key_supports_paraphrases_instead_of_raw_wording_only():
    source = read("src/Bot/ChromeNs/KnowledgeMemoryEngine.cs")
    assert "DetectMemoryIntent" in source
    assert "Semantic-key boost" in source
    assert "currentMatched >= 2" in source
    assert "currentMatched >= 3 ? 0.92 : 0.89" in source
    assert 'return "capability"' in source
    assert "StripDemonstratives" in source


def test_memory_runtime_reasserts_outer_wrapper_after_streaming_pipeline():
    source = read("src/Bot/ChromeNs/KnowledgeMemoryEngine.cs")
    assert "MemoryHandlerWrapper" in source
    assert "current.Target is MemoryHandlerWrapper" in source
    assert "new Timer(_ => PatchExisting(), null, 700, 700)" in source
    assert "Memory remains the outermost text decision layer" in source


def test_memory_settings_are_shop_scoped_and_exportable_with_existing_knowledge_package():
    source = read("src/Bot/ChromeNs/KnowledgeMemoryEngine.cs")
    package = read("src/Bot/Knowledge/RulePolicyImportExportUi.cs")
    assert 'EnabledSettingsKey = "knowledge.memory_engine.enabled"' in source
    assert 'DirectThresholdSettingsKey = "knowledge.memory_engine.direct_threshold"' in source
    assert 'MinConfidenceSettingsKey = "knowledge.memory_engine.min_confidence"' in source
    assert "ShopScopedSettingsStore" in source
    assert 'StartsWith("knowledge.", StringComparison.OrdinalIgnoreCase)' in package


def test_memory_ui_exposes_status_rebuild_and_query_preview():
    source = read("src/Bot/Knowledge/KnowledgeMemoryEngineUi.cs")
    assert 'Header = "记忆引擎"' in source
    assert 'Content = "启用 Knowledge Memory Engine"' in source
    assert 'Content = "重建记忆索引"' in source
    assert 'Content = "测试记忆检索"' in source
    assert "KnowledgeMemoryEngine.FormatDecision" in source


def test_memory_engine_sources_are_compiled_for_bot_and_wpf_temp_projects():
    props = read("src/Bot/Directory.Build.props")
    assert "ChromeNs\\KnowledgeMemoryEngine.cs" in props
    assert "Knowledge\\KnowledgeMemoryEngineUi.cs" in props
    source = read("src/Bot/ChromeNs/KnowledgeMemoryEngine.cs")
    assert "_knowledgeMemoryEngineBootstrap" in source
