from __future__ import annotations

import asyncio
import json
import threading
import time
from typing import Iterable, Tuple

from fastapi import APIRouter, Request
from fastapi.responses import StreamingResponse

import bot_update_cache


router = APIRouter()
_PUSH_LOCK = threading.RLock()
_ACTIVE_STREAMS = 0
_LAST_PUSH_VERSION = ""
_LAST_PUSH_AT_UNIX = 0.0
_TOTAL_PUSH_EVENTS = 0


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
    return "event: bot-update\ndata: " + json.dumps(payload, ensure_ascii=False, separators=(",", ":")) + "\n\n"


def _mirror_ready(metadata: dict) -> bool:
    """Only announce a release after the complete server-side package is verified."""
    try:
        tag = str(metadata.get("tag") or "").strip()
        expected_sha = str(metadata.get("sha256") or "").strip().lower()
        if not tag or not expected_sha:
            return False
        target = bot_update_cache._metadata_tag_dir(tag) / bot_update_cache.PACKAGE_ASSET_NAME
        if not target.is_file():
            return False
        expected_size = int(metadata.get("size") or 0)
        if expected_size > 0 and target.stat().st_size != expected_size:
            return False
        return bot_update_cache._hash_file(target).lower() == expected_sha
    except Exception:
        return False


def get_push_status() -> dict:
    with _PUSH_LOCK:
        return {
            "active_streams": _ACTIVE_STREAMS,
            "last_push_version": _LAST_PUSH_VERSION,
            "last_push_at_unix": _LAST_PUSH_AT_UNIX,
            "total_push_events": _TOTAL_PUSH_EVENTS,
            "mode": "server-push-sse",
        }


def _stream_opened() -> None:
    global _ACTIVE_STREAMS
    with _PUSH_LOCK:
        _ACTIVE_STREAMS += 1


def _stream_closed() -> None:
    global _ACTIVE_STREAMS
    with _PUSH_LOCK:
        _ACTIVE_STREAMS = max(0, _ACTIVE_STREAMS - 1)


def _record_push(version: str) -> None:
    global _LAST_PUSH_VERSION, _LAST_PUSH_AT_UNIX, _TOTAL_PUSH_EVENTS
    with _PUSH_LOCK:
        _LAST_PUSH_VERSION = version
        _LAST_PUSH_AT_UNIX = time.time()
        _TOTAL_PUSH_EVENTS += 1


@router.get("/api/public/v1/bot-update/events", name="bot_update_event_stream")
async def bot_update_event_stream(request: Request, current_version: str = "") -> StreamingResponse:
    """Server-driven release notification stream.

    A release becomes client-visible only after the server has downloaded and verified the full
    package. While the mirror is incomplete this stream sends no update event. There is deliberately
    no timeout that announces the release early or sends the client to GitHub for the installer.
    """

    async def events() -> Iterable[str]:
        last_sent_version = ""
        heartbeat = 0
        _stream_opened()
        try:
            while True:
                if await request.is_disconnected():
                    break
                try:
                    metadata = bot_update_cache.get_latest_metadata()
                    public = bot_update_cache._public_metadata(metadata, request)
                    version = str(public.get("version") or "").strip()
                    if version and version != last_sent_version and _is_newer(version, current_version):
                        if _mirror_ready(metadata):
                            public["notification_mode"] = "server-push-sse"
                            public["mirror_ready"] = True
                            public["package_verified_on_server"] = True
                            _record_push(version)
                            yield _encode_event(public)
                            last_sent_version = version
                except Exception:
                    if heartbeat % 6 == 0:
                        yield ": update-cache-temporarily-unavailable\n\n"
                heartbeat += 1
                if heartbeat % 6 == 0:
                    yield ": keep-alive\n\n"
                await asyncio.sleep(5)
        finally:
            _stream_closed()

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
