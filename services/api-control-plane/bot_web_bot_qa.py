from __future__ import annotations

import re
from datetime import datetime, timezone
from typing import Any, Dict, Iterable, List, Optional, Tuple

from fastapi import APIRouter, Depends, Query
from pydantic import BaseModel, Field

import bot_web_console as core


router = APIRouter()
_AI_MARKER = re.compile(r"\s*\[AI\]\s*$", re.IGNORECASE)
_BOT_ANSWER_TYPES = {"bot_answer", "ai_answer", "bot_reply"}
_DUPLICATE_WINDOW_SECONDS = 120
_QUESTION_LOOKBACK_SECONDS = 300
_QUESTION_BURST_GAP_SECONDS = 45


def install(control_plane: Any) -> None:
    # These routes must be registered before the legacy all-chat conversation
    # routes. Starlette resolves the first matching route, so the Web page sees
    # Bot Q&A records while the older knowledge routes remain available.
    control_plane.app.include_router(router)


def _parse_time(value: Any) -> Optional[datetime]:
    text = str(value or "").strip()
    if not text:
        return None
    try:
        parsed = datetime.fromisoformat(text.replace("Z", "+00:00"))
        if parsed.tzinfo is None:
            parsed = parsed.replace(tzinfo=timezone.utc)
        return parsed.astimezone(timezone.utc)
    except Exception:
        return None


def _seconds_between(left: Dict[str, Any], right: Dict[str, Any]) -> Optional[float]:
    left_at = _parse_time(left.get("occurred_at"))
    right_at = _parse_time(right.get("occurred_at"))
    if left_at is None or right_at is None:
        return None
    return abs((right_at - left_at).total_seconds())


def _normalize_text(value: Any, strip_ai_marker: bool = False) -> str:
    text = str(value or "").replace("\x00", "").strip()
    if strip_ai_marker:
        text = _AI_MARKER.sub("", text).strip()
    text = re.sub(r"\s+", " ", text)
    return text


def _is_bot_answer(row: Dict[str, Any]) -> bool:
    if str(row.get("role") or "").lower() != "assistant":
        return False
    message_type = str(row.get("message_type") or "").lower()
    if message_type in _BOT_ANSWER_TYPES:
        return True
    return bool(_AI_MARKER.search(str(row.get("text") or "")))


def _deduplicate_consecutive(rows: Iterable[Dict[str, Any]]) -> List[Dict[str, Any]]:
    result: List[Dict[str, Any]] = []
    for original in rows:
        row = dict(original)
        role = str(row.get("role") or "").lower()
        normalized = _normalize_text(row.get("text"), strip_ai_marker=_is_bot_answer(row))
        if not normalized:
            continue
        row["_normalized_text"] = normalized

        if result:
            previous = result[-1]
            same_role = str(previous.get("role") or "").lower() == role
            same_text = str(previous.get("_normalized_text") or "") == normalized
            delta = _seconds_between(previous, row)
            if same_role and same_text and (delta is None or delta <= _DUPLICATE_WINDOW_SECONDS):
                # Keep the earliest stable record. The duplicated context-store
                # and seller-echo records usually differ only by id/time.
                continue
        result.append(row)
    return result


def _question_tail(pending_users: List[Dict[str, Any]], answer: Dict[str, Any]) -> List[Dict[str, Any]]:
    if not pending_users:
        return []
    answer_at = _parse_time(answer.get("occurred_at"))
    eligible: List[Dict[str, Any]] = []
    for item in pending_users:
        item_at = _parse_time(item.get("occurred_at"))
        if answer_at is None or item_at is None:
            eligible.append(item)
            continue
        delta = (answer_at - item_at).total_seconds()
        if 0 <= delta <= _QUESTION_LOOKBACK_SECONDS:
            eligible.append(item)
    if not eligible:
        return []

    tail = [eligible[-1]]
    for item in reversed(eligible[:-1]):
        gap = _seconds_between(item, tail[0])
        if gap is not None and gap > _QUESTION_BURST_GAP_SECONDS:
            break
        tail.insert(0, item)
    return tail


