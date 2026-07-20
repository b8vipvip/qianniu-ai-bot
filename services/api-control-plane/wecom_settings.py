from __future__ import annotations

import base64
import hashlib
import os
import secrets
import sqlite3
from contextlib import contextmanager
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, Iterable, Optional, Tuple

from cryptography.fernet import Fernet, InvalidToken
from fastapi import APIRouter, Depends, HTTPException, Request
from pydantic import BaseModel, Field


DATA_DIR = Path(os.getenv("DATA_DIR", "/data")).resolve()
DATA_DIR.mkdir(parents=True, exist_ok=True)
DB_PATH = Path(os.getenv("DATABASE_PATH", str(DATA_DIR / "api-control-plane.db"))).resolve()
PUBLIC_BASE_URL = os.getenv("PUBLIC_BASE_URL", "").rstrip("/")
APP_SECRET = os.getenv("APP_SECRET", "change-me-in-production")
router = APIRouter()


def iso_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


def derive_fernet_key() -> bytes:
    explicit = os.getenv("API_KEY_ENCRYPTION_KEY", "").strip()
    if explicit:
        try:
            Fernet(explicit.encode("ascii"))
            return explicit.encode("ascii")
        except Exception as exc:
            raise RuntimeError("API_KEY_ENCRYPTION_KEY 必须是有效的 Fernet key") from exc
    digest = hashlib.sha256(APP_SECRET.encode("utf-8")).digest()
    return base64.urlsafe_b64encode(digest)


FERNET = Fernet(derive_fernet_key())


def encrypt_secret(value: str) -> str:
    value = (value or "").strip()
    return FERNET.encrypt(value.encode("utf-8")).decode("ascii") if value else ""


def decrypt_secret(value: str) -> str:
    value = (value or "").strip()
    if not value:
        return ""
    try:
        return FERNET.decrypt(value.encode("ascii")).decode("utf-8")
    except InvalidToken as exc:
        raise RuntimeError("无法解密企业微信配置，请确认 API_KEY_ENCRYPTION_KEY 未变化") from exc


@contextmanager
def db(path: Optional[Path] = None) -> Iterable[sqlite3.Connection]:
    connection = sqlite3.connect(str(path or DB_PATH), timeout=30, check_same_thread=False)
    connection.row_factory = sqlite3.Row
    connection.execute("PRAGMA journal_mode=WAL")
    try:
        yield connection
        connection.commit()
    except Exception:
        connection.rollback()
        raise
    finally:
        connection.close()


def init_wecom_settings_db(path: Optional[Path] = None) -> None:
    with db(path) as conn:
        conn.execute(
            """
            CREATE TABLE IF NOT EXISTS wecom_settings (
                id INTEGER PRIMARY KEY CHECK(id=1),
                enabled INTEGER NOT NULL DEFAULT 0,
                corp_id TEXT NOT NULL DEFAULT '',
                app_secret_cipher TEXT NOT NULL DEFAULT '',
                agent_id TEXT NOT NULL DEFAULT '',
                to_users TEXT NOT NULL DEFAULT '',
                callback_token_cipher TEXT NOT NULL DEFAULT '',
                callback_aes_key_cipher TEXT NOT NULL DEFAULT '',
                allowed_reply_users TEXT NOT NULL DEFAULT '',
                ticket_hours INTEGER NOT NULL DEFAULT 24,
                updated_at TEXT NOT NULL
            )
            """
        )


def split_users(value: str) -> Tuple[str, ...]:
    output = []
    for item in (value or "").replace(",", "|").replace("，", "|").replace(";", "|").replace("；", "|").split("|"):
        item = item.strip()
        if item and item not in output:
            output.append(item)
    return tuple(output)


def default_settings() -> Dict[str, Any]:
    return {
        "exists": False,
        "enabled": False,
        "corp_id": "",
        "app_secret": "",
        "agent_id": "",
        "to_users": "",
        "callback_token": "",
        "callback_aes_key": "",
        "allowed_reply_users": "",
        "ticket_hours": 24,
        "updated_at": None,
    }


