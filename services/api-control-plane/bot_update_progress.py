from __future__ import annotations

import hashlib
import os
import time
from pathlib import Path
from typing import Any, Dict, Optional

import bot_update_cache


_INSTALLED = False
_ORIGINAL_STATUS = bot_update_cache.get_cached_package_status

DOWNLOAD_MAX_ATTEMPTS = max(1, min(20, int(os.getenv("BOT_UPDATE_DOWNLOAD_MAX_ATTEMPTS", "8"))))
DOWNLOAD_RETRY_BASE_SECONDS = max(1, min(60, int(os.getenv("BOT_UPDATE_DOWNLOAD_RETRY_BASE_SECONDS", "2"))))
DOWNLOAD_RETRY_MAX_SECONDS = max(DOWNLOAD_RETRY_BASE_SECONDS, min(300, int(os.getenv("BOT_UPDATE_DOWNLOAD_RETRY_MAX_SECONDS", "60"))))
DOWNLOAD_CONNECT_TIMEOUT_SECONDS = max(3, min(120, int(os.getenv("BOT_UPDATE_DOWNLOAD_CONNECT_TIMEOUT_SECONDS", "15"))))
DOWNLOAD_READ_TIMEOUT_SECONDS = max(30, min(3600, int(os.getenv("BOT_UPDATE_DOWNLOAD_READ_TIMEOUT_SECONDS", "300"))))
GITHUB_PROXY = os.getenv("BOT_UPDATE_GITHUB_PROXY", "").strip()


def _request_proxy_kwargs() -> Dict[str, Any]:
    if not GITHUB_PROXY:
        return {}
    return {"proxies": {"http": GITHUB_PROXY, "https": GITHUB_PROXY}}


def _network_source() -> str:
    return "github-https-proxy" if GITHUB_PROXY else "github-https-direct"


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


def _hash_existing(path: Path) -> tuple[hashlib._Hash, int]:
    digest = hashlib.sha256()
    copied = 0
    if not path.is_file():
        return digest, copied
    with path.open("rb") as handle:
        while True:
            block = handle.read(1024 * 1024)
            if not block:
                break
            digest.update(block)
            copied += len(block)
    return digest, copied


def _retry_delay(attempt: int) -> int:
    return min(DOWNLOAD_RETRY_MAX_SECONDS, DOWNLOAD_RETRY_BASE_SECONDS * (2 ** max(0, attempt - 1)))


def _fetch_json_resilient(url: str, timeout: int) -> Dict[str, Any]:
    last_error: Optional[Exception] = None
    attempts = min(4, DOWNLOAD_MAX_ATTEMPTS)
    for attempt in range(1, attempts + 1):
        try:
            response = bot_update_cache.curl_requests.get(
                url,
                headers=bot_update_cache._github_headers(),
                timeout=(DOWNLOAD_CONNECT_TIMEOUT_SECONDS, max(timeout, DOWNLOAD_READ_TIMEOUT_SECONDS)),
                allow_redirects=True,
                impersonate="chrome",
                **_request_proxy_kwargs(),
            )
            try:
                if response.status_code < 200 or response.status_code >= 300:
                    raise RuntimeError(f"GitHub 返回 HTTP {response.status_code}")
                payload = response.json()
                if not isinstance(payload, dict):
                    raise RuntimeError("GitHub 返回的更新数据格式无效")
                return payload
            finally:
                try:
                    response.close()
                except Exception:
                    pass
        except Exception as exc:
            last_error = exc
            if attempt < attempts:
                time.sleep(min(8, _retry_delay(attempt)))
    raise RuntimeError(f"GitHub 元数据连续请求失败 {attempts} 次：{last_error}")


