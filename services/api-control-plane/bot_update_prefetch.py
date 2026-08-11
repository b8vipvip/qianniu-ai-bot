from __future__ import annotations

import logging
import os
import threading
from pathlib import Path
from typing import Optional

import bot_update_cache


LOGGER = logging.getLogger("bot-update-prefetch")
PREFETCH_ENABLED = os.getenv("BOT_UPDATE_PREFETCH_ENABLED", "true").strip().lower() not in {
    "0",
    "false",
    "no",
    "off",
}
PREFETCH_POLL_SECONDS = max(
    30,
    min(3600, int(os.getenv("BOT_UPDATE_PREFETCH_POLL_SECONDS", "60"))),
)

_STOP_EVENT = threading.Event()
_THREAD: Optional[threading.Thread] = None


def _prefetch_once() -> Optional[Path]:
    metadata = bot_update_cache.get_latest_metadata()
    path = bot_update_cache.ensure_cached_package(metadata)
    LOGGER.info(
        "Bot update mirror ready: tag=%s path=%s",
        metadata.get("tag") or "",
        path,
    )
    return path


def _prefetch_loop() -> None:
    while not _STOP_EVENT.is_set():
        try:
            _prefetch_once()
        except Exception as exc:
            LOGGER.warning("Bot update mirror prefetch failed: %s", exc)
        _STOP_EVENT.wait(PREFETCH_POLL_SECONDS)


def init_bot_update_prefetch() -> None:
    global _THREAD
    if not PREFETCH_ENABLED:
        LOGGER.info("Bot update mirror prefetch is disabled")
        return
    if _THREAD is not None and _THREAD.is_alive():
        return
    _STOP_EVENT.clear()
    _THREAD = threading.Thread(
        target=_prefetch_loop,
        name="bot-update-package-prefetch",
        daemon=True,
    )
    _THREAD.start()


def stop_bot_update_prefetch() -> None:
    global _THREAD
    _STOP_EVENT.set()
    thread = _THREAD
    if thread is not None and thread.is_alive():
        thread.join(timeout=2)
    _THREAD = None
