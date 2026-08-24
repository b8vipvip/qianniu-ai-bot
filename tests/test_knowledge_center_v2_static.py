from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_v2_uses_sqlite_repository_and_structured_indexes():
    repo = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    index = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Index.cs")
    models = read("src/Bot/Knowledge/KnowledgeEngineV2.Models.cs")
    assert 'knowledge-center-v2.db' in repo
    assert 'SQLiteHelper' in repo
    assert 'Dictionary<string, HashSet<int>> Exact' in read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs")
    assert 'Dictionary<string, HashSet<int>> Predicate' in read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs")
    assert 'snapshot.Ngram' in index
    assert 'public string Predicate { get; set; }' in models
    assert 'public string Subject { get; set; }' in models


def test_v2_query_does_not_periodically_rebuild_full_index():
    index = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Index.cs")
    public = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs")
    assert 'ExpiresAt' not in public.split('private sealed class Snapshot', 1)[1].split('}', 1)[0]
    assert 'Snapshots.TryGetValue(shop.ShopKey' in index
    assert 'DateTime.Now.AddMinutes(10)' not in index
    assert 'Recall(snapshot, query)' in public


def test_conflict_is_scoped_to_same_fact_key():
    index = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Index.cs")
    semantics = read("src/Bot/Knowledge/KnowledgeEngineV2.Semantics.cs")
    assert 'KnowledgeEngineV2Semantics.FactKey(best.Record)' in index
    assert 'KnowledgeEngineV2Semantics.FactKey(second.Record)' in index
    assert 'Compact(record.Subject) + "|" + NormalizePredicate(record.Predicate)' in semantics


def test_working_memory_only_supplements_context_dependent_messages():
    semantics = read("src/Bot/Knowledge/KnowledgeEngineV2.Semantics.cs")
    assert 'if (query.ContextDependent && memory != null' in semantics
    assert 'if (query.Entities.Count == 0 && memory.Entities != null)' in semantics
    assert 'if (query.Predicate == "general") query.Predicate = memory.Predicate;' in semantics


def test_v2_runtime_retires_memory_v1_and_preserves_safe_send_path():
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    assert 'StopLegacyMemoryTimer();' in runtime
    assert 'StripLegacyMemoryWrapper' in runtime
    assert 'ReplyModeService.IsLocalFirst' in runtime
    assert 'ParallelReplyRelevanceGate.ShouldSend' in runtime
    assert 'SendTextWithRetryAsync' in runtime


def test_learning_candidates_require_explicit_approval_before_direct_reply():
    public = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs")
    bridge = read("src/Bot/ChromeNs/KnowledgeEngineV2LearningBridge.cs")
    assert 'string.Equals(best.Record.Type, "learning_candidate"' in public
    assert '&& !unapprovedLearning' in public
    assert 'record.Type = "learning_candidate";' in bridge
    assert 'record.Status = "candidate";' in bridge
    assert 'KnowledgeV2Record existing = null;' in bridge


def test_new_knowledge_center_has_required_navigation_and_debugger():
    ui = read("src/Bot/Knowledge/KnowledgeCenterV2Ui.cs")
    ops = read("src/Bot/Knowledge/KnowledgeCenterV2OperationsPages.cs")
    for label in ["知识", "商品知识", "流程", "学习", "冲突", "测试台", "导入导出", "设置"]:
        assert f'Nav("{label}"' in ui
    assert '30次性能测试' in ops
    assert 'P95 ≤ 50ms' in ops
    assert '导出V2完整包' in ops
    assert '导入V2完整包' in ops
    assert 'CreateAutomaticBackup' in ops


def test_build_props_compiles_all_v2_sources():
    props = read("src/Bot/Directory.Build.props")
    for name in [
        "KnowledgeEngineV2.Models.cs",
        "KnowledgeEngineV2.Repository.cs",
        "KnowledgeEngineV2.Semantics.cs",
        "KnowledgeEngineV2.Service.Index.cs",
        "KnowledgeEngineV2.Service.Public.cs",
        "KnowledgeCenterV2Ui.cs",
        "KnowledgeCenterV2RecordsPage.cs",
        "KnowledgeCenterV2OperationsPages.cs",
        "KnowledgeEngineV2RuntimeBridge.cs",
        "KnowledgeEngineV2LearningBridge.cs",
    ]:
        assert name in props
