from __future__ import annotations

import re
from datetime import datetime
from typing import Any, Dict
from urllib.parse import urlparse
from zoneinfo import ZoneInfo

from curl_cffi import requests as curl_requests
from fastapi import APIRouter, Depends, HTTPException
from pydantic import BaseModel, Field

from wecom_settings import (
    db,
    decrypt_secret,
    encrypt_secret,
    iso_now,
    require_admin,
    require_runtime_client,
)


router = APIRouter()
DEFAULT_QUERY_URL = "https://ka.k2n.cn/get_recharge_status"
CODE_PATTERN = re.compile(r"^[A-Za-z0-9_-]{6,64}$")
CHARGING = {"等待", "准备登录", "webdl", "appdl", "登录成功"}
SUCCESS = {"充值成功", "成功", "已成功", "手动成功"}
FAILED = {"失败", "已拦截", "无效订单", "重复订单"}
MANUAL = {"需手动", "卡了", "超时", "异常", "正在充值", "充值中", "无元素", "滑块验证"}
CAPTCHA_INVALID = {"验证失效", "验证码失效", "验证错"}


class RechargeQuerySettingsInput(BaseModel):
    enabled: bool = False
    query_url: str = Field(default=DEFAULT_QUERY_URL, max_length=1000)
    auth_key: str = Field(default="", max_length=1000)
    timeout_seconds: int = Field(default=15, ge=3, le=60)
    clear_auth_key: bool = False


class RechargeStatusRuntimeInput(BaseModel):
    code: str = Field(min_length=6, max_length=64)
    seller: str = Field(default="", max_length=180)
    buyer: str = Field(default="", max_length=180)


def init_recharge_query_db() -> None:
    with db() as conn:
        conn.executescript(
            """
            CREATE TABLE IF NOT EXISTS recharge_query_settings (
                id INTEGER PRIMARY KEY CHECK(id=1),
                enabled INTEGER NOT NULL DEFAULT 0,
                query_url TEXT NOT NULL DEFAULT '',
                auth_key_cipher TEXT NOT NULL DEFAULT '',
                timeout_seconds INTEGER NOT NULL DEFAULT 15,
                updated_at TEXT NOT NULL
            );
            """
        )


def _valid_url(value: str) -> bool:
    try:
        parsed = urlparse((value or "").strip())
        return parsed.scheme in {"http", "https"} and bool(parsed.netloc)
    except Exception:
        return False


def _load_settings(include_secret: bool = False) -> Dict[str, Any]:
    init_recharge_query_db()
    with db() as conn:
        row = conn.execute("SELECT * FROM recharge_query_settings WHERE id=1").fetchone()
    if not row:
        result: Dict[str, Any] = {
            "exists": False,
            "enabled": False,
            "query_url": DEFAULT_QUERY_URL,
            "auth_key_configured": False,
            "timeout_seconds": 15,
            "updated_at": None,
        }
        if include_secret:
            result["auth_key"] = ""
        return result

    cipher = str(row["auth_key_cipher"] or "")
    result = {
        "exists": True,
        "enabled": bool(row["enabled"]),
        "query_url": str(row["query_url"] or DEFAULT_QUERY_URL).strip(),
        "auth_key_configured": bool(cipher),
        "timeout_seconds": max(3, min(60, int(row["timeout_seconds"] or 15))),
        "updated_at": row["updated_at"],
    }
    if include_secret:
        result["auth_key"] = decrypt_secret(cipher) if cipher else ""
    return result


def _save_settings(data: RechargeQuerySettingsInput) -> Dict[str, Any]:
    current = _load_settings(include_secret=True)
    query_url = (data.query_url or DEFAULT_QUERY_URL).strip()
    if not _valid_url(query_url):
        raise HTTPException(status_code=400, detail="充值查询接口地址无效")

    auth_key = (data.auth_key or "").strip()
    if data.clear_auth_key:
        auth_key = ""
    elif not auth_key:
        auth_key = str(current.get("auth_key") or "")

    if data.enabled and not auth_key:
        raise HTTPException(status_code=400, detail="启用自动查询前必须配置后台访问 Key")

    now = iso_now()
    with db() as conn:
        conn.execute(
            """
            INSERT INTO recharge_query_settings(
                id,enabled,query_url,auth_key_cipher,timeout_seconds,updated_at
            ) VALUES(1,?,?,?,?,?)
            ON CONFLICT(id) DO UPDATE SET
                enabled=excluded.enabled,
                query_url=excluded.query_url,
                auth_key_cipher=excluded.auth_key_cipher,
                timeout_seconds=excluded.timeout_seconds,
                updated_at=excluded.updated_at
            """,
            (
                1 if data.enabled else 0,
                query_url,
                encrypt_secret(auth_key),
                max(3, min(60, data.timeout_seconds)),
                now,
            ),
        )
    return _load_settings(include_secret=False)


