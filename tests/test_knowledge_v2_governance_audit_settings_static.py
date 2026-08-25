from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
AUDIT = (ROOT / "src/Bot/Knowledge/KnowledgeEngineV2GovernanceAuditService.cs").read_text(encoding="utf-8")
SERVICE = (ROOT / "src/Bot/Knowledge/KnowledgeEngineV2GovernanceService.cs").read_text(encoding="utf-8")
REVISION = (ROOT / "src/Bot/Knowledge/KnowledgeEngineV2RevisionService.cs").read_text(encoding="utf-8")
UI = (ROOT / "src/Bot/Knowledge/KnowledgeCenterV2GovernanceUi.cs").read_text(encoding="utf-8")
PROPS = (ROOT / "src/Bot/Directory.Build.props").read_text(encoding="utf-8")


def test_governance_audit_is_append_only_and_shop_scoped():
    assert "IShopScopedPathProvider Paths = new ShopScopedPathProvider()" in AUDIT
    assert "Stores.GetOrAdd(shop.ShopKey" in AUDIT
    assert 'Path.Combine(root, "knowledge-governance-v2.db")' in AUDIT
    assert "KnowledgeV2GovernanceAuditRow" in AUDIT
    assert "state.Db.SaveOneRecord(row)" in AUDIT
    assert "delete from knowledgev2governanceauditrow" not in AUDIT.lower()
    assert "KnowledgeEngineV2GovernanceAuditService.GetEntries(_seller, 500)" in UI


def test_governance_settings_are_per_shop_bounded_and_do_not_weaken_rollback_safety():
    for key in [
        "knowledge.engine_v2.governance.normal_verification_days",
        "knowledge.engine_v2.governance.high_risk_verification_days",
        "knowledge.engine_v2.governance.unused_stale_days",
    ]:
        assert key in AUDIT
    assert "new ShopScopedSettingsStore(shop, Paths)" in AUDIT
    assert "store.MergeValues(new Dictionary<string, string>" in AUDIT
    assert "MinNormalVerificationDays = 30" in AUDIT
    assert "MaxNormalVerificationDays = 730" in AUDIT
    assert "MinHighRiskVerificationDays = 7" in AUDIT
    assert "Math.Min(MaxHighRiskVerificationDays, after.NormalVerificationDays)" in AUDIT
    assert "MinUnusedStaleDays = 30" in AUDIT
    assert "governanceSettings.UnusedStaleDays" in SERVICE
    assert "AfterNegativeRate >= Math.Max(0.25, item.BeforeNegativeRate + 0.15)" in SERVICE
    assert "item.AfterNegative >= 2" in SERVICE
    assert "item.AfterSent < 3" in SERVICE


def test_human_governance_and_revision_actions_append_audit_entries():
    for action in ["mark_verified", "disable_knowledge", "rollback_revision"]:
        assert f'"{action}"' in SERVICE
    for action in ["generate_revision_candidates", "apply_revision", "reject_revision"]:
        assert f'"{action}"' in REVISION
    assert '"update_settings"' in AUDIT
    assert "KnowledgeEngineV2GovernanceAuditService.TryAppendAction" in SERVICE
    assert "KnowledgeEngineV2GovernanceAuditService.TryAppendAction" in REVISION


def test_audit_state_uses_answer_fingerprint_not_plain_answer_copy():
    describe = AUDIT.split("public static string DescribeRecord", 1)[1].split(
        "public static string DescribeSettings", 1
    )[0]
    assert '";answer_sha256=" + Sha256(record.Answer' in describe
    assert "record.Answer +" not in describe
    assert "SHA256.Create()" in AUDIT


def test_governance_ui_exposes_history_filters_and_confirmed_setting_save():
    for label in [
        'Header = "治理历史"',
        'Header = "治理设置"',
        'Btn("刷新历史"',
        'Btn("保存设置"',
        'Btn("填入默认值"',
        "操作前状态",
        "操作后状态",
    ]:
        assert label in UI
    save = UI.split("private void SaveGovernanceSettings", 1)[1].split(
        "private void LoadSettingsValues", 1
    )[0]
    assert "MessageBoxButton.YesNo" in save
    assert "KnowledgeEngineV2GovernanceAuditService.SaveSettings" in save
    assert "不会自动修改生产知识" in save


def test_governance_audit_source_compiles_for_normal_and_wpf_temp_projects():
    assert "Knowledge\\KnowledgeEngineV2GovernanceAuditService.cs" in PROPS
    assert "Knowledge\\KnowledgeEngineV2GovernanceService.cs" in PROPS
    assert "Knowledge\\KnowledgeCenterV2GovernanceUi.cs" in PROPS
    assert not (ROOT / "src/Bot/Directory.Build.targets").exists()
