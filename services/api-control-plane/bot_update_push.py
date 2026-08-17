from __future__ import annotations

import asyncio
import json
import os
import time
from typing import Iterable, Tuple

from fastapi import APIRouter, Request
from fastapi.responses import StreamingResponse

import bot_update_cache


router = APIRouter()

MIRROR_READY_GRACE_SECONDS = max(
    10,
    min(300, int(os.getenv("BOT_UPDATE_PUSH_MIRROR_GRACE_SECONDS", "75"))),
)


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


def _mirror_ready(metadata: dict) -> bool:
    """Return true only for an already verified/atomically published mirror package.

    ensure_cached_package() writes to *.partial and only renames to the final package after
    SHA-256 validation, so checking the final file (and expected size when known) is enough
    here and avoids hashing a ~10MB package every five seconds for every SSE client.
    """
    try:
        tag = str(metadata.get("tag") or "").strip()
        if not tag:
            return False
        target = bot_update_cache._metadata_tag_dir(tag) / bot_update_cache.PACKAGE_ASSET_NAME
        if not target.is_file():
            return False
        expected_size = int(metadata.get("size") or 0)
        return expected_size <= 0 or target.stat().st_size == expected_size
    except Exception:
        return False


@router.get("/api/public/v1/bot-update/events", name="bot_update_event_stream")
async def bot_update_event_stream(
    request: Request,
    current_version: str = "",
) -> StreamingResponse:
    """Server-driven release notification stream.

    The control plane discovers releases and prefetches/validates the server mirror. A newly
    discovered version is intentionally held for a short grace period so clients normally
    receive the notification only after the server package is ready. If the mirror still is
    not ready after the grace period, the event is sent with mirror_url cleared, which makes
    the client use GitHub directly instead of wasting a connection timeout on an unready
    server endpoint.
    """

    async def events() -> Iterable[str]:
        last_sent_version = ""
        pending_version = ""
        pending_since = 0.0
        heartbeat = 0
        while True:
            if await request.is_disconnected():
                break
            try:
                metadata = bot_update_cache.get_latest_metadata()
                public = bot_update_cache._public_metadata(metadata, request)
                version = str(public.get("version") or "").strip()
                if version != pending_version:
                    pending_version = version
                    pending_since = time.monotonic()

                if (
                    version
                    and version != last_sent_version
                    and _is_newer(version, current_version)
                ):
                    ready = _mirror_ready(metadata)
                    waited = max(0.0, time.monotonic() - pending_since)
                    if not ready and waited < MIRROR_READY_GRACE_SECONDS:
                        # The prefetch thread is allowed to finish first. Keep the SSE alive,
                        # but do not advertise a server download URL that will block while the
                        # server itself is still fetching the GitHub package.
                        pass
                    else:
                        public["notification_mode"] = "server-push-sse"
                        public["mirror_ready"] = bool(ready)
                        if not ready:
                            public["mirror_url"] = ""
                            public["mirror_wait_seconds"] = int(waited)
                        yield _encode_event(public)
                        last_sent_version = version
            except Exception:
                # Keep the stream alive. The cache refresher/prefetcher retries independently.
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
