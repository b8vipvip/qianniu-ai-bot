from __future__ import annotations

import os
from typing import Any, Dict, Optional

from fastapi import Depends, HTTPException
from pydantic import BaseModel

OCR_FIRST = "ocr_first"
AI_FIRST = "ai_first"
_ALLOWED_PRIORITIES = {OCR_FIRST, AI_FIRST}
_DEFAULT_PRIORITY = (os.getenv("OCR_VISION_PRIORITY", OCR_FIRST) or OCR_FIRST).strip().lower()
if _DEFAULT_PRIORITY not in _ALLOWED_PRIORITIES:
    _DEFAULT_PRIORITY = OCR_FIRST

_INSTALLED = False
_CONTROL_PLANE: Optional[Any] = None


class OcrVisionPriorityUpdate(BaseModel):
    vision_priority: str = OCR_FIRST


def _normalize_priority(value: Any) -> str:
    priority = str(value or "").strip().lower()
    if priority not in _ALLOWED_PRIORITIES:
        raise HTTPException(status_code=422, detail="视觉理解优先级必须是 ocr_first 或 ai_first")
    return priority


def init_db(control_plane: Optional[Any] = None) -> None:
    cp = control_plane or _CONTROL_PLANE
    if cp is None:
        raise RuntimeError("runtime_ocr_priority 尚未安装到控制面")
    now = cp.iso_now()
    with cp.db() as conn:
        conn.execute(
            """
            CREATE TABLE IF NOT EXISTS runtime_ocr_vision_priority (
                id INTEGER PRIMARY KEY CHECK(id = 1),
                vision_priority TEXT NOT NULL DEFAULT 'ocr_first',
                updated_at TEXT NOT NULL,
                updated_by TEXT NOT NULL DEFAULT ''
            )
            """
        )
        conn.execute(
            """
            INSERT OR IGNORE INTO runtime_ocr_vision_priority(
                id, vision_priority, updated_at, updated_by
            ) VALUES(1, ?, ?, ?)
            """,
            (_DEFAULT_PRIORITY, now, "environment-defaults"),
        )


def _read(control_plane: Any) -> Dict[str, Any]:
    init_db(control_plane)
    with control_plane.db() as conn:
        row = conn.execute(
            "SELECT vision_priority, updated_at, updated_by FROM runtime_ocr_vision_priority WHERE id = 1"
        ).fetchone()
    if row is None:
        return {
            "vision_priority": _DEFAULT_PRIORITY,
            "source": "environment-defaults",
            "updated_at": "",
            "updated_by": "",
            "restart_required": False,
        }
    priority = str(row["vision_priority"] or OCR_FIRST).strip().lower()
    if priority not in _ALLOWED_PRIORITIES:
        priority = OCR_FIRST
    return {
        "vision_priority": priority,
        "source": "database",
        "updated_at": str(row["updated_at"] or ""),
        "updated_by": str(row["updated_by"] or ""),
        "restart_required": False,
    }


def _write(control_plane: Any, priority: str, updated_by: str, source: str = "database") -> Dict[str, Any]:
    priority = _normalize_priority(priority)
    now = control_plane.iso_now()
    init_db(control_plane)
    with control_plane.db() as conn:
        conn.execute(
            """
            INSERT INTO runtime_ocr_vision_priority(id, vision_priority, updated_at, updated_by)
            VALUES(1, ?, ?, ?)
            ON CONFLICT(id) DO UPDATE SET
                vision_priority=excluded.vision_priority,
                updated_at=excluded.updated_at,
                updated_by=excluded.updated_by
            """,
            (priority, now, updated_by),
        )
    return {
        "vision_priority": priority,
        "source": source,
        "updated_at": now,
        "updated_by": updated_by,
        "restart_required": False,
    }


def install(control_plane: Any) -> None:
    global _INSTALLED, _CONTROL_PLANE
    _CONTROL_PLANE = control_plane
    if _INSTALLED:
        return
    _INSTALLED = True

    app = control_plane.app
    require_admin = control_plane.require_admin
    require_client = control_plane.require_client

    @app.get("/api/admin/ocr/vision-priority")
    async def get_admin_ocr_vision_priority(
        admin_username: str = Depends(require_admin),
    ) -> Dict[str, Any]:
        return _read(control_plane)

    @app.put("/api/admin/ocr/vision-priority")
    async def update_admin_ocr_vision_priority(
        payload: OcrVisionPriorityUpdate,
        admin_username: str = Depends(require_admin),
    ) -> Dict[str, Any]:
        return _write(control_plane, payload.vision_priority, admin_username)

    @app.post("/api/admin/ocr/vision-priority/reset")
    async def reset_admin_ocr_vision_priority(
        admin_username: str = Depends(require_admin),
    ) -> Dict[str, Any]:
        return _write(control_plane, _DEFAULT_PRIORITY, admin_username, "environment-defaults")

    @app.get("/api/runtime/v1/ocr/vision-priority")
    async def get_runtime_ocr_vision_priority(
        client: Dict[str, Any] = Depends(require_client),
    ) -> Dict[str, Any]:
        data = _read(control_plane)
        return {
            "vision_priority": data["vision_priority"],
            "restart_required": False,
        }
