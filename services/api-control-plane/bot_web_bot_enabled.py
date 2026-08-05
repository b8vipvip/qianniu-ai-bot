from __future__ import annotations

import json
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


def _parse(value: Any) -> Dict[str, Any]:
    if not value:
        return {}
    try:
        parsed = json.loads(value)
        return parsed if isinstance(parsed, dict) else {}
    except Exception:
        return {}


def _json(value: Dict[str, Any]) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def _read_state(client_id: int) -> Dict[str, Any]:
    bot_web_console._ensure_state(client_id)
    with _cp.db() as conn:
        row = conn.execute(
            """
            SELECT desired_settings_json,current_settings_json,last_seen_at
            FROM bot_client_state WHERE client_id=?
            """,
            (client_id,),
        ).fetchone()
    desired = _parse(row["desired_settings_json"] if row else None)
    current = _parse(row["current_settings_json"] if row else None)
    desired_value = desired.get("bot_enabled")
    current_value = current.get("bot_enabled")
    if desired_value is None:
        desired_value = current_value if current_value is not None else True
    return {
        "desired_enabled": bool(desired_value),
        "current_enabled": None if current_value is None else bool(current_value),
        "last_seen_at": row["last_seen_at"] if row else None,
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
    bot_web_console._ensure_state(client_id)
    with _cp.db() as conn:
        row = conn.execute(
            "SELECT desired_settings_json FROM bot_client_state WHERE client_id=?",
            (client_id,),
        ).fetchone()
        desired = _parse(row["desired_settings_json"] if row else None)
        desired["bot_enabled"] = bool(data.enabled)
        conn.execute(
            "UPDATE bot_client_state SET desired_settings_json=?,updated_at=? WHERE client_id=?",
            (_json(desired), bot_web_console._now(), client_id),
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
    bot_web_console._ensure_state(client_id)
    current_enabled = bool(data.current_enabled)
    with _cp.db() as conn:
        row = conn.execute(
            """
            SELECT desired_settings_json,current_settings_json,status_json
            FROM bot_client_state WHERE client_id=?
            """,
            (client_id,),
        ).fetchone()
        desired = _parse(row["desired_settings_json"] if row else None)
        current = _parse(row["current_settings_json"] if row else None)
        status_data = _parse(row["status_json"] if row else None)

        # Upgrade compatibility: the first new-client sync adopts the current
        # Windows value unless the user has already explicitly changed Web.
        if "bot_enabled" not in desired:
            desired["bot_enabled"] = current_enabled
        current["bot_enabled"] = current_enabled
        status_data["shop_key"] = shop_key
        status_data["windows_bot_enabled"] = current_enabled
        status_data["effective_bot_enabled"] = current_enabled

        now = bot_web_console._now()
        conn.execute(
            """
            UPDATE bot_client_state SET
                desired_settings_json=?,current_settings_json=?,status_json=?,
                last_seen_at=?,updated_at=?
            WHERE client_id=?
            """,
            (_json(desired), _json(current), _json(status_data), now, now, client_id),
        )

    return {
        "ok": True,
        "shop_key": shop_key,
        "desired_enabled": bool(desired.get("bot_enabled", current_enabled)),
        "server_time": bot_web_console._now(),
    }
