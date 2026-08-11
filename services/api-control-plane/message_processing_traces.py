from __future__ import annotations

from datetime import datetime, timedelta, timezone
from typing import Any, Dict, List, Optional

from fastapi import APIRouter, Depends, HTTPException, Query, Request
from pydantic import BaseModel, Field

import bot_client_shop_binding
import bot_web_console as core


router = APIRouter()
_cp: Any = None


class TraceEventInput(BaseModel):
    event_id: str = Field(default="", max_length=80)
    trace_id: str = Field(default="", max_length=80)
    seller: str = Field(default="", max_length=160)
    buyer: str = Field(default="", max_length=160)
    stage: str = Field(default="", max_length=80)
    status: str = Field(default="", max_length=40)
    summary: str = Field(default="", max_length=300)
    detail: str = Field(default="", max_length=2000)
    duration_ms: int = Field(default=0, ge=0, le=3_600_000)
    occurred_at: str = Field(default="", max_length=80)


class TraceBatchInput(BaseModel):
    events: List[TraceEventInput] = Field(default_factory=list)


def install(control_plane: Any) -> None:
    global _cp
    _cp = control_plane
    control_plane.app.include_router(router)


def init_db() -> None:
    with _cp.db() as conn:
        conn.executescript(
            """
            CREATE TABLE IF NOT EXISTS bot_message_processing_traces (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                client_id INTEGER NOT NULL,
                shop_key TEXT NOT NULL,
                event_id TEXT NOT NULL,
                trace_id TEXT NOT NULL,
                seller TEXT NOT NULL DEFAULT '',
                buyer TEXT NOT NULL DEFAULT '',
                stage TEXT NOT NULL DEFAULT '',
                status TEXT NOT NULL DEFAULT '',
                summary TEXT NOT NULL DEFAULT '',
                detail TEXT NOT NULL DEFAULT '',
                duration_ms INTEGER NOT NULL DEFAULT 0,
                occurred_at TEXT NOT NULL,
                created_at TEXT NOT NULL,
                UNIQUE(client_id, event_id),
                FOREIGN KEY(client_id) REFERENCES client_tokens(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_bot_processing_trace_client
            ON bot_message_processing_traces(client_id, id DESC);

            CREATE INDEX IF NOT EXISTS idx_bot_processing_trace_shop
            ON bot_message_processing_traces(shop_key, id DESC);

            CREATE INDEX IF NOT EXISTS idx_bot_processing_trace_conversation
            ON bot_message_processing_traces(client_id, shop_key, seller, buyer, id DESC);

            CREATE INDEX IF NOT EXISTS idx_bot_processing_trace_trace_id
            ON bot_message_processing_traces(client_id, trace_id, id ASC);
            """
        )


def _safe(value: Any, limit: int) -> str:
    text = str(value or "").replace("\x00", "").replace("\r", " ").replace("\n", " ").strip()
    while "  " in text:
        text = text.replace("  ", " ")
    return text if len(text) <= limit else text[:limit] + "..."


def _binding_shop_key(client_id: int) -> str:
    with _cp.db() as conn:
        row = conn.execute(
            "SELECT shop_key FROM bot_client_shop_binding WHERE client_id=?",
            (client_id,),
        ).fetchone()
    return _safe(row["shop_key"] if row else "", 160)


def _cleanup(client_id: int) -> None:
    threshold = (datetime.now(timezone.utc) - timedelta(days=14)).isoformat(timespec="seconds")
    with _cp.db() as conn:
        conn.execute(
            "DELETE FROM bot_message_processing_traces WHERE client_id=? AND occurred_at<?",
            (client_id, threshold),
        )
        row = conn.execute(
            "SELECT COUNT(*) c FROM bot_message_processing_traces WHERE client_id=?",
            (client_id,),
        ).fetchone()
        count = int(row["c"] if row else 0)
        if count > 20_000:
            conn.execute(
                """
                DELETE FROM bot_message_processing_traces
                WHERE client_id=? AND id NOT IN (
                    SELECT id FROM bot_message_processing_traces
                    WHERE client_id=? ORDER BY id DESC LIMIT 20000
                )
                """,
                (client_id, client_id),
            )


@router.post("/api/runtime/v1/message-processing-traces/batch")
def runtime_trace_batch(data: TraceBatchInput, request: Request) -> Dict[str, Any]:
    client = core._runtime_client(request)
    client_id = int(client["id"])
    shop_key = _safe(request.headers.get("x-shop-key") or "", 160)
    if not shop_key:
        raise HTTPException(status_code=400, detail="缺少 X-Shop-Key")
    bot_client_shop_binding.ensure_binding(client_id, shop_key, False, "")

    saved = 0
    now = _cp.iso_now()
    with _cp.db() as conn:
        for event in data.events[:500]:
            event_id = _safe(event.event_id, 80)
            trace_id = _safe(event.trace_id, 80)
            if not event_id or not trace_id:
                continue
            occurred_at = _safe(event.occurred_at or now, 80)
            cursor = conn.execute(
                """
                INSERT OR IGNORE INTO bot_message_processing_traces(
                    client_id,shop_key,event_id,trace_id,seller,buyer,stage,status,
                    summary,detail,duration_ms,occurred_at,created_at
                ) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)
                """,
                (
                    client_id,
                    shop_key,
                    event_id,
                    trace_id,
                    _safe(event.seller, 160),
                    _safe(event.buyer, 160),
                    _safe(event.stage, 80),
                    _safe(event.status, 40),
                    _safe(event.summary, 300),
                    _safe(event.detail, 2000),
                    max(0, int(event.duration_ms or 0)),
                    occurred_at,
                    now,
                ),
            )
            if cursor.rowcount > 0:
                saved += 1
    _cleanup(client_id)
    return {"ok": True, "saved": saved, "shop_key": shop_key}


@router.get("/api/admin/message-processing-traces")
def admin_message_processing_traces(
    client_id: int = Query(0, ge=0),
    shop_key: str = Query("", max_length=160),
    seller: str = Query("", max_length=160),
    buyer: str = Query("", max_length=160),
    status: str = Query("", max_length=40),
    trace_id: str = Query("", max_length=80),
    limit: int = Query(300, ge=1, le=1000),
    _: str = Depends(lambda request: _cp.require_admin(request)),
) -> List[Dict[str, Any]]:
    where: List[str] = []
    values: List[Any] = []
    if client_id > 0:
        where.append("t.client_id=?")
        values.append(client_id)
    if shop_key.strip():
        where.append("t.shop_key=?")
        values.append(shop_key.strip())
    if seller.strip():
        where.append("t.seller LIKE ?")
        values.append("%" + seller.strip() + "%")
    if buyer.strip():
        where.append("t.buyer LIKE ?")
        values.append("%" + buyer.strip() + "%")
    if status.strip():
        where.append("t.status=?")
        values.append(status.strip())
    if trace_id.strip():
        where.append("t.trace_id=?")
        values.append(trace_id.strip())
    sql = """
        SELECT t.*, c.name client_name
        FROM bot_message_processing_traces t
        JOIN client_tokens c ON c.id=t.client_id
    """
    if where:
        sql += " WHERE " + " AND ".join(where)
    sql += " ORDER BY t.id DESC LIMIT ?"
    values.append(limit)
    with _cp.db() as conn:
        rows = conn.execute(sql, tuple(values)).fetchall()
    return [dict(row) for row in rows]