def _public_message(row: Dict[str, Any], message_type: str) -> Dict[str, Any]:
    item = {key: value for key, value in row.items() if not str(key).startswith("_")}
    item["message_type"] = message_type
    if message_type == "bot_answer":
        item["text"] = _normalize_text(item.get("text"), strip_ai_marker=True)
    else:
        item["text"] = str(item.get("text") or "").strip()
    return item


def build_bot_qa_messages(rows: Iterable[Dict[str, Any]]) -> List[Dict[str, Any]]:
    ordered = sorted(
        (dict(row) for row in rows),
        key=lambda item: (int(item.get("id") or 0), str(item.get("occurred_at") or "")),
    )
    deduped = _deduplicate_consecutive(ordered)
    pending_users: List[Dict[str, Any]] = []
    output: List[Dict[str, Any]] = []
    recent_pairs: List[Tuple[str, Optional[datetime]]] = []

    for row in deduped:
        role = str(row.get("role") or "").lower()
        if role == "user":
            pending_users.append(row)
            if len(pending_users) > 40:
                pending_users = pending_users[-40:]
            continue

        if not _is_bot_answer(row):
            # Manual customer-service messages, fixed welcomes and Web manual
            # replies are intentionally excluded. They also do not reset the
            # pending buyer question, because the Bot may answer a moment later.
            continue

        questions = _question_tail(pending_users, row)
        pending_users = []
        if not questions:
            continue

        question_text = "\n".join(str(item.get("_normalized_text") or "") for item in questions)
        answer_text = str(row.get("_normalized_text") or "")
        pair_key = question_text + "\n=>\n" + answer_text
        answer_at = _parse_time(row.get("occurred_at"))

        duplicate_pair = False
        for previous_key, previous_at in reversed(recent_pairs[-10:]):
            if previous_key != pair_key:
                continue
            if answer_at is None or previous_at is None:
                duplicate_pair = True
                break
            if abs((answer_at - previous_at).total_seconds()) <= _DUPLICATE_WINDOW_SECONDS:
                duplicate_pair = True
                break
        if duplicate_pair:
            continue

        for question in questions:
            output.append(_public_message(question, "bot_question"))
        output.append(_public_message(row, "bot_answer"))
        recent_pairs.append((pair_key, answer_at))

    return output


def _group_all_messages(client_id: int) -> Dict[Tuple[str, str], List[Dict[str, Any]]]:
    cp = core._cp
    with cp.db() as conn:
        rows = conn.execute(
            """
            SELECT id,client_id,message_key,seller,buyer,role,text,message_type,occurred_at,created_at
            FROM bot_messages
            WHERE client_id=? AND buyer<>''
            ORDER BY seller ASC,buyer ASC,id ASC
            """,
            (client_id,),
        ).fetchall()
    grouped: Dict[Tuple[str, str], List[Dict[str, Any]]] = {}
    for row in rows:
        item = dict(row)
        key = (str(item.get("seller") or ""), str(item.get("buyer") or ""))
        grouped.setdefault(key, []).append(item)
    return grouped


def _last_read_map(client_id: int) -> Dict[Tuple[str, str], int]:
    cp = core._cp
    with cp.db() as conn:
        rows = conn.execute(
            "SELECT seller,buyer,last_read_message_id FROM bot_conversation_reads WHERE client_id=?",
            (client_id,),
        ).fetchall()
    return {
        (str(row["seller"] or ""), str(row["buyer"] or "")): int(row["last_read_message_id"] or 0)
        for row in rows
    }


class ConversationReadInput(BaseModel):
    seller: str = Field(min_length=1, max_length=120)
    buyer: str = Field(min_length=1, max_length=120)
    message_id: int = Field(default=0, ge=0)


