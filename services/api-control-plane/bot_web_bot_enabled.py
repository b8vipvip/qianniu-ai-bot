from __future__ import annotations

from typing import Any, Dict

from fastapi import APIRouter, Depends, HTTPException, Request
from pydantic import BaseModel

import bot_web_console


router = APIRouter()
_cp: Any = None


def install(control_plane: Any) -> None:
    global _cp
    _cp = control_plane
    control_plane.app.include_router(router)


def init_db() -> None:
    with _cp.db() as conn:
        conn.executescript(
            """
            CREATE TABLE IF NOT EXISTS bot_client_bot_enabled (
                client_id INTEGER PRIMARY KEY,
                desired_enabled INTEGER,
                current_enabled INTEGER,
                shop_key TEXT NOT NULL DEFAULT '',
                last_seen_at TEXT,
                updated_at TEXT NOT NULL,
                FOREIGN KEY(client_id) REFERENCES client_tokens(id) ON DELETE CASCADE
            );
            """
        )


def _read_state(client_id: int) -> Dict[str, Any]:
    with _cp.db() as conn:
        row = conn.execute(
            """
            SELECT desired_enabled,current_enabled,shop_key,last_seen_at
            FROM bot_client_bot_enabled WHERE client_id=?
            """,
            (client_id,),
        ).fetchone()
    if not row:
        return {
            "desired_enabled": True,
            "current_enabled": None,
            "shop_key": "",
            "last_seen_at": None,
        }
    desired = row["desired_enabled"]
    current = row["current_enabled"]
    if desired is None:
        desired = current if current is not None else 1
    return {
        "desired_enabled": bool(desired),
        "current_enabled": None if current is None else bool(current),
        "shop_key": row["shop_key"] or "",
        "last_seen_at": row["last_seen_at"],
    }


class WebBotEnabledInput(BaseModel):
    enabled: bool


class RuntimeBotEnabledInput(BaseModel):
    current_enabled: bool


@router.get("/api/bot-web/bot-enabled")
def bot_web_get_bot_enabled(
    client: Dict[str, Any] = Depends(bot_web_console._web_client),
) -> Dict[str, Any]:
    state = _read_state(int(client["id"]))
    current = state["current_enabled"]
    desired = state["desired_enabled"]
    return {
        "ok": True,
        "shop_key": state["shop_key"],
        "desired_enabled": desired,
        "current_enabled": current,
        "pending": current is None or current != desired,
        "online": bot_web_console._is_online(state["last_seen_at"]),
        "last_seen_at": state["last_seen_at"],
    }


@router.put("/api/bot-web/bot-enabled")
def bot_web_set_bot_enabled(
    data: WebBotEnabledInput,
    client: Dict[str, Any] = Depends(bot_web_console._web_client),
) -> Dict[str, Any]:
    client_id = int(client["id"])
    now = bot_web_console._now()
    with _cp.db() as conn:
        conn.execute(
            """
            INSERT INTO bot_client_bot_enabled(
                client_id,desired_enabled,current_enabled,shop_key,last_seen_at,updated_at
            ) VALUES(?,?,NULL,'',NULL,?)
            ON CONFLICT(client_id) DO UPDATE SET
                desired_enabled=excluded.desired_enabled,
                updated_at=excluded.updated_at
            """,
            (client_id, 1 if data.enabled else 0, now),
        )
    return {"ok": True, "desired_enabled": bool(data.enabled)}


@router.post("/api/runtime/v1/bot-web/bot-enabled-sync")
def runtime_bot_enabled_sync(
    data: RuntimeBotEnabledInput,
    request: Request,
    client: Dict[str, Any] = Depends(bot_web_console._runtime_client),
) -> Dict[str, Any]:
    shop_key = (request.headers.get("x-shop-key") or "").strip()
    if not shop_key:
        raise HTTPException(status_code=400, detail="缺少 X-Shop-Key")

    client_id = int(client["id"])
    current_enabled = bool(data.current_enabled)
    now = bot_web_console._now()
    with _cp.db() as conn:
        existing = conn.execute(
            "SELECT shop_key FROM bot_client_bot_enabled WHERE client_id=?",
            (client_id,),
        ).fetchone()
        bound_shop_key = (existing["shop_key"] if existing else "") or ""
        if bound_shop_key and bound_shop_key != shop_key:
            raise HTTPException(status_code=409, detail="该客户端令牌已绑定其他 ShopKey")

        # Upgrade compatibility: the first new-client sync adopts the current
        # Windows value. A Web value saved before first sync is preserved.
        conn.execute(
            """
            INSERT INTO bot_client_bot_enabled(
                client_id,desired_enabled,current_enabled,shop_key,last_seen_at,updated_at
            ) VALUES(?,?,?,?,?,?)
            ON CONFLICT(client_id) DO UPDATE SET
                desired_enabled=COALESCE(
                    bot_client_bot_enabled.desired_enabled,
                    excluded.current_enabled
                ),
                current_enabled=excluded.current_enabled,
                shop_key=excluded.shop_key,
                last_seen_at=excluded.last_seen_at,
                updated_at=excluded.updated_at
            """,
            (
                client_id,
                1 if current_enabled else 0,
                1 if current_enabled else 0,
                shop_key,
                now,
                now,
            ),
        )
        row = conn.execute(
            "SELECT desired_enabled FROM bot_client_bot_enabled WHERE client_id=?",
            (client_id,),
        ).fetchone()

    desired_enabled = current_enabled if not row or row["desired_enabled"] is None else bool(row["desired_enabled"])
    return {
        "ok": True,
        "shop_key": shop_key,
        "desired_enabled": desired_enabled,
        "server_time": now,
    }