def load_settings(path: Optional[Path] = None) -> Dict[str, Any]:
    init_wecom_settings_db(path)
    with db(path) as conn:
        row = conn.execute("SELECT * FROM wecom_settings WHERE id=1").fetchone()
    if not row:
        return default_settings()
    return {
        "exists": True,
        "enabled": bool(row["enabled"]),
        "corp_id": str(row["corp_id"] or "").strip(),
        "app_secret": decrypt_secret(row["app_secret_cipher"]),
        "agent_id": str(row["agent_id"] or "").strip(),
        "to_users": str(row["to_users"] or "").strip(),
        "callback_token": decrypt_secret(row["callback_token_cipher"]),
        "callback_aes_key": decrypt_secret(row["callback_aes_key_cipher"]),
        "allowed_reply_users": str(row["allowed_reply_users"] or "").strip(),
        "ticket_hours": max(1, min(168, int(row["ticket_hours"] or 24))),
        "updated_at": row["updated_at"],
    }


def validate_aes_key(value: str) -> bool:
    value = (value or "").strip()
    if len(value) != 43:
        return False
    try:
        return len(base64.b64decode(value + "=")) == 32
    except Exception:
        return False


def callback_url() -> str:
    return (PUBLIC_BASE_URL + "/api/wecom/callback") if PUBLIC_BASE_URL else "/api/wecom/callback"


def public_settings(settings: Dict[str, Any]) -> Dict[str, Any]:
    recipients = split_users(settings.get("to_users", ""))
    allowed = split_users(settings.get("allowed_reply_users", "")) or recipients
    outbound = bool(
        settings.get("enabled")
        and settings.get("corp_id")
        and settings.get("app_secret")
        and str(settings.get("agent_id") or "").isdigit()
        and recipients
    )
    callback = bool(
        outbound
        and settings.get("callback_token")
        and validate_aes_key(str(settings.get("callback_aes_key") or ""))
    )
    return {
        "enabled": bool(settings.get("enabled")),
        "corp_id": settings.get("corp_id", ""),
        "app_secret_configured": bool(settings.get("app_secret")),
        "agent_id": settings.get("agent_id", ""),
        "to_users": "|".join(recipients),
        "callback_token_configured": bool(settings.get("callback_token")),
        "callback_aes_key_configured": validate_aes_key(str(settings.get("callback_aes_key") or "")),
        "allowed_reply_users": "|".join(split_users(str(settings.get("allowed_reply_users") or ""))),
        "effective_allowed_reply_users": "|".join(allowed),
        "ticket_hours": int(settings.get("ticket_hours") or 24),
        "callback_url": callback_url(),
        "outbound_configured": outbound,
        "callback_configured": callback,
        "updated_at": settings.get("updated_at"),
    }


def require_admin(request: Request) -> str:
    username = request.session.get("admin_username")
    if not username:
        raise HTTPException(status_code=401, detail="请先登录控制面")
    return str(username)


class WeComSettingsInput(BaseModel):
    enabled: bool = False
    corp_id: str = Field(default="", max_length=128)
    app_secret: str = Field(default="", max_length=512)
    agent_id: str = Field(default="", max_length=32)
    to_users: str = Field(default="", max_length=2000)
    callback_token: str = Field(default="", max_length=512)
    callback_aes_key: str = Field(default="", max_length=128)
    allowed_reply_users: str = Field(default="", max_length=2000)
    ticket_hours: int = Field(default=24, ge=1, le=168)
    clear_app_secret: bool = False
    clear_callback_token: bool = False
    clear_callback_aes_key: bool = False


