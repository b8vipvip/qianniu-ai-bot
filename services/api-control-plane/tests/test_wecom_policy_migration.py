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


def test_deprecated_policy_state_contains_no_server_rules():
    state = wecom_policy_migration.deprecated_policy_state()
    assert state["deprecated"] is True
    assert state["rules"] == []
    assert state["summary"]["total_rule_count"] == 0
    assert "Windows Bot" in state["message"]
