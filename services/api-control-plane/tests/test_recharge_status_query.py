from __future__ import annotations

import inspect

import pytest
from fastapi import HTTPException

import recharge_status_query


def classify(status: str, elapsed: int = 0):
    return recharge_status_query._classify(
        {"r_status": status, "elapsed_minutes": elapsed}
    )


class FakeResponse:
    def __init__(
        self,
        *,
        url: str,
        status_code: int = 200,
        payload=None,
        text: str = "",
        content_type: str = "text/html; charset=UTF-8",
    ):
        self.url = url
        self.status_code = status_code
        self._payload = payload
        self.text = text
        self.headers = {"content-type": content_type}

    def json(self):
        if self._payload is None:
            raise ValueError("not json")
        return self._payload


class FakeSession:
    def __init__(self, responses):
        self.responses = list(responses)
        self.calls = []
        self.closed = False

    def get(self, url, **kwargs):
        self.calls.append((url, kwargs))
        response = self.responses.pop(0)
        if isinstance(response, Exception):
            raise response
        return response

    def close(self):
        self.closed = True


def test_charging_status_under_eight_minutes_waits_without_handoff():
    result = classify("准备登录", 6)
    assert result["category"] == "charging"
    assert result["reply_text"] == "还在充值中，请多等几分钟。"
    assert result["notify_human"] is False


def test_sent_status_is_charging_and_obeys_eight_minute_threshold():
    waiting = classify("已发送", 6)
    assert waiting["category"] == "charging"
    assert waiting["notify_human"] is False

    overdue = classify("已发送", 9)
    assert overdue["category"] == "charging"
    assert overdue["notify_human"] is True


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


def test_legacy_query_url_is_migrated_to_real_api_path():
    assert (
        recharge_status_query._normalize_query_url(
            "https://ka.k2n.cn/get_recharge_status"
        )
        == "https://ka.k2n.cn/api.php"
    )
    assert (
        recharge_status_query._normalize_query_url(
            "https://ka.k2n.cn/api.php?action=search&page=1"
        )
        == "https://ka.k2n.cn/api.php"
    )


def test_search_api_selects_newest_exact_record_and_reuses_session(monkeypatch):
    code = "fh5dbpbrcj199"
    session = FakeSession(
        [
            FakeResponse(
                url="https://ka.k2n.cn/",
                text="<html><title>后台管理</title></html>",
            ),
            FakeResponse(
                url="https://ka.k2n.cn/api.php?action=search&page=1",
                payload={
                    "records": [
                        {
                            "id": 1396,
                            "tel": code,
                            "create_date": "08011111",
                            "r_status": "已发送",
                        },
                        {
                            "id": 1397,
                            "tel": code,
                            "create_date": "08011118",
                            "r_status": "失败",
                        },
                        {
                            "id": 2000,
                            "tel": code + "x",
                            "r_status": "充值成功",
                        },
                    ],
                    "current_page": 1,
                    "total_pages": 0,
                },
                content_type="application/json; charset=utf-8",
            ),
        ]
    )
    monkeypatch.setattr(
        recharge_status_query.curl_requests,
        "Session",
        lambda **kwargs: session,
    )

    payload = recharge_status_query._query_upstream(
        code,
        {
            "query_url": "https://ka.k2n.cn/get_recharge_status",
            "auth_key": "server-side-secret",
            "timeout_seconds": 15,
        },
    )

    assert payload["id"] == 1397
    assert payload["r_status"] == "失败"
    assert session.closed is True
    assert len(session.calls) == 2

    login_url, login_kwargs = session.calls[0]
    assert login_url == "https://ka.k2n.cn/auth_key"
    assert login_kwargs["params"] == {"key": "server-side-secret"}
    assert "Mozilla/5.0" in login_kwargs["headers"]["User-Agent"]

    query_url, query_kwargs = session.calls[1]
    assert query_url == "https://ka.k2n.cn/api.php"
    assert query_kwargs["params"] == {
        "action": "search",
        "tel": code,
        "page": 1,
    }
    assert query_kwargs["headers"]["Referer"] == "https://ka.k2n.cn/"
    assert "Mozilla/5.0" in query_kwargs["headers"]["User-Agent"]
    assert "Authorization" not in query_kwargs["headers"]
    assert "X-Admin-Key" not in query_kwargs["headers"]
    assert "X-API-Key" not in query_kwargs["headers"]


def test_search_api_uses_newest_filtered_record_when_tel_is_not_echoed():
    payload = {
        "records": [
            {"id": 1396, "tel": "***", "r_status": "已发送"},
            {"id": 1397, "tel": "***", "r_status": "失败"},
        ]
    }

    record = recharge_status_query._select_record(payload, "fh5dbpbrcj199")
    assert record["id"] == 1397
    assert record["r_status"] == "失败"


