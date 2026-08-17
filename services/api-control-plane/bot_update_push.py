from __future__ import annotations

import asyncio
import json
from typing import Iterable, Tuple

from fastapi import APIRouter, Request
from fastapi.responses import StreamingResponse

import bot_update_cache


router = APIRouter()


def _version_tuple(value: str) -> Tuple[int, ...]:
    text = str(value or "").strip().lower()
    if text.startswith("bot-v"):
        text = text[5:]
    elif text.startswith("v"):
        text = text[1:]
    text = text.split("-", 1)[0]
    parts = []
    for item in text.split("."):
        try:
            parts.append(max(0, int(item)))
        except Exception:
            parts.append(0)
    while len(parts) < 4:
        parts.append(0)
    return tuple(parts[:4])


def _is_newer(candidate: str, current: str) -> bool:
    return _version_tuple(candidate) > _version_tuple(current)


def _encode_event(payload: dict) -> str:
    return "event: bot-update\ndata: " + json.dumps(
        payload, ensure_ascii=False, separators=(",", ":")
    ) + "\n\n"


@router.get("/api/public/v1/bot-update/events", name="bot_update_event_stream")
async def bot_update_event_stream(
    request: Request,
    current_version: str = "",
) -> StreamingResponse:
    """Server-driven release notification stream.

    Clients keep one long-lived SSE connection. They never poll GitHub or the metadata
    endpoint in the background. The control-plane's own cache refresher is the only
    component that discovers releases; this stream simply pushes a newer cached release
    to connected Bot processes as soon as the server observes it.
    """

    async def events() -> Iterable[str]:
        last_sent_version = ""
        heartbeat = 0
        while True:
            if await request.is_disconnected():
                break
            try:
                metadata = bot_update_cache.get_latest_metadata()
                public = bot_update_cache._public_metadata(metadata, request)
                version = str(public.get("version") or "").strip()
                if (
                    version
                    and version != last_sent_version
                    and _is_newer(version, current_version)
                ):
                    public["notification_mode"] = "server-push-sse"
                    yield _encode_event(public)
                    last_sent_version = version
            except Exception as exc:
                # Keep the stream alive. The cache refresher will retry independently.
                if heartbeat % 6 == 0:
                    yield ": update-cache-temporarily-unavailable\n\n"

            heartbeat += 1
            if heartbeat % 6 == 0:
                yield ": keep-alive\n\n"
            await asyncio.sleep(5)

    return StreamingResponse(
        events(),
        media_type="text/event-stream",
        headers={
            "Cache-Control": "no-cache, no-transform",
            "Connection": "keep-alive",
            "X-Accel-Buffering": "no",
            "X-Bot-Update-Mode": "server-push-sse",
        },
    )
