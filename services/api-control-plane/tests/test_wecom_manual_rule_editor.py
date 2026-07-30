from pathlib import Path


def test_wecom_advanced_rule_view_is_editable_and_guarded():
    root = Path(__file__).resolve().parents[1]
    page = (root / "static" / "wecom.html").read_text(encoding="utf-8")

    assert '<textarea id="advancedRules"' in page
    assert 'id="applyRules"' in page
    assert 'id="formatRules"' in page
    assert 'id="resetRules"' in page
    assert "parseAdvancedRules" in page
    assert "summarizeRulesLocally" in page
    assert "manual_pending_server_validation" in page
    assert "editorSignature" in page
    assert "validatedRulesSignature" in page


def test_manual_rule_changes_must_be_applied_before_publish():
    root = Path(__file__).resolve().parents[1]
    page = (root / "static" / "wecom.html").read_text(encoding="utf-8")

    change_guard = page.index("$('advancedRules').addEventListener('input'")
    apply_handler = page.index("$('applyRules').onclick")
    publish_handler = page.index("$('publishPolicy').onclick")

    assert change_guard < apply_handler < publish_handler
    assert "结构化规则已变化，请先点击“应用手动修改”" in page
    assert "policyDraft.rules" in page
    assert "/api/admin/wecom/handoff-policy/publish" in page
    assert "服务端会重新执行安全校验" in page


def test_existing_publish_endpoint_remains_final_authority():
    root = Path(__file__).resolve().parents[1]
    policy = (root / "wecom_handoff_policy.py").read_text(encoding="utf-8")

    publish = policy.index("def publish_policy_state(")
    normalize = policy.index("normalized = normalize_compiled_payload", publish)
    validate = policy.index("validation = validate_policy", normalize)
    reject = policy.index("策略安全测试未通过，已拒绝发布", validate)
    save = policy.index("wecom_settings.save_handoff_rules", reject)

    assert publish < normalize < validate < reject < save