def _progress_download(download_url: str, destination: Path, expected_sha256: str, expected_size: int) -> None:
    tag = destination.parent.name
    partial = destination.with_suffix(destination.suffix + ".partial")
    partial.parent.mkdir(parents=True, exist_ok=True)
    if expected_size > 0 and partial.is_file() and partial.stat().st_size > expected_size:
        partial.unlink(missing_ok=True)

    last_error: Optional[Exception] = None
    for attempt in range(1, DOWNLOAD_MAX_ATTEMPTS + 1):
        response = None
        try:
            digest, existing = _hash_existing(partial)
            if expected_size > 0 and existing == expected_size:
                actual = digest.hexdigest()
                if actual.lower() == expected_sha256.lower():
                    partial.replace(destination)
                    _update_job(
                        tag,
                        phase="ready",
                        downloaded_bytes=existing,
                        total_bytes=expected_size,
                        progress_percent=100,
                        speed_bps=0,
                        eta_seconds=0,
                        attempt=attempt,
                        max_attempts=DOWNLOAD_MAX_ATTEMPTS,
                        source=_network_source(),
                        proxy_enabled=bool(GITHUB_PROXY),
                        resumable=True,
                        downloading=False,
                        ready=True,
                        error="",
                        last_error="",
                    )
                    return
                partial.unlink(missing_ok=True)
                digest, existing = _hash_existing(partial)

            headers = {
                "Accept": "application/octet-stream",
                "User-Agent": "QianniuAiBot-UpdateMirror/1.2",
            }
            if existing > 0:
                headers["Range"] = f"bytes={existing}-"

            _update_job(
                tag,
                phase="resuming" if existing > 0 else "connecting",
                downloaded_bytes=existing,
                total_bytes=expected_size,
                progress_percent=min(98, int(existing * 100 / expected_size)) if expected_size > 0 else 0,
                speed_bps=0,
                eta_seconds=None,
                attempt=attempt,
                max_attempts=DOWNLOAD_MAX_ATTEMPTS,
                retry_in_seconds=0,
                source=_network_source(),
                proxy_enabled=bool(GITHUB_PROXY),
                resumable=True,
                downloading=True,
                ready=False,
                error="",
            )

            response = bot_update_cache.curl_requests.get(
                download_url,
                headers=headers,
                timeout=(DOWNLOAD_CONNECT_TIMEOUT_SECONDS, DOWNLOAD_READ_TIMEOUT_SECONDS),
                allow_redirects=True,
                impersonate="chrome",
                stream=True,
                **_request_proxy_kwargs(),
            )

            if existing > 0 and response.status_code == 416 and expected_size > 0 and existing == expected_size:
                continue
            if response.status_code < 200 or response.status_code >= 300:
                raise RuntimeError(f"GitHub 安装包返回 HTTP {response.status_code}")

            append = existing > 0 and response.status_code == 206
            if existing > 0 and not append:
                # Some GitHub/CDN paths may ignore Range and return 200. Restart safely instead of
                # appending a complete response to the partial file.
                existing = 0
                digest = hashlib.sha256()
                partial.unlink(missing_ok=True)

            header_size = 0
            try:
                header_size = int(response.headers.get("Content-Length") or 0)
            except Exception:
                header_size = 0
            total = int(expected_size or (existing + header_size if append else header_size) or 0)
            copied = existing
            started_at = time.monotonic()
            last_emit_at = started_at
            last_emit_bytes = copied
            mode = "ab" if append else "wb"

            _update_job(
                tag,
                phase="downloading",
                downloaded_bytes=copied,
                total_bytes=total,
                progress_percent=min(98, int(copied * 100 / total)) if total > 0 else 0,
                attempt=attempt,
                max_attempts=DOWNLOAD_MAX_ATTEMPTS,
            )

            with partial.open(mode) as output:
                for chunk in response.iter_content(chunk_size=1024 * 1024):
                    if not chunk:
                        continue
                    output.write(chunk)
                    output.flush()
                    digest.update(chunk)
                    copied += len(chunk)
                    now = time.monotonic()
                    if now - last_emit_at >= 0.5:
                        interval = max(0.001, now - last_emit_at)
                        instant_speed = max(0.0, (copied - last_emit_bytes) / interval)
                        average_speed = max(0.0, (copied - existing) / max(0.001, now - started_at))
                        speed = instant_speed if instant_speed > 0 else average_speed
                        remaining = max(0, total - copied) if total > 0 else 0
                        eta = int(remaining / speed) if remaining > 0 and speed > 1 else None
                        percent = min(98, int(copied * 100 / total)) if total > 0 else 0
                        _update_job(
                            tag,
                            phase="downloading",
                            downloaded_bytes=copied,
                            total_bytes=total,
                            progress_percent=percent,
                            speed_bps=int(speed),
                            eta_seconds=eta,
                            attempt=attempt,
                            max_attempts=DOWNLOAD_MAX_ATTEMPTS,
                        )
                        last_emit_at = now
                        last_emit_bytes = copied

            if expected_size > 0 and copied < expected_size:
                raise RuntimeError(f"下载连接提前结束：已下载 {copied}/{expected_size} 字节")
            if expected_size > 0 and copied > expected_size:
                partial.unlink(missing_ok=True)
                raise RuntimeError(f"服务端镜像安装包大小异常：期望 {expected_size}，实际 {copied}")

            _update_job(
                tag,
                phase="verifying",
                downloaded_bytes=copied,
                total_bytes=total or copied,
                progress_percent=99,
                speed_bps=0,
                eta_seconds=0,
                attempt=attempt,
                max_attempts=DOWNLOAD_MAX_ATTEMPTS,
            )
            actual = digest.hexdigest()
            if actual.lower() != expected_sha256.lower():
                partial.unlink(missing_ok=True)
                raise RuntimeError("服务端镜像安装包 SHA-256 校验失败，已丢弃损坏的 partial 文件")
            if expected_size > 0 and copied != expected_size:
                raise RuntimeError(f"服务端镜像安装包大小不一致：期望 {expected_size}，实际 {copied}")

            partial.replace(destination)
            _update_job(
                tag,
                phase="ready",
                downloaded_bytes=copied,
                total_bytes=total or copied,
                progress_percent=100,
                speed_bps=0,
                eta_seconds=0,
                attempt=attempt,
                max_attempts=DOWNLOAD_MAX_ATTEMPTS,
                retry_in_seconds=0,
                source=_network_source(),
                proxy_enabled=bool(GITHUB_PROXY),
                resumable=True,
                downloading=False,
                ready=True,
                error="",
                last_error="",
            )
            return
        except Exception as exc:
            last_error = exc
            if attempt >= DOWNLOAD_MAX_ATTEMPTS:
                break
            delay = _retry_delay(attempt)
            partial_size = partial.stat().st_size if partial.is_file() else 0
            _update_job(
                tag,
                phase="retrying",
                downloaded_bytes=partial_size,
                total_bytes=expected_size,
                progress_percent=min(98, int(partial_size * 100 / expected_size)) if expected_size > 0 else 0,
                speed_bps=0,
                eta_seconds=None,
                attempt=attempt,
                max_attempts=DOWNLOAD_MAX_ATTEMPTS,
                retry_in_seconds=delay,
                source=_network_source(),
                proxy_enabled=bool(GITHUB_PROXY),
                resumable=True,
                downloading=True,
                ready=False,
                error="",
                last_error=str(exc)[:300],
            )
            time.sleep(delay)
        finally:
            if response is not None:
                try:
                    response.close()
                except Exception:
                    pass

    partial_size = partial.stat().st_size if partial.is_file() else 0
    _update_job(
        tag,
        phase="failed",
        downloaded_bytes=partial_size,
        total_bytes=expected_size,
        progress_percent=min(98, int(partial_size * 100 / expected_size)) if expected_size > 0 else 0,
        speed_bps=0,
        eta_seconds=None,
        attempt=DOWNLOAD_MAX_ATTEMPTS,
        max_attempts=DOWNLOAD_MAX_ATTEMPTS,
        retry_in_seconds=0,
        source=_network_source(),
        proxy_enabled=bool(GITHUB_PROXY),
        resumable=True,
        downloading=False,
        ready=False,
        error=str(last_error or "GitHub 安装包下载失败")[:300],
        last_error=str(last_error or "")[:300],
    )
    raise RuntimeError(f"GitHub 安装包连续下载失败 {DOWNLOAD_MAX_ATTEMPTS} 次：{last_error}")


