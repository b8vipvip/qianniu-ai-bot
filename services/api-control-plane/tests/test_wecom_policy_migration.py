from pathlib import Path
import sys


ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

import wecom_policy_migration


def test_wecom_page_hides_old_policy_and_shows_windows_migration_notice():
    source = (ROOT / "static" / "wecom.html").read_text(encoding="utf-8-sig")
    result = wecom_policy_migration.transform_wecom_html(source)
    assert ".panel:has(#policyText)" in result
    assert 'id="handoffPolicyMigrationNotice"' in result
    assert "功能设置 → 消息通知 → 转人工通知 → 通知策略" in result
    assert "AI 转人工策略已迁移到 Windows Bot" in result
    assert "首次启动会自动读取一次旧服务端规则" in result


def test_deprecated_policy_state_contains_no_editable_server_rules():
    state = wecom_policy_migration.deprecated_policy_state()
    assert state["deprecated"] is True
    assert state["rules"] == []
    assert state["summary"]["total_rule_count"] == 0
    assert "Windows Bot" in state["message"]


def test_runtime_rule_endpoint_is_left_for_authenticated_one_time_migration():
    source = (ROOT / "wecom_policy_migration.py").read_text(encoding="utf-8-sig")
    assert 'path == "/api/runtime/v1/handoff/rules" and method == "GET"' in source
    assert "return await call_next(request)" in source
    assert "one-time migration" in source
    assert 'path == "/api/admin/wecom/handoff-rules"' in source
    assert "status_code=410" in source
