from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "Bot" / "Knowledge" / "KnowledgePolicyProfileUi.cs"


def source_text() -> str:
    return SOURCE.read_text(encoding="utf-8-sig")


def test_policy_window_has_search_and_count():
    text = source_text()
    assert 'Title = "知识策略与可靠度"' in text
    assert 'private readonly TextBox _search;' in text
    assert 'private readonly TextBlock _filterStats;' in text
    assert '当前显示 ' in text
    assert 'ApplySearch();' in text


def test_policy_search_covers_knowledge_and_policy_fields():
    text = source_text()
    for field in (
        "profile.QuestionSnapshot",
        "profile.Intent",
        "profile.Entities",
        "profile.ApplyWhen",
        "profile.DoNotApplyWhen",
        "profile.RequiredContext",
        "profile.LastEvidenceType",
        "entry.Answer",
        "entry.Keywords",
        "entry.Category",
        "entry.SourceType",
    ):
        assert field in text


def test_original_question_wording_is_fuzzy_searchable():
    text = source_text()
    assert 'AttachManagerFuzzySearch(manager);' in text
    assert 'GetField("_search"' in text
    assert 'GetField("_all"' in text
    assert 'GetField("_view"' in text
    assert 'ManagerMatches(x, query)' in text
    assert '"用到"' in text
    assert '"上的"' in text
    assert 'terms.All(normalizedHaystack.Contains)' in text


def test_selected_policy_shows_actual_knowledge_record():
    text = source_text()
    assert '知识状态：' in text
    assert '；来源：' in text
    assert '知识更新时间：' in text
    assert '知识答案：' in text
