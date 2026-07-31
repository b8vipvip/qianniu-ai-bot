from __future__ import annotations

import inspect

import recharge_status_query


def classify(status: str, elapsed: int = 0):
    return recharge_status_query._classify(
        {"r_status": status, "elapsed_minutes": elapsed}
    )


def test_charging_status_under_eight_minutes_waits_without_handoff():
    result = classify("准备登录", 6)
    assert result["category"] == "charging"
    assert result["reply_text"] == "还在充值中，请多等几分钟。"
    assert result["notify_human"] is False


def test_charging_status_over_eight_minutes_hands_off():
    result = classify("登录成功", 9)
    assert result["category"] == "charging"
    assert result["reply_text"] == "稍等，正在转人工客服处理。"
    assert result["notify_human"] is True


def test_success_failure_manual_and_captcha_mappings():
    success = classify("充值成功")
    assert success["category"] == "success"
    assert "重新登录" in success["reply_text"]
    assert success["notify_human"] is False

    failure = classify("已拦截")
    assert failure["category"] == "failed"
    assert "转接人工客服" in failure["reply_text"]
    assert failure["notify_human"] is True

    manual = classify("滑块验证")
    assert manual["category"] == "manual_processing"
    assert "多等2分钟" in manual["reply_text"]

    captcha = classify("验证失效")
    assert captcha["category"] == "captcha_invalid"
    assert "重新提交" in captcha["reply_text"]


def test_not_found_is_explicit_and_does_not_fake_success():
    result = classify("")
    assert result["category"] == "not_found"
    assert "未查询到充值记录" in result["reply_text"]
    assert result["notify_human"] is False


def test_runtime_code_validation_and_public_settings_hide_secret():
    assert recharge_status_query.CODE_PATTERN.fullmatch("fh5dbpbrcj199")
    assert not recharge_status_query.CODE_PATTERN.fullmatch("https://cn12.vip")
    source = inspect.getsource(recharge_status_query._load_settings)
    assert 'result["auth_key"]' in source
    assert "if include_secret" in source
    public_endpoint = inspect.getsource(
        recharge_status_query.admin_get_recharge_query_settings
    )
    assert "include_secret=False" in public_endpoint


def test_upstream_key_is_header_only_in_source():
    source = inspect.getsource(recharge_status_query._query_upstream)
    assert '"Authorization": "Bearer " + auth_key' in source
    assert '"X-Admin-Key": auth_key' in source
    assert '"X-API-Key": auth_key' in source
    assert 'params={"q": code}' in source
    assert 'params={"q": code, "key": auth_key}' not in source