def save_settings(data: WeComSettingsInput, path: Optional[Path] = None) -> Dict[str, Any]:
    existing = load_settings(path)
    app_secret = "" if data.clear_app_secret else ((data.app_secret or "").strip() or existing.get("app_secret", ""))
    callback_token = "" if data.clear_callback_token else ((data.callback_token or "").strip() or existing.get("callback_token", ""))
    callback_aes_key = "" if data.clear_callback_aes_key else ((data.callback_aes_key or "").strip() or existing.get("callback_aes_key", ""))
    corp_id = (data.corp_id or "").strip()
    agent_id = (data.agent_id or "").strip()
    recipients = split_users(data.to_users)
    allowed = split_users(data.allowed_reply_users) or recipients

    if data.enabled:
        if not corp_id:
            raise HTTPException(status_code=400, detail="CorpID 不能为空")
        if not app_secret:
            raise HTTPException(status_code=400, detail="应用 Secret 不能为空")
        if not agent_id.isdigit():
            raise HTTPException(status_code=400, detail="AgentId 必须是数字")
        if not recipients:
            raise HTTPException(status_code=400, detail="至少填写一个接收成员 UserID")
    if bool(callback_token) != bool(callback_aes_key):
        raise HTTPException(status_code=400, detail="回调 Token 和 EncodingAESKey 必须同时配置")
    if callback_aes_key and not validate_aes_key(callback_aes_key):
        raise HTTPException(status_code=400, detail="EncodingAESKey 必须是可解码为32字节的43位值")

    now = iso_now()
    init_wecom_settings_db(path)
    with db(path) as conn:
        conn.execute(
            """
            INSERT INTO wecom_settings(
                id,enabled,corp_id,app_secret_cipher,agent_id,to_users,
                callback_token_cipher,callback_aes_key_cipher,
                allowed_reply_users,ticket_hours,updated_at
            ) VALUES(1,?,?,?,?,?,?,?,?,?,?)
            ON CONFLICT(id) DO UPDATE SET
                enabled=excluded.enabled,
                corp_id=excluded.corp_id,
                app_secret_cipher=excluded.app_secret_cipher,
                agent_id=excluded.agent_id,
                to_users=excluded.to_users,
                callback_token_cipher=excluded.callback_token_cipher,
                callback_aes_key_cipher=excluded.callback_aes_key_cipher,
                allowed_reply_users=excluded.allowed_reply_users,
                ticket_hours=excluded.ticket_hours,
                updated_at=excluded.updated_at
            """,
            (
                1 if data.enabled else 0,
                corp_id,
                encrypt_secret(app_secret),
                agent_id,
                "|".join(recipients),
                encrypt_secret(callback_token),
                encrypt_secret(callback_aes_key),
                "|".join(allowed),
                max(1, min(168, int(data.ticket_hours))),
                now,
            ),
        )
    return load_settings(path)


@router.get("/api/admin/wecom/settings")
def admin_get_wecom_settings(_: str = Depends(require_admin)) -> Dict[str, Any]:
    return public_settings(load_settings())


@router.put("/api/admin/wecom/settings")
def admin_save_wecom_settings(data: WeComSettingsInput, _: str = Depends(require_admin)) -> Dict[str, Any]:
    saved = save_settings(data)
    try:
        import wecom_bridge
        wecom_bridge.clear_access_token_cache()
    except Exception:
        pass
    return public_settings(saved)


@router.post("/api/admin/wecom/generate-callback")
def admin_generate_wecom_callback(_: str = Depends(require_admin)) -> Dict[str, str]:
    return {
        "callback_token": secrets.token_urlsafe(24),
        "callback_aes_key": base64.b64encode(secrets.token_bytes(32)).decode("ascii").rstrip("="),
    }


@router.post("/api/admin/wecom/test")
def admin_test_wecom(_: str = Depends(require_admin)) -> Dict[str, Any]:
    import wecom_bridge

    settings = load_settings()
    if not public_settings(settings)["outbound_configured"]:
        raise HTTPException(status_code=400, detail="请先保存完整的企业微信应用消息配置")
    content = (
        "【千牛 AI 控制面测试】\n"
        "企业微信应用消息配置已生效。\n"
        "回调地址：" + callback_url() + "\n"
        "人工回复格式：QN-XXXXXXXX 回复内容"
    )
    try:
        result = wecom_bridge.send_app_text(wecom_bridge.configured_recipients(), content)
    except Exception as exc:
        raise HTTPException(status_code=502, detail=str(exc)[:500]) from exc
    return {"ok": True, "msgid": result.get("msgid"), "message": "测试消息发送成功"}
