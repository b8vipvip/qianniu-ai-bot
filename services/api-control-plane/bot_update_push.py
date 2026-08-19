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


def _mirror_ready(metadata: dict) -> bool:
    """Return true only after the complete GitHub package is present and verified on server.

    ensure_cached_package() downloads to *.partial, validates SHA-256 and expected size, and only
    then atomically renames the file to the final package path. Re-hash the final file here before
    announcing a release so notification/auto-install can never outrun server-side package readiness.
    """
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


@router.get("/api/public/v1/bot-update/events", name="bot_update_event_stream")
async def bot_update_event_stream(
    request: Request,
    current_version: str = "",
) -> StreamingResponse:
    """Server-driven release notification stream.

    A GitHub release is not a client-visible update until the control plane has successfully
    downloaded the entire package and verified its SHA-256/size. If prefetch is delayed or fails,
    this SSE connection stays alive and sends no update event. There is deliberately no timeout
    that falls back to notifying clients early or telling them to fetch the new package from GitHub.
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
                    if _mirror_ready(metadata):
                        public["notification_mode"] = "server-push-sse"
                        public["mirror_ready"] = True
                        public["package_verified_on_server"] = True
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
