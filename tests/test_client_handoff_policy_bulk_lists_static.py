from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_handoff_policy_is_local_and_no_longer_polls_server():
    source = read("src/Bot/ChromeNs/HandoffRuleRemoteConfigService.cs")
    assert "handoff-policy.json" in source
    assert "HandoffPolicyUiBridge.Initialize" in source
    assert "BulkListManagementUi.Initialize" in source
    assert "/api/runtime/v1/handoff/rules" not in source
    assert "PollLoopAsync" not in source
    assert "HttpClient" not in source
    assert "不再访问服务端规则接口" in source


def test_notification_tab_gets_local_policy_button_and_manager():
    source = read("src/Bot/Options/HandoffPolicyUi.cs")
    assert 'FindText(window, "转人工通知")' in source
    assert 'Content = "通知策略"' in source
    assert "AI转人工通知策略（本机）" in source
    assert "覆盖全部" in source
    assert "合并更新" in source
    assert "仅追加" in source
    for label in ("全选", "取消全选", "删除所选", "清空全部", "导入", "导出"):
        assert label in source


def test_all_knowledge_rule_lists_have_bulk_management_controls():
    source = read("src/Bot/Knowledge/BulkListManagementUi.cs")
    assert "KnowledgeManagerControl" in source
    assert "KnowledgePolicyProfileWindow" in source
    assert "StorePromptProfileWindow" in source
    assert "StoreRuleListWindow" in source
    assert "导入（多模式）" in source
    assert "全选当前" in source
    assert "删除所选" in source
    assert "清空全部" in source
    assert "BulkImportMode.Replace" in source
    assert "BulkImportMode.Merge" in source
    assert "BulkImportMode.Append" in source
    assert "knowledge-before-import" in source
    assert "knowledge-policies-before-import" in source
    assert "可靠度学习统计保留" in source


def test_new_windows_files_are_included_in_all_wpf_builds():
    props = read("src/Bot/Directory.Build.props")
    assert "Knowledge\\BulkListManagementUi.cs" in props
    assert "Options\\HandoffPolicyUi.cs" in props


def test_handoff_notification_still_applies_local_safe_exceptions():
    source = read("src/Bot/ChromeNs/HandoffNotificationService.cs")
    assert "HandoffRuleRemoteConfigService.TryApplySafeAutoReply" in source


def test_server_no_longer_registers_or_packages_handoff_policy_engine():
    bootstrap = read("services/api-control-plane/bootstrap.py")
    dockerfile = read("services/api-control-plane/Dockerfile")
    assert "wecom_policy_migration.install(control_plane)" in bootstrap
    assert "import wecom_handoff_policy" not in bootstrap
    assert "wecom_handoff_policy.router" not in bootstrap
    assert "init_handoff_policy_db" not in bootstrap
    assert "wecom_policy_migration.py" in dockerfile
    assert "wecom_handoff_policy.py" not in dockerfile
