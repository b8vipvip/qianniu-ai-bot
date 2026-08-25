from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
PREVIEW = (ROOT / "src/Bot/Knowledge/LegacyKnowledgePreviewWindow.cs").read_text(encoding="utf-8")
SETTINGS = (ROOT / "src/Bot/Options/FeatureSettingsOptionsControl.cs").read_text(encoding="utf-8")
PROPS = (ROOT / "src/Bot/Directory.Build.props").read_text(encoding="utf-8")
HELP = (ROOT / "src/Bot/Knowledge/KnowledgeCenterHelpWindow.cs").read_text(encoding="utf-8")
DOC = (ROOT / "docs/KNOWLEDGE_CENTER_V2_USER_HELP.md").read_text(encoding="utf-8")


def test_preview_is_reachable_from_client_knowledge_settings():
    assert 'Content = "旧版知识库预览"' in SETTINGS
    assert 'ToolTip = "只读查看当前店铺的旧版知识库，不启用旧版功能"' in SETTINGS
    assert "LegacyKnowledgePreviewWindow.MyShow(Window.GetWindow(this), Seller);" in SETTINGS


def test_preview_reads_a_snapshot_from_the_resolved_shop_scope():
    assert "ShopScopedUiBridge.Get(owner)" in PREVIEW
    assert "ShopContextLocator.ResolveRuntimeBySellerNick(effectiveSeller)" in PREVIEW
    assert "new ShopScopedSettingsStore(shop, Paths)" in PREVIEW
    assert "store.TryGetString(LegacyKnowledgeKey, out json)" in PREVIEW
    assert 'LegacyKnowledgeKey = "KnowledgeBaseJson"' in PREVIEW
    assert "BotFeatureStore.GetKnowledgeBase()" not in PREVIEW
    assert "ShopSettingsScope.Enter(" not in PREVIEW
    assert "source.Where(x => x != null).Select(PreviewRow.From).ToList()" in PREVIEW
    assert "ShopScopedUiBridge.Attach(window, shop);" in PREVIEW


def test_preview_grid_and_details_are_strictly_read_only():
    for guard in [
        "CanUserAddRows = false",
        "CanUserDeleteRows = false",
        "IsReadOnly = true",
        'Text = "条目详情（只读）"',
        "列表是打开/刷新时生成的只读快照",
    ]:
        assert guard in PREVIEW


def test_preview_does_not_mount_or_call_legacy_mutation_features():
    for forbidden in [
        "new KnowledgeManagerControl",
        "new KnowledgeImportControl",
        "new KnowledgeCenterWindow",
        "SaveKnowledgeBase(",
        "ImportKnowledgePackage(",
        "ExportKnowledgePackage(",
        "KnowledgeEngineV2Repository.Save(",
        "KnowledgeEngineV2Repository.ReplaceAll(",
        "KnowledgeEngineV2Service.RebuildFromLegacy(",
        ".SetString(",
        ".Remove(",
        ".ReplaceValues(",
        ".MergeValues(",
        "KnowledgeLearningService",
    ]:
        assert forbidden not in PREVIEW

    for notice in [
        "仅供预览参考",
        "不会保存、编辑、删除、导入、导出、AI 优化",
        "不会启用旧版知识库的检索、匹配或自动回复功能",
        "仅展示，不代表已启用旧版运行时",
        "预览不会猜测旧全局数据归属",
    ]:
        assert notice in PREVIEW


def test_preview_source_ships_in_normal_and_wpf_temporary_builds():
    assert "Knowledge\\LegacyKnowledgePreviewWindow.cs" in PROPS
    assert not (ROOT / "src/Bot/Directory.Build.targets").exists()


def test_help_documents_preview_scope_and_non_activation_contract():
    for source in (HELP, DOC):
        assert "旧版知识库预览" in source
        assert "当前 ShopKey" in source
        assert "不会启用旧版检索、匹配或自动回复" in source
