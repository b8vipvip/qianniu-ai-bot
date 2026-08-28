from __future__ import annotations

import hashlib
import time
from pathlib import Path
from typing import Any, Dict

import bot_update_cache


_INSTALLED = False
_ORIGINAL_STATUS = bot_update_cache.get_cached_package_status


def _update_job(tag: str, **changes: Any) -> None:
    with bot_update_cache._PACKAGE_JOB_LOCK:
        state: Dict[str, Any] = dict(bot_update_cache._PACKAGE_JOBS.get(tag) or {})
        state.setdefault("tag", tag)
        state.setdefault("ready", False)
        state.setdefault("downloading", True)
        state.setdefault("error", "")
        state.update(changes)
        state["updated_at_unix"] = time.time()
        bot_update_cache._PACKAGE_JOBS[tag] = state


def _progress_download(download_url: str, destination: Path, expected_sha256: str, expected_size: int) -> None:
    tag = destination.parent.name
    partial = destination.with_suffix(destination.suffix + ".partial")
    partial.unlink(missing_ok=True)
    response = bot_update_cache.curl_requests.get(
        download_url,
        headers={"Accept": "application/octet-stream", "User-Agent": "QianniuAiBot-UpdateMirror/1.1"},
        timeout=bot_update_cache.PACKAGE_TIMEOUT_SECONDS,
        allow_redirects=True,
        impersonate="chrome",
        stream=True,
    )
    if response.status_code < 200 or response.status_code >= 300:
        raise RuntimeError(f"GitHub 安装包返回 HTTP {response.status_code}")
    header_size = 0
    try:
        header_size = int(response.headers.get("Content-Length") or 0)
    except Exception:
        header_size = 0
    total = int(expected_size or header_size or 0)
    digest = hashlib.sha256()
    copied = 0
    last_emit = 0.0
    _update_job(tag, phase="downloading", downloaded_bytes=0, total_bytes=total, progress_percent=0, downloading=True, ready=False, error="")
    try:
        with partial.open("wb") as output:
            for chunk in response.iter_content(chunk_size=1024 * 1024):
                if not chunk:
                    continue
                output.write(chunk)
                digest.update(chunk)
                copied += len(chunk)
                now = time.monotonic()
                if now - last_emit >= 0.2:
                    percent = min(98, int(copied * 100 / total)) if total > 0 else 0
                    _update_job(tag, phase="downloading", downloaded_bytes=copied, total_bytes=total, progress_percent=percent)
                    last_emit = now
    finally:
        try:
            response.close()
        except Exception:
            pass
    _update_job(tag, phase="verifying", downloaded_bytes=copied, total_bytes=total or copied, progress_percent=99)
    actual = digest.hexdigest()
    if actual.lower() != expected_sha256.lower():
        partial.unlink(missing_ok=True)
        raise RuntimeError("服务端镜像安装包 SHA-256 校验失败")
    if expected_size > 0 and copied != expected_size:
        partial.unlink(missing_ok=True)
        raise RuntimeError(f"服务端镜像安装包大小不一致：期望 {expected_size}，实际 {copied}")
    partial.replace(destination)
    _update_job(tag, phase="ready", downloaded_bytes=copied, total_bytes=total or copied, progress_percent=100)


def _status_with_progress(metadata: Dict[str, Any]) -> Dict[str, Any]:
    state = dict(_ORIGINAL_STATUS(metadata))
    tag = str(metadata.get("tag") or "")
    total = int(metadata.get("size") or 0)
    target = bot_update_cache._package_target(metadata)
    if state.get("ready") and target.is_file():
        downloaded = int(target.stat().st_size)
        state.update({"phase": "ready", "downloaded_bytes": downloaded, "total_bytes": total or downloaded, "progress_percent": 100})
        return state
    with bot_update_cache._PACKAGE_JOB_LOCK:
        live = dict(bot_update_cache._PACKAGE_JOBS.get(tag) or {})
    for key in ("phase", "downloaded_bytes", "total_bytes", "progress_percent"):
        if key in live:
            state[key] = live[key]
    state.setdefault("phase", "downloading" if state.get("downloading") else "waiting")
    state.setdefault("downloaded_bytes", 0)
    state.setdefault("total_bytes", total)
    state.setdefault("progress_percent", 0)
    return state


def install() -> None:
    global _INSTALLED
    if _INSTALLED:
        return
    bot_update_cache._download_package = _progress_download
    bot_update_cache.get_cached_package_status = _status_with_progress
    _INSTALLED = True
