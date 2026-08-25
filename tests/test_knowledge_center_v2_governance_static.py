from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SERVICE = (ROOT / "src/Bot/Knowledge/KnowledgeEngineV2GovernanceService.cs").read_text(encoding="utf-8")
UI = (ROOT / "src/Bot/Knowledge/KnowledgeCenterV2GovernanceUi.cs").read_text(encoding="utf-8")
BOOTSTRAP = (ROOT / "src/Bot/Knowledge/KnowledgeEngineV2GovernanceBootstrap.cs").read_text(encoding="utf-8")
PROPS = (ROOT / "src/Bot/Directory.Build.props").read_text(encoding="utf-8")


def test_governance_scan_covers_priority_issue_classes():
    assert '"rollback_recommended"' in SERVICE
    assert '"conflict"' in SERVICE
    assert '"low_quality"' in SERVICE
    assert '"multiple_pending_revision"' in SERVICE
    assert '"pending_revision"' in SERVICE
    assert '"verification_due"' in SERVICE
    assert '"unused_stale"' in SERVICE
    assert '"stale_revision"' in SERVICE


def test_governance_uses_stricter_verification_for_high_risk_knowledge():
    assert "NormalVerificationDays = 180" in SERVICE
    assert "HighRiskVerificationDays = 60" in SERVICE
    assert "IsHighRisk(record) ? HighRiskVerificationDays : NormalVerificationDays" in SERVICE


def test_revision_effect_comparison_uses_real_feedback_windows():
    assert "ImpactWindowDays = 30" in SERVICE
    assert "KnowledgeEngineV2FeedbackService.GetRecentEvents" in SERVICE
    assert 'CountType(before, "sent")' in SERVICE
    assert 'CountType(after, "sent")' in SERVICE
    assert "CountNegative(before)" in SERVICE
    assert "CountNegative(after)" in SERVICE
    assert "AfterNegativeRate >= Math.Max(0.25, item.BeforeNegativeRate + 0.15)" in SERVICE
    assert "item.AfterNegative >= 2" in SERVICE
    assert "item.AfterSent < 3" in SERVICE


def test_rollback_is_human_triggered_and_refuses_to_overwrite_later_edits():
    assert "public static bool RollbackRevision" in SERVICE
    assert "NormalizeComparable(record.Answer)" in SERVICE
    assert "NormalizeComparable(candidate.ProposedAnswer)" in SERVICE
    assert "为避免覆盖后续人工修改，已拒绝自动回滚" in SERVICE
    assert "record.Answer = candidate.OriginalAnswer.Trim();" in SERVICE
    assert "MessageBoxButton.YesNo" in UI
    assert "回滚所选修订" in UI


def test_governance_actions_do_not_delete_knowledge():
    assert "public static bool MarkVerified" in SERVICE
    assert "record.LastVerifiedAt = DateTime.Now;" in SERVICE
    assert "public static bool DisableKnowledge" in SERVICE
    assert "record.Enabled = false;" in SERVICE
    assert "record.Status = \"disabled\";" in SERVICE
    assert "KnowledgeEngineV2Repository.Delete" not in SERVICE


def test_governance_dashboard_exposes_queue_revision_effects_and_safe_actions():
    assert 'Content = "治理"' in UI
    assert 'Header = "治理队列"' in UI
    assert 'Header = "修订效果"' in UI
    assert 'Btn("生成修订候选"' in UI
    assert 'Btn("打开修订"' in UI
    assert 'Btn("确认仍有效"' in UI
    assert 'Btn("停用所选"' in UI
    assert 'Btn("回滚所选修订"' in UI
    assert "KnowledgeV2RevisionWindow" in UI


def test_revision_review_and_governance_bridges_are_bootstrapped_for_app():
    assert "KnowledgeV2RevisionUiBridge.Initialize()" in BOOTSTRAP
    assert "KnowledgeV2GovernanceUiBridge.Initialize()" in BOOTSTRAP
    assert "public partial class App" in BOOTSTRAP
    assert "InitializeForApp()" in BOOTSTRAP


def test_legacy_msbuild_and_wpf_temp_projects_receive_governance_sources():
    assert "..\\Directory.Build.props" in PROPS
    assert "Knowledge\\KnowledgeEngineV2GovernanceService.cs" in PROPS
    assert "Knowledge\\KnowledgeCenterV2GovernanceUi.cs" in PROPS
    assert "Knowledge\\KnowledgeEngineV2GovernanceBootstrap.cs" in PROPS
    assert not (ROOT / "src/Bot/Directory.Build.targets").exists()