def test_search_api_normalizes_formatted_echoed_code():
    payload = {
        "records": [
            {"id": 8, "tel": "FH5D-BPBR-CJ199", "r_status": "充值成功"}
        ]
    }

    record = recharge_status_query._select_record(payload, "fh5dbpbrcj199")
    assert record["id"] == 8


def test_search_api_returns_not_found_only_when_records_are_empty():
    payload = recharge_status_query._select_record({"records": []}, "fh5dbpbrcj199")
    assert payload == {}
    assert recharge_status_query._classify(payload)["category"] == "not_found"


def test_invalid_records_shape_is_reported(monkeypatch):
    session = FakeSession(
        [
            FakeResponse(
                url="https://ka.k2n.cn/",
                text="<html><title>后台管理</title></html>",
            ),
            FakeResponse(
                url="https://ka.k2n.cn/api.php?action=search&page=1",
                payload={"records": {}},
                content_type="application/json",
            ),
        ]
    )
    monkeypatch.setattr(
        recharge_status_query.curl_requests,
        "Session",
        lambda **kwargs: session,
    )

    with pytest.raises(HTTPException) as exc_info:
        recharge_status_query._query_upstream(
            "fh5dbpbrcj199",
            {
                "query_url": "https://ka.k2n.cn/api.php",
                "auth_key": "server-side-secret",
                "timeout_seconds": 15,
            },
        )

    assert exc_info.value.status_code == 502
    assert "records 格式无效" in str(exc_info.value.detail)


def test_invalid_key_login_is_reported_before_query(monkeypatch):
    session = FakeSession(
        [
            FakeResponse(
                url="https://ka.k2n.cn/login.php",
                text="<html><title>后台 Key 登录</title>请输入后台访问 Key</html>",
            )
        ]
    )
    monkeypatch.setattr(
        recharge_status_query.curl_requests,
        "Session",
        lambda **kwargs: session,
    )

    with pytest.raises(HTTPException) as exc_info:
        recharge_status_query._query_upstream(
            "fh5dbpbrcj199",
            {
                "query_url": "https://ka.k2n.cn/api.php",
                "auth_key": "wrong-key",
                "timeout_seconds": 15,
            },
        )

    assert exc_info.value.status_code == 502
    assert "Key 无效或登录失败" in str(exc_info.value.detail)
    assert len(session.calls) == 1
    assert session.closed is True


def test_expired_session_redirect_is_reported_as_auth_failure(monkeypatch):
    session = FakeSession(
        [
            FakeResponse(
                url="https://ka.k2n.cn/",
                text="<html><title>后台管理</title></html>",
            ),
            FakeResponse(
                url="https://ka.k2n.cn/login.php",
                text="<html><title>后台 Key 登录</title></html>",
            ),
        ]
    )
    monkeypatch.setattr(
        recharge_status_query.curl_requests,
        "Session",
        lambda **kwargs: session,
    )

    with pytest.raises(HTTPException) as exc_info:
        recharge_status_query._query_upstream(
            "fh5dbpbrcj199",
            {
                "query_url": "https://ka.k2n.cn/api.php",
                "auth_key": "server-side-secret",
                "timeout_seconds": 15,
            },
        )

    assert exc_info.value.status_code == 502
    assert "登录状态失效" in str(exc_info.value.detail)


def test_connection_errors_do_not_echo_key_or_recharge_code(monkeypatch):
    session = FakeSession(
        [RuntimeError("https://ka.k2n.cn/auth_key?key=do-not-leak")]
    )
    monkeypatch.setattr(
        recharge_status_query.curl_requests,
        "Session",
        lambda **kwargs: session,
    )

    with pytest.raises(HTTPException) as exc_info:
        recharge_status_query._query_upstream(
            "private-code-123",
            {
                "query_url": "https://ka.k2n.cn/api.php",
                "auth_key": "do-not-leak",
                "timeout_seconds": 15,
            },
        )

    detail = str(exc_info.value.detail)
    assert "do-not-leak" not in detail
    assert "private-code-123" not in detail
    assert "RuntimeError" in detail


def test_upstream_contract_is_key_session_plus_search_api():
    auth_source = inspect.getsource(recharge_status_query._authenticate_upstream)
    query_source = inspect.getsource(recharge_status_query._query_upstream)
    assert '"/auth_key"' in auth_source
    assert 'params={"key": auth_key}' in auth_source
    assert '"action": "search"' in query_source
    assert '"tel": code' in query_source
    assert '"page": 1' in query_source
    assert '"Authorization": "Bearer " + auth_key' not in query_source
    assert '"X-Admin-Key": auth_key' not in query_source
    assert '"X-API-Key": auth_key' not in query_source
