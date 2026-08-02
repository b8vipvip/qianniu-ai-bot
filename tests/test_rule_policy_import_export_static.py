from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "Bot" / "Knowledge" / "RulePolicyImportExportUi.cs"
TARGETS = ROOT / "src" / "Bot" / "Directory.Build.targets"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def test_store_rule_center_has_versioned_json_import_export():
    source = read(SOURCE)
    assert '"qianniu-ai-bot.store-rules"' in source
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


def test_knowledge_policy_export_contains_only_portable_fields():
    source = read(SOURCE)
    start = source.index("private static JObject BuildPolicyExportObject")
    end = source.index("private static string BackupKnowledgePolicies", start)
    block = source[start:end]
    assert '"qianniu-ai-bot.knowledge-policies"' in source
    assert '"answerMode"' in block
    assert '"confidence"' in block
    assert "DirectSelectedCount" not in block
    assert "ContextualSelectedCount" not in block
    assert "SellerCorrectionCount" not in block
    assert "SellerWithdrawCount" not in block


def test_knowledge_policy_import_is_merge_only_and_preserves_learning_stats():
    source = read(SOURCE)
    assert "FindKnowledgeForImport" in source
    assert "KnowledgePolicyProfileService.SaveProfile(entry, imported)" in source
    assert "不会删除现有策略" in source
    assert "可靠度学习统计也不会被覆盖" in source
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
    targets = read(TARGETS)
    assert "..\\Directory.Build.targets" in targets
    assert "Knowledge\\RulePolicyImportExportUi.cs" in targets
    source = read(SOURCE)
    assert "RulePolicyImportExportBootstrap" in source
    assert "EventManager.RegisterClassHandler" in source