def _elapsed_minutes(payload: Dict[str, Any]) -> int:
    raw = payload.get("elapsed_minutes")
    try:
        if raw is not None and str(raw).strip() != "":
            return max(0, int(float(str(raw))))
    except Exception:
        pass

    text = str(payload.get("recharge_time") or "").strip()
    if not text:
        return 0
    try:
        submitted = datetime.strptime(text, "%Y-%m-%d %H:%M:%S").replace(
            tzinfo=ZoneInfo("Asia/Shanghai")
        )
        now = datetime.now(ZoneInfo("Asia/Shanghai"))
        return max(0, int((now - submitted).total_seconds() // 60))
    except Exception:
        return 0


def _classify(payload: Dict[str, Any]) -> Dict[str, Any]:
    r_status = str(payload.get("r_status") or "").strip()
    elapsed = _elapsed_minutes(payload)
    category = "unknown"
    reply = "暂未查询到明确的充值进度，请稍后再试或联系人工客服。"
    notify_human = False

    if r_status in CHARGING:
        category = "charging"
        if elapsed > 8:
            reply = "稍等，正在转人工客服处理。"
            notify_human = True
        else:
            reply = "还在充值中，请多等几分钟。"
    elif r_status in SUCCESS:
        category = "success"
        reply = "已经充值成功，您账号重新登录一次即可，有问题请联系客服。"
    elif r_status in FAILED:
        category = "failed"
        reply = "充值失败了，正在转接人工客服处理。"
        notify_human = True
    elif r_status in MANUAL:
        category = "manual_processing"
        reply = "客服正在充值中，请多等2分钟！"
    elif r_status in CAPTCHA_INVALID:
        category = "captcha_invalid"
        reply = "您刚刚兑换时提交的验证码失效了，重新提交一下。"
    elif not r_status:
        category = "not_found"
        reply = "暂未查询到充值记录，请确认兑换码是否正确，或稍后再试。"

    return {
        "handled": True,
        "category": category,
        "r_status": r_status,
        "elapsed_minutes": elapsed,
        "reply_text": reply,
        "notify_human": notify_human,
    }


def _upstream_origin(query_url: str) -> str:
    parsed = urlparse(query_url)
    return f"{parsed.scheme}://{parsed.netloc}"


def _response_path(response: Any) -> str:
    try:
        return (urlparse(str(response.url or "")).path or "").lower()
    except Exception:
        return ""


def _response_text_prefix(response: Any, limit: int = 4096) -> str:
    try:
        return str(response.text or "")[:limit].lower()
    except Exception:
        return ""


def _looks_like_key_login(response: Any) -> bool:
    if _response_path(response).endswith("/login.php"):
        return True
    content_type = str(getattr(response, "headers", {}).get("content-type", "")).lower()
    if "text/html" not in content_type:
        return False
    text = _response_text_prefix(response)
    return "后台 key 登录".lower() in text or "请输入后台访问 key".lower() in text


def _authenticate_upstream(
    session: Any,
    query_url: str,
    auth_key: str,
    timeout: int,
) -> None:
    auth_url = _upstream_origin(query_url) + "/auth_key"
    headers = {
        "Accept": "text/html,application/xhtml+xml,application/json;q=0.9,*/*;q=0.8",
        "User-Agent": "aboter-recharge-status/1.1",
    }
    try:
        response = session.get(
            auth_url,
            params={"key": auth_key},
            headers=headers,
            timeout=timeout,
            allow_redirects=True,
        )
    except Exception as exc:
        raise HTTPException(
            status_code=502,
            detail="充值查询上游鉴权连接失败（" + type(exc).__name__ + "）",
        )

    if response.status_code < 200 or response.status_code >= 300:
        raise HTTPException(
            status_code=502,
            detail="充值查询上游鉴权返回 HTTP " + str(response.status_code),
        )
    if _looks_like_key_login(response):
        raise HTTPException(status_code=502, detail="充值查询后台访问 Key 无效或登录失败")


def _query_upstream(code: str, settings: Dict[str, Any]) -> Dict[str, Any]:
    query_url = str(settings.get("query_url") or DEFAULT_QUERY_URL).strip()
    auth_key = str(settings.get("auth_key") or "").strip()
    timeout = max(3, min(60, int(settings.get("timeout_seconds") or 15)))
    if not auth_key:
        raise HTTPException(status_code=503, detail="服务端未配置充值查询后台访问 Key")

    session = curl_requests.Session(
        impersonate="chrome",
        timeout=timeout,
        allow_redirects=True,
    )
    try:
        _authenticate_upstream(session, query_url, auth_key, timeout)
        try:
            response = session.get(
                query_url,
                params={"q": code},
                headers={
                    "Accept": "application/json",
                    "User-Agent": "aboter-recharge-status/1.1",
                    "Referer": _upstream_origin(query_url) + "/admin/index.html",
                },
                timeout=timeout,
                allow_redirects=True,
            )
        except Exception as exc:
            raise HTTPException(
                status_code=502,
                detail="充值查询接口连接失败（" + type(exc).__name__ + "）",
            )
    finally:
        try:
            session.close()
        except Exception:
            pass

    if response.status_code < 200 or response.status_code >= 300:
        raise HTTPException(
            status_code=502,
            detail="充值查询接口返回 HTTP " + str(response.status_code),
        )
    if _looks_like_key_login(response):
        raise HTTPException(status_code=502, detail="充值查询上游登录状态失效")
    try:
        payload = response.json()
    except Exception:
        content_type = str(response.headers.get("content-type", "")).split(";", 1)[0]
        suffix = "（" + content_type[:80] + "）" if content_type else ""
        raise HTTPException(status_code=502, detail="充值查询接口未返回 JSON" + suffix)
    if not isinstance(payload, dict):
        raise HTTPException(status_code=502, detail="充值查询接口返回格式无效")
    if str(payload.get("status") or "").lower() == "error":
        raise HTTPException(status_code=502, detail="充值查询接口返回错误")
    return payload


@router.get("/api/admin/recharge-query/settings")
def admin_get_recharge_query_settings(_: str = Depends(require_admin)) -> Dict[str, Any]:
    return _load_settings(include_secret=False)


@router.put("/api/admin/recharge-query/settings")
def admin_put_recharge_query_settings(
    data: RechargeQuerySettingsInput,
    _: str = Depends(require_admin),
) -> Dict[str, Any]:
    return _save_settings(data)


@router.post("/api/admin/recharge-query/test")
def admin_test_recharge_query(
    data: RechargeStatusRuntimeInput,
    _: str = Depends(require_admin),
) -> Dict[str, Any]:
    code = (data.code or "").strip()
    if not CODE_PATTERN.fullmatch(code):
        raise HTTPException(status_code=400, detail="测试兑换码格式无效")
    settings = _load_settings(include_secret=True)
    if not settings.get("auth_key"):
        raise HTTPException(status_code=400, detail="请先保存后台访问 Key")
    return _classify(_query_upstream(code, settings))


@router.get("/api/runtime/v1/recharge-query/config")
def runtime_recharge_query_config(
    _: Dict[str, Any] = Depends(require_runtime_client),
) -> Dict[str, Any]:
    settings = _load_settings(include_secret=False)
    return {
        "enabled": bool(settings.get("enabled")),
        "updated_at": settings.get("updated_at"),
    }


@router.post("/api/runtime/v1/recharge-query/status")
def runtime_recharge_query_status(
    data: RechargeStatusRuntimeInput,
    _: Dict[str, Any] = Depends(require_runtime_client),
) -> Dict[str, Any]:
    code = (data.code or "").strip()
    if not CODE_PATTERN.fullmatch(code):
        raise HTTPException(status_code=400, detail="兑换码格式无效")

    settings = _load_settings(include_secret=True)
    if not settings.get("enabled"):
        return {"handled": False, "reason": "disabled"}
    if not settings.get("auth_key"):
        raise HTTPException(status_code=503, detail="服务端未配置充值查询后台访问 Key")

    result = _classify(_query_upstream(code, settings))
    # 只向 Windows Bot 返回处理所需的状态，不返回手机号、账号昵称、验证码等个人信息。
    return result