@router.get("/api/bot-web/conversations")
def web_bot_qa_conversations(
    query: str = Query("", max_length=120),
    limit: int = Query(300, ge=1, le=1000),
    client: Dict[str, Any] = Depends(core._web_client),
) -> Dict[str, Any]:
    client_id = int(client["id"])
    grouped = _group_all_messages(client_id)
    reads = _last_read_map(client_id)
    needle = query.strip().lower()
    conversations: List[Dict[str, Any]] = []

    for (seller, buyer), raw_rows in grouped.items():
        messages = build_bot_qa_messages(raw_rows)
        if not messages:
            continue
        searchable = " ".join(
            [seller, buyer] + [str(message.get("text") or "") for message in messages]
        ).lower()
        if needle and needle not in searchable:
            continue

        last = messages[-1]
        last_read = reads.get((seller, buyer), 0)
        unread = sum(
            1
            for message in messages
            if message.get("message_type") == "bot_question"
            and int(message.get("id") or 0) > last_read
        )
        conversations.append(
            {
                "id": int(last.get("id") or 0),
                "seller": seller,
                "buyer": buyer,
                "role": last.get("role"),
                "text": last.get("text"),
                "message_type": last.get("message_type"),
                "occurred_at": last.get("occurred_at"),
                "last_read_message_id": last_read,
                "unread_count": unread,
                "record_scope": "bot_qa",
            }
        )

    conversations.sort(
        key=lambda item: (str(item.get("occurred_at") or ""), int(item.get("id") or 0)),
        reverse=True,
    )
    return {
        "conversations": conversations[:limit],
        "server_time": core._now(),
        "record_scope": "bot_qa",
    }


@router.get("/api/bot-web/conversation/messages")
def web_bot_qa_messages(
    seller: str = Query(..., min_length=1, max_length=120),
    buyer: str = Query(..., min_length=1, max_length=120),
    before_id: int = Query(0, ge=0),
    limit: int = Query(100, ge=1, le=300),
    client: Dict[str, Any] = Depends(core._web_client),
) -> Dict[str, Any]:
    client_id = int(client["id"])
    cp = core._cp
    with cp.db() as conn:
        rows = conn.execute(
            """
            SELECT id,client_id,message_key,seller,buyer,role,text,message_type,occurred_at,created_at
            FROM bot_messages
            WHERE client_id=? AND seller=? AND buyer=?
            ORDER BY id ASC
            """,
            (client_id, seller.strip(), buyer.strip()),
        ).fetchall()
    messages = build_bot_qa_messages(dict(row) for row in rows)
    if before_id > 0:
        messages = [message for message in messages if int(message.get("id") or 0) < before_id]
    has_more = len(messages) > limit
    return {
        "messages": messages[-limit:],
        "has_more": has_more,
        "record_scope": "bot_qa",
    }


@router.post("/api/bot-web/conversation/read")
def web_mark_bot_qa_read(
    data: ConversationReadInput,
    client: Dict[str, Any] = Depends(core._web_client),
) -> Dict[str, Any]:
    client_id = int(client["id"])
    cp = core._cp
    message_id = int(data.message_id or 0)
    if message_id < 1:
        with cp.db() as conn:
            rows = conn.execute(
                """
                SELECT id,client_id,message_key,seller,buyer,role,text,message_type,occurred_at,created_at
                FROM bot_messages
                WHERE client_id=? AND seller=? AND buyer=?
                ORDER BY id ASC
                """,
                (client_id, data.seller.strip(), data.buyer.strip()),
            ).fetchall()
        messages = build_bot_qa_messages(dict(row) for row in rows)
        message_id = max((int(message.get("id") or 0) for message in messages), default=0)

    with cp.db() as conn:
        conn.execute(
            """
            INSERT INTO bot_conversation_reads(client_id,seller,buyer,last_read_message_id,updated_at)
            VALUES(?,?,?,?,?)
            ON CONFLICT(client_id,seller,buyer) DO UPDATE SET
                last_read_message_id=MAX(last_read_message_id,excluded.last_read_message_id),
                updated_at=excluded.updated_at
            """,
            (client_id, data.seller.strip(), data.buyer.strip(), message_id, core._now()),
        )
    return {"ok": True, "last_read_message_id": message_id, "record_scope": "bot_qa"}