def _status_with_progress(metadata: Dict[str, Any]) -> Dict[str, Any]:
    state = dict(_ORIGINAL_STATUS(metadata))
    tag = str(metadata.get("tag") or "")
    total = int(metadata.get("size") or 0)
    target = bot_update_cache._package_target(metadata)
    if state.get("ready") and target.is_file():
        downloaded = int(target.stat().st_size)
        state.update(
            {
                "phase": "ready",
                "downloaded_bytes": downloaded,
                "total_bytes": total or downloaded,
                "progress_percent": 100,
                "speed_bps": 0,
                "eta_seconds": 0,
                "source": _network_source(),
                "proxy_enabled": bool(GITHUB_PROXY),
                "resumable": True,
            }
        )
        return state
    with bot_update_cache._PACKAGE_JOB_LOCK:
        live = dict(bot_update_cache._PACKAGE_JOBS.get(tag) or {})
    for key in (
        "phase",
        "downloaded_bytes",
        "total_bytes",
        "progress_percent",
        "speed_bps",
        "eta_seconds",
        "attempt",
        "max_attempts",
        "retry_in_seconds",
        "source",
        "proxy_enabled",
        "resumable",
        "last_error",
    ):
        if key in live:
            state[key] = live[key]
    partial = target.with_suffix(target.suffix + ".partial")
    if not state.get("downloaded_bytes") and partial.is_file():
        state["downloaded_bytes"] = int(partial.stat().st_size)
    state.setdefault("phase", "downloading" if state.get("downloading") else "waiting")
    state.setdefault("downloaded_bytes", 0)
    state.setdefault("total_bytes", total)
    state.setdefault("progress_percent", min(98, int(state["downloaded_bytes"] * 100 / total)) if total > 0 else 0)
    state.setdefault("speed_bps", 0)
    state.setdefault("eta_seconds", None)
    state.setdefault("attempt", 0)
    state.setdefault("max_attempts", DOWNLOAD_MAX_ATTEMPTS)
    state.setdefault("retry_in_seconds", 0)
    state.setdefault("source", _network_source())
    state.setdefault("proxy_enabled", bool(GITHUB_PROXY))
    state.setdefault("resumable", True)
    return state


def install() -> None:
    global _INSTALLED
    if _INSTALLED:
        return
    # Install the resilient network layer before any update-cache startup task can contact GitHub.
    bot_update_cache._fetch_json = _fetch_json_resilient
    bot_update_cache._download_package = _progress_download
    bot_update_cache.get_cached_package_status = _status_with_progress
    _INSTALLED = True
