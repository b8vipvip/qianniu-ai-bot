from __future__ import annotations

from pathlib import Path

from fastapi import HTTPException

import wecom_settings


def test_default_account_rule_distinguishes_purchase_from_security(tmp_path: Path):
    state = wecom_settings.load_handoff_rules(tmp_path / "rules.db")
    account = next(rule for rule in state["rules"] if rule["keyword"] == "账号")

    assert account["enabled"] is True
    assert account["rule_type"] == "confirm"
    assert account["match_mode"] == "sensitive_context"
    assert "密码" in account["risk_terms"]
    assert "验证码" in account["risk_terms"]
    assert "另一个账号" in account["exceptions"]
    assert "给朋友" in account["exceptions"]
    assert "再拍" in account["exceptions"]
    assert "可以给朋友或其他账号充值" in account["safe_reply"]


def test_rules_can_be_replaced_and_empty_list_does_not_reseed(tmp_path: Path):
    db_path = tmp_path / "rules.db"
    payload = wecom_settings.HandoffRuleSetInput(
        rules=[
            wecom_settings.HandoffRuleInput(
                enabled=True,
                rule_type="manual",
                keyword="特殊售后",
                match_mode="contains",
                note="服务端新增规则",
                sort_order=10,
            ),
            wecom_settings.HandoffRuleInput(
                enabled=True,
                rule_type="confirm",
                keyword="会员账号",
                match_mode="sensitive_context",
                risk_terms="密码|验证码",
                exceptions="给朋友|代充",
                safe_reply="可以代充，请下单后提供账号。",
                sort_order=20,
            ),
        ]
    )
    saved = wecom_settings.save_handoff_rules(payload, db_path)

    assert [rule["keyword"] for rule in saved["rules"]] == ["特殊售后", "会员账号"]
    assert saved["revision"]
    assert wecom_settings.load_handoff_rules(db_path)["rules"][1]["exceptions"] == "给朋友|代充"

    emptied = wecom_settings.save_handoff_rules(
        wecom_settings.HandoffRuleSetInput(rules=[]),
        db_path,
    )
    assert emptied["rules"] == []
    assert wecom_settings.load_handoff_rules(db_path)["rules"] == []


def test_duplicate_keywords_are_rejected(tmp_path: Path):
    data = wecom_settings.HandoffRuleSetInput(
        rules=[
            wecom_settings.HandoffRuleInput(keyword="账号"),
            wecom_settings.HandoffRuleInput(keyword="账号", rule_type="manual"),
        ]
    )
    try:
        wecom_settings.save_handoff_rules(data, tmp_path / "rules.db")
    except HTTPException as exc:
        assert exc.status_code == 400
        assert "关键词不能重复" in str(exc.detail)
    else:
        raise AssertionError("duplicate handoff keywords should fail")


def test_admin_page_and_runtime_endpoint_are_wired():
    root = Path(__file__).resolve().parents[1]
    page = (root / "static" / "wecom.html").read_text(encoding="utf-8")
    settings = (root / "wecom_settings.py").read_text(encoding="utf-8")

    assert "/api/admin/wecom/handoff-rules" in page
    assert "新增规则" in page
    assert "敏感语境词" in page
    assert "例外短语" in page
    assert "例外自动回复" in page
    assert '@router.get("/api/runtime/v1/handoff/rules")' in settings
    assert "Depends(require_runtime_client)" in settings
