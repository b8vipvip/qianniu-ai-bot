from __future__ import annotations

import json
import sys
from pathlib import Path
from types import SimpleNamespace

from fastapi import HTTPException

import wecom_handoff_policy
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


def test_policy_state_is_seeded_from_existing_rules(tmp_path: Path):
    state = wecom_handoff_policy.load_policy_state(tmp_path / "policy.db")

    assert "退款" in state["policy_text"]
    assert "账号" in state["policy_text"]
    assert state["summary"]["manual_count"] > 0
    assert state["summary"]["confirm_count"] > 0
    assert state["summary"]["safe_exception_count"] > 0
    assert state["can_rollback"] is False


def sample_ai_payload():
    return {
        "summary": {
            "manual": ["退款", "投诉"],
            "confirm": ["账号安全"],
            "safe_exceptions": ["另一个账号", "给朋友", "代充"],
        },
        "rules": [
            {
                "enabled": True,
                "rule_type": "manual",
                "keyword": "退款",
                "match_mode": "contains",
                "risk_terms": [],
                "exceptions": [],
                "safe_reply": "",
                "note": "退款问题必须转人工",
                "sort_order": 10,
            },
            {
                "enabled": True,
                "rule_type": "manual",
                "keyword": "投诉",
                "match_mode": "contains",
                "risk_terms": [],
                "exceptions": [],
                "safe_reply": "",
                "note": "投诉问题必须转人工",
                "sort_order": 20,
            },
            {
                "enabled": True,
                "rule_type": "confirm",
                "keyword": "账号",
                "match_mode": "sensitive_context",
                "risk_terms": ["密码", "验证码", "登录", "找回"],
                "exceptions": ["另一个账号", "其他账号", "给朋友", "代充", "充值"],
                "safe_reply": "可以的，可以给朋友或其他账号充值。",
                "note": "账号安全转人工，正常代充不转人工",
                "sort_order": 30,
            },
        ],
        "tests": [
            {"message": "我要退款", "expected": "handoff", "reason": "售后风险"},
            {"message": "另一个账号可以充值吗", "expected": "safe", "reason": "正常购买"},
        ],
    }


def test_ai_payload_normalization_and_safety_validation():
    normalized = wecom_handoff_policy.normalize_compiled_payload(sample_ai_payload())
    account = next(rule for rule in normalized["rules"] if rule["keyword"] == "账号")

    assert account["match_mode"] == "sensitive_context"
    assert account["risk_terms"] == "密码|验证码|登录|找回"
    assert "另一个账号" in account["exceptions"]

    policy = "退款和投诉必须转人工。账号密码、验证码和登录安全问题转人工。给朋友或其他账号充值属于正常购买。"
    validation = wecom_handoff_policy.validate_policy(policy, normalized["rules"], normalized["tests"])
    assert validation["ok"] is True
    assert all(item["passed"] for item in validation["tests"] if item["required"])


def test_compile_policy_uses_control_plane_ai_dispatch(monkeypatch, tmp_path: Path):
    payload = sample_ai_payload()

    def fake_dispatch(client_name, requested_model, messages, max_tokens, temperature, timeout):
        assert client_name == "admin-handoff-policy"
        assert requested_model == "text-default"
        assert max_tokens == 4000
        assert any("管理员自然语言策略" in message["content"] for message in messages)
        return {
            "success": True,
            "attempt": {
                "answer": json.dumps(payload, ensure_ascii=False),
                "model": "test-model",
                "provider_name": "test-provider",
            },
            "attempts": [],
        }

    monkeypatch.setitem(sys.modules, "app", SimpleNamespace(dispatch_chat=fake_dispatch))
    result = wecom_handoff_policy.compile_policy_with_ai(
        "退款投诉转人工；账号安全转人工；给朋友或其他账号充值不转人工。",
        tmp_path / "compile.db",
    )

    assert result["validation"]["ok"] is True
    assert result["model"] == "test-model"
    assert result["provider"] == "test-provider"
    assert len(result["rules"]) == 3


def test_publish_and_restore_previous_policy(tmp_path: Path):
    db_path = tmp_path / "publish.db"
    original = wecom_handoff_policy.load_policy_state(db_path)
    custom_rules = [
        {
            "enabled": True,
            "rule_type": "manual",
            "keyword": "特殊售后",
            "match_mode": "contains",
            "risk_terms": "",
            "exceptions": "",
            "safe_reply": "",
            "note": "自定义测试",
            "sort_order": 10,
        }
    ]

    published = wecom_handoff_policy.publish_policy_state(
        "特殊售后必须转人工。",
        custom_rules,
        path=db_path,
    )
    assert [rule["keyword"] for rule in published["rules"]] == ["特殊售后"]
    assert published["can_rollback"] is True

    restored = wecom_handoff_policy.rollback_policy_state(db_path)
    assert restored["restored"] is True
    assert any(rule["keyword"] == "账号" for rule in restored["rules"])
    assert restored["policy_text"] == original["policy_text"]


def test_publish_rejects_failed_required_safety_case(tmp_path: Path):
    try:
        wecom_handoff_policy.publish_policy_state(
            "退款问题必须转人工。",
            [
                {
                    "enabled": True,
                    "rule_type": "manual",
                    "keyword": "其他问题",
                    "match_mode": "contains",
                    "risk_terms": "",
                    "exceptions": "",
                    "safe_reply": "",
                    "note": "故意缺少退款规则",
                    "sort_order": 10,
                }
            ],
            path=tmp_path / "reject.db",
        )
    except HTTPException as exc:
        assert exc.status_code == 400
        assert "安全测试未通过" in str(exc.detail)
    else:
        raise AssertionError("unsafe policy should be rejected")


def test_admin_page_policy_router_runtime_and_docker_are_wired():
    root = Path(__file__).resolve().parents[1]
    page = (root / "static" / "wecom.html").read_text(encoding="utf-8")
    settings = (root / "wecom_settings.py").read_text(encoding="utf-8")
    policy = (root / "wecom_handoff_policy.py").read_text(encoding="utf-8")
    bootstrap = (root / "bootstrap.py").read_text(encoding="utf-8")
    dockerfile = (root / "Dockerfile").read_text(encoding="utf-8")

    assert "AI 转人工策略" in page
    assert "AI 分析并生成规则" in page
    assert "保存并发布" in page
    assert "恢复上一个版本" in page
    assert "高级设置 / 查看 AI 生成的结构化规则" in page
    assert "新增规则" not in page
    assert "r-keyword" not in page
    assert "/api/admin/wecom/handoff-policy/compile" in page
    assert "/api/admin/wecom/handoff-policy/publish" in page
    assert '@router.post("/api/admin/wecom/handoff-policy/compile")' in policy
    assert "control_plane.dispatch_chat" in policy
    assert "validate_policy" in policy
    assert "include_router(wecom_handoff_policy.router)" in bootstrap
    assert "wecom_handoff_policy.py" in dockerfile
    assert '@router.get("/api/runtime/v1/handoff/rules")' in settings
    assert "Depends(require_runtime_client)" in settings
