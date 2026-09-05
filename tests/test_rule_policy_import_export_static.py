from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "Bot" / "Knowledge" / "RulePolicyImportExportUi.cs"
PROPS = ROOT / "src" / "Bot" / "Directory.Build.props"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def test_store_rule_center_has_versioned_json_import_export():
    source = read(SOURCE)
    assert '"qnbot.store-rules"' in source
    assert 'Title = "导入店铺规则"' in source
    assert 'Title = "导出店铺规则"' in source
    assert 'StorePromptProfileService.ParseRulesJson' in source
    assert 'StorePromptProfileService.SaveStructured(raw, core, rules)' in source


def test_store_rule_import_backs_up_before_overwrite():
    source = read(SOURCE)
    start = source.index("private static void ImportStoreRules")
    backup = source.index("var backup = BackupStoreRules(window)", start)
    save = source.index("StorePromptProfileService.SaveStructured(raw, core, rules)", backup)
    assert backup < save
    assert "导入前会自动备份当前配置" in source


def test_knowledge_policy_export_contains_complete_config_and_learning_stats():
    source = read(SOURCE)
    start = source.index("private static JObject BuildPolicyExportObject")
    end = source.index("private static string BackupKnowledgePolicies", start)
    block = source[start:end]
    assert '"qnbot.knowledge-policies"' in source
    assert '"answerMode"' in block
    assert '"confidence"' in block
    assert "DirectSelectedCount" in block
    assert "ContextualSelectedCount" in block
    assert "AcceptedCount" in block
    assert "SellerCorrectionCount" in block
    assert "SellerWithdrawCount" in block
    assert '"enabled"' in block


def test_knowledge_policy_import_merges_and_restores_learning_stats():
    source = read(SOURCE)
    assert "FindKnowledgeForImport" in source
    assert "KnowledgePolicyProfileService.ImportCompleteProfile(entry, imported)" in source
    assert "不会删除现有策略" in source
    assert "完整恢复配置和可靠度学习统计" in source
    assert "var backup = BackupKnowledgePolicies(window)" in source


def test_import_validates_schema_and_version_and_reports_counts():
    source = read(SOURCE)
    assert "ValidateSchema(root, StoreSchema)" in source
    assert "ValidateSchema(root, PolicySchema)" in source
    assert "不支持的导入文件版本" in source
    assert '"\\n成功更新：" + updated' in source
    assert '"\\n未找到对应知识：" + skipped' in source
    assert '"\\n无效记录：" + invalid' in source


def test_ui_extension_is_compiled_for_bot_and_wpf_temp_projects():
    props = read(PROPS)
    assert "..\\Directory.Build.props" in props
    assert "Knowledge\\RulePolicyImportExportUi.cs" in props
    assert not (ROOT / "src" / "Bot" / "Directory.Build.targets").exists()
    source = read(SOURCE)
    assert "RulePolicyImportExportBootstrap" in source
    assert "EventManager.RegisterClassHandler" in source