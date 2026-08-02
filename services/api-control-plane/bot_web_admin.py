from __future__ import annotations

import secrets
from datetime import datetime, timedelta, timezone
from typing import Any, Dict, List, Optional

from fastapi import APIRouter, Depends, HTTPException, Request
from pydantic import BaseModel, Field


router = APIRouter()
_cp: Any = None


def install(control_plane: Any) -> None:
    global _cp
    _cp = control_plane
    control_plane.app.include_router(router)


def _admin(request: Request) -> str:
    return _cp.require_admin(request)


def _parse_iso(value: Optional[str]) -> Optional[datetime]:
    if not value:
        return None
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
        if parsed.tzinfo is None:
            parsed = parsed.replace(tzinfo=timezone.utc)
        return parsed.astimezone(timezone.utc)
    except Exception:
        return None


def _online(value: Optional[str]) -> bool:
    parsed = _parse_iso(value)
    return bool(parsed and parsed >= datetime.now(timezone.utc) - timedelta(seconds=20))


def _parse_json(value: Optional[str], default: Any) -> Any:
    return _cp.parse_json(value, default)


class ClientInput(BaseModel):
    name: str = Field(min_length=1, max_length=100)


@router.get("/api/admin/mobile-bot/clients")
def list_clients(_: str = Depends(_admin)) -> List[Dict[str, Any]]:
    with _cp.db() as conn:
        rows = conn.execute(
            """
            SELECT c.id,c.name,c.token_prefix,c.token_cipher,c.enabled,c.created_at,c.last_used_at,
                   s.last_seen_at,s.app_version,s.seller_nicks_json
            FROM client_tokens c
            LEFT JOIN bot_client_state s ON s.client_id=c.id
            ORDER BY c.id DESC
            """
        ).fetchall()
    return [
        {
            "id": int(row["id"]),
            "name": row["name"],
            "token_prefix": row["token_prefix"],
            "token_available": bool(row["token_cipher"]),
            "enabled": bool(row["enabled"]),
            "created_at": row["created_at"],
            "last_used_at": row["last_used_at"],
            "last_seen_at": row["last_seen_at"],
            "online": _online(row["last_seen_at"]),
            "app_version": row["app_version"] or "",
            "seller_nicks": _parse_json(row["seller_nicks_json"], []),
        }
        for row in rows
    ]


@router.post("/api/admin/mobile-bot/clients")
def create_client(data: ClientInput, _: str = Depends(_admin)) -> Dict[str, Any]:
    token = "qnb_" + secrets.token_urlsafe(32)
    with _cp.db() as conn:
        cursor = conn.execute(
            """
            INSERT INTO client_tokens(name,token_hash,token_prefix,token_cipher,enabled,created_at)
            VALUES(?,?,?,?,1,?)
            """,
            (data.name.strip(), _cp.hash_token(token), token[:12], _cp.encrypt_secret(token), _cp.iso_now()),
        )
        client_id = int(cursor.lastrowid)
        conn.execute(
            """
            INSERT OR IGNORE INTO bot_client_state(
                client_id,status_json,current_settings_json,desired_settings_json,
                app_version,seller_nicks_json,last_seen_at,updated_at
            ) VALUES(?,?,?,?,?,?,?,?)
            """,
            (
                client_id,
                "{}",
                "{}",
                '{"auto_reply_enabled":true,"message_sync_enabled":true,"allow_web_manual_reply":true,"sync_interval_seconds":3,"message_retention_days":7}',
                "",
                "[]",
                None,
                _cp.iso_now(),
            ),
        )
    return {"id": client_id, "name": data.name.strip(), "token": token}


@router.get("/api/admin/mobile-bot/clients/{client_id}/token")
def reveal_token(client_id: int, _: str = Depends(_admin)) -> Dict[str, Any]:
    with _cp.db() as conn:
        row = conn.execute("SELECT token_cipher FROM client_tokens WHERE id=?", (client_id,)).fetchone()
    if not row:
        raise HTTPException(status_code=404, detail="客户端不存在")
    if not row["token_cipher"]:
        raise HTTPException(status_code=409, detail="旧令牌尚未加密留存；请让新版 Bot 在线同步一次，或重新生成令牌")
    return {"token": _cp.decrypt_secret(row["token_cipher"])}


@router.post("/api/admin/mobile-bot/clients/{client_id}/rotate")
def rotate_token(client_id: int, _: str = Depends(_admin)) -> Dict[str, Any]:
    token = "qnb_" + secrets.token_urlsafe(32)
    with _cp.db() as conn:
        row = conn.execute("SELECT id FROM client_tokens WHERE id=?", (client_id,)).fetchone()
        if not row:
            raise HTTPException(status_code=404, detail="客户端不存在")
        conn.execute(
            """
            UPDATE client_tokens
            SET token_hash=?,token_prefix=?,token_cipher=?,enabled=1,last_used_at=NULL
            WHERE id=?
            """,
            (_cp.hash_token(token), token[:12], _cp.encrypt_secret(token), client_id),
        )
    return {"ok": True, "token": token, "warning": "旧令牌已立即失效，请同步更新 Windows Bot。"}
