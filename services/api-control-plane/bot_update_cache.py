from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import threading
import time
from pathlib import Path
from typing import Any, Dict, Optional

from curl_cffi import requests as curl_requests
from fastapi import APIRouter, HTTPException, Request
from fastapi.responses import FileResponse, JSONResponse


router = APIRouter()

REPOSITORY = "b8vipvip/qianniu-ai-bot"
GITHUB_LATEST_RELEASE_API = f"https://api.github.com/repos/{REPOSITORY}/releases/latest"
PACKAGE_ASSET_NAME = "qianniu-bot-x64.zip"
MANIFEST_ASSET_NAME = "update.json"
TAG_PATTERN = re.compile(r"^bot-v\d+\.\d+\.\d+(?:\.\d+)?$", re.IGNORECASE)
SHA256_PATTERN = re.compile(r"^[0-9a-f]{64}$", re.IGNORECASE)

DATA_DIR = Path(os.getenv("DATA_DIR", "/data")).resolve()
CACHE_ROOT = Path(
    os.getenv("BOT_UPDATE_CACHE_DIR", str(DATA_DIR / "bot-update-cache"))
).resolve()
METADATA_CACHE_PATH = CACHE_ROOT / "latest.json"
METADATA_CACHE_SECONDS = max(
    30, min(3600, int(os.getenv("BOT_UPDATE_METADATA_CACHE_SECONDS", "300")))
)
METADATA_STALE_SECONDS = max(
    METADATA_CACHE_SECONDS,
    min(604800, int(os.getenv("BOT_UPDATE_METADATA_STALE_SECONDS", "86400"))),
)
GITHUB_TIMEOUT_SECONDS = max(
    3, min(60, int(os.getenv("BOT_UPDATE_GITHUB_TIMEOUT_SECONDS", "12")))
)
PACKAGE_TIMEOUT_SECONDS = max(
    30, min(3600, int(os.getenv("BOT_UPDATE_PACKAGE_TIMEOUT_SECONDS", "600")))
)
KEEP_PACKAGE_VERSIONS = max(
    1, min(10, int(os.getenv("BOT_UPDATE_KEEP_PACKAGE_VERSIONS", "3")))
)
PUBLIC_BASE_URL = os.getenv("PUBLIC_BASE_URL", "").strip().rstrip("/")

_METADATA_LOCK = threading.RLock()
_PACKAGE_LOCKS: Dict[str, threading.Lock] = {}
_PACKAGE_LOCKS_GUARD = threading.Lock()
_PACKAGE_JOB_LOCK = threading.RLock()
_PACKAGE_JOBS: Dict[str, Dict[str, Any]] = {}
_PACKAGE_THREADS: Dict[str, threading.Thread] = {}
_METADATA: Optional[Dict[str, Any]] = None
_REFRESH_THREAD: Optional[threading.Thread] = None
_STOP_EVENT = threading.Event()


def _now() -> float:
    return time.time()


def _safe_text(value: Any, limit: int = 500) -> str:
    text = str(value or "").replace("\r", " ").replace("\n", " ").strip()
    return text if len(text) <= limit else text[:limit] + "..."


def _normalize_version(tag_or_version: str) -> str:
    value = str(tag_or_version or "").strip()
    if value.lower().startswith("bot-v"):
        value = value[5:]
    elif value.lower().startswith("v"):
        value = value[1:]
    return value.split("-", 1)[0].strip()


def _validate_metadata(metadata: Dict[str, Any]) -> Dict[str, Any]:
    tag = str(metadata.get("tag") or "").strip()
    version = _normalize_version(str(metadata.get("version") or tag))
    download_url = str(metadata.get("download_url") or "").strip()
    sha256 = str(metadata.get("sha256") or "").strip().lower()
    if not TAG_PATTERN.fullmatch(tag):
        raise RuntimeError("GitHub latest Release 不是受支持的 bot-v* 正式版本")
    if version != _normalize_version(tag):
        raise RuntimeError("更新清单版本与 Release 标签不一致")
    if not download_url.startswith("https://"):
        raise RuntimeError("正式安装包下载地址必须使用 HTTPS")
    if not SHA256_PATTERN.fullmatch(sha256):
        raise RuntimeError("正式更新缺少有效 SHA-256")
    metadata = dict(metadata)
    metadata["tag"] = tag
    metadata["version"] = version
    metadata["download_url"] = download_url
    metadata["sha256"] = sha256
    metadata["size"] = max(0, int(metadata.get("size") or 0))
    metadata["fetched_at_unix"] = float(metadata.get("fetched_at_unix") or _now())
    return metadata


def _github_headers() -> Dict[str, str]:
    return {
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
        "User-Agent": "QianniuAiBot-UpdateCache/1.0",
    }


def _fetch_json(url: str, timeout: int) -> Dict[str, Any]:
    response = curl_requests.get(
        url,
        headers=_github_headers(),
        timeout=timeout,
        allow_redirects=True,
        impersonate="chrome",
    )
    if response.status_code < 200 or response.status_code >= 300:
        raise RuntimeError(f"GitHub 返回 HTTP {response.status_code}")
    payload = response.json()
    if not isinstance(payload, dict):
        raise RuntimeError("GitHub 返回的更新数据格式无效")
    return payload


def _fetch_latest_from_github() -> Dict[str, Any]:
    release = _fetch_json(GITHUB_LATEST_RELEASE_API, GITHUB_TIMEOUT_SECONDS)
    if bool(release.get("draft")) or bool(release.get("prerelease")):
        raise RuntimeError("GitHub latest Release 不是稳定版本")
    tag = str(release.get("tag_name") or "").strip()
    assets = release.get("assets")
    if not isinstance(assets, list):
        assets = []
    package = next((x for x in assets if isinstance(x, dict) and str(x.get("name") or "").lower() == PACKAGE_ASSET_NAME.lower()), None)
    manifest = next((x for x in assets if isinstance(x, dict) and str(x.get("name") or "").lower() == MANIFEST_ASSET_NAME.lower()), None)
    if not isinstance(package, dict) or not isinstance(manifest, dict):
        raise RuntimeError("正式 Release 缺少安装包或 update.json")
    manifest_url = str(manifest.get("browser_download_url") or "").strip()
    if not manifest_url.startswith("https://"):
        raise RuntimeError("update.json 下载地址无效")
    manifest_payload = _fetch_json(manifest_url, GITHUB_TIMEOUT_SECONDS)
    metadata = {
        "version": _normalize_version(tag),
        "tag": tag,
        "name": str(release.get("name") or tag),
        "notes": str(release.get("body") or ""),
        "html_url": str(release.get("html_url") or f"https://github.com/{REPOSITORY}/releases"),
        "download_url": str(package.get("browser_download_url") or ""),
        "manifest_url": manifest_url,
        "sha256": str(manifest_payload.get("sha256") or ""),
        "size": int(package.get("size") or manifest_payload.get("size") or 0),
        "published_at": str(release.get("published_at") or ""),
        "commit": str(manifest_payload.get("commit") or release.get("target_commitish") or ""),
        "source": "github-latest",
        "fetched_at_unix": _now(),
    }
    manifest_version = _normalize_version(str(manifest_payload.get("version") or ""))
    if manifest_version and manifest_version != metadata["version"]:
        raise RuntimeError("update.json 版本与 Release 标签不一致")
    return _validate_metadata(metadata)


def _metadata_tag_dir(tag: str) -> Path:
    if not TAG_PATTERN.fullmatch(tag):
        raise RuntimeError("版本标签格式无效")
    return CACHE_ROOT / tag


def _save_metadata(metadata: Dict[str, Any]) -> None:
    CACHE_ROOT.mkdir(parents=True, exist_ok=True)
    encoded = json.dumps(metadata, ensure_ascii=False, indent=2)
    temp = METADATA_CACHE_PATH.with_suffix(".json.tmp")
    temp.write_text(encoded, encoding="utf-8")
    temp.replace(METADATA_CACHE_PATH)
    tag_dir = _metadata_tag_dir(str(metadata["tag"]))
    tag_dir.mkdir(parents=True, exist_ok=True)
    tag_temp = tag_dir / "metadata.json.tmp"
    tag_temp.write_text(encoded, encoding="utf-8")
    tag_temp.replace(tag_dir / "metadata.json")


def _load_metadata_file(path: Path) -> Optional[Dict[str, Any]]:
    try:
        if not path.is_file():
            return None
        payload = json.loads(path.read_text(encoding="utf-8"))
        if not isinstance(payload, dict):
            return None
        return _validate_metadata(payload)
    except Exception:
        return None


def _load_latest_disk_metadata() -> Optional[Dict[str, Any]]:
    return _load_metadata_file(METADATA_CACHE_PATH)


def _load_tag_metadata(tag: str) -> Optional[Dict[str, Any]]:
    try:
        return _load_metadata_file(_metadata_tag_dir(tag) / "metadata.json")
    except Exception:
        return None


def _is_fresh(metadata: Dict[str, Any]) -> bool:
    fetched = float(metadata.get("fetched_at_unix") or 0)
    return fetched > 0 and _now() - fetched <= METADATA_CACHE_SECONDS


def _is_usable_stale(metadata: Dict[str, Any]) -> bool:
    fetched = float(metadata.get("fetched_at_unix") or 0)
    return fetched > 0 and _now() - fetched <= METADATA_STALE_SECONDS


def refresh_latest_metadata() -> Dict[str, Any]:
    global _METADATA
    with _METADATA_LOCK:
        metadata = _fetch_latest_from_github()
        _save_metadata(metadata)
        _METADATA = metadata
        return dict(metadata)


def get_latest_metadata() -> Dict[str, Any]:
    global _METADATA
    with _METADATA_LOCK:
        if _METADATA is None:
            _METADATA = _load_latest_disk_metadata()
        if _METADATA is not None and _is_fresh(_METADATA):
            return dict(_METADATA)
        stale = dict(_METADATA) if _METADATA is not None else None
        try:
            metadata = _fetch_latest_from_github()
            _save_metadata(metadata)
            _METADATA = metadata
            return dict(metadata)
        except Exception as exc:
            if stale is not None and _is_usable_stale(stale):
                stale["stale"] = True
                stale["refresh_error"] = _safe_text(exc)
                return stale
            raise


def _absolute_mirror_url(request: Request, tag: str) -> str:
    base = PUBLIC_BASE_URL or str(request.base_url).rstrip("/")
    return f"{base}/api/public/v1/bot-update/download/{tag}"


def _public_metadata(metadata: Dict[str, Any], request: Request) -> Dict[str, Any]:
    result = {
        "version": metadata["version"], "tag": metadata["tag"],
        "name": metadata.get("name") or metadata["tag"], "notes": metadata.get("notes") or "",
        "html_url": metadata.get("html_url") or "", "download_url": metadata["download_url"],
        "mirror_url": _absolute_mirror_url(request, metadata["tag"]), "sha256": metadata["sha256"],
        "size": metadata.get("size") or 0, "published_at": metadata.get("published_at") or "",
        "commit": metadata.get("commit") or "", "source": "control-plane-cache",
        "stale": bool(metadata.get("stale")),
        "cache_age_seconds": max(0, int(_now() - float(metadata.get("fetched_at_unix") or _now()))),
    }
    if metadata.get("refresh_error"):
        result["refresh_error"] = _safe_text(metadata["refresh_error"], 240)
    return result


def _package_lock(tag: str) -> threading.Lock:
    with _PACKAGE_LOCKS_GUARD:
        lock = _PACKAGE_LOCKS.get(tag)
        if lock is None:
            lock = threading.Lock()
            _PACKAGE_LOCKS[tag] = lock
        return lock


def _hash_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        while True:
            block = handle.read(1024 * 1024)
            if not block:
                break
            digest.update(block)
    return digest.hexdigest()


def _download_package(download_url: str, destination: Path, expected_sha256: str, expected_size: int) -> None:
    partial = destination.with_suffix(destination.suffix + ".partial")
    partial.unlink(missing_ok=True)
    response = curl_requests.get(download_url, headers={"Accept": "application/octet-stream", "User-Agent": "QianniuAiBot-UpdateMirror/1.0"}, timeout=PACKAGE_TIMEOUT_SECONDS, allow_redirects=True, impersonate="chrome", stream=True)
    if response.status_code < 200 or response.status_code >= 300:
        raise RuntimeError(f"GitHub 安装包返回 HTTP {response.status_code}")
    digest = hashlib.sha256()
    copied = 0
    try:
        with partial.open("wb") as output:
            for chunk in response.iter_content(chunk_size=1024 * 1024):
                if not chunk:
                    continue
                output.write(chunk); digest.update(chunk); copied += len(chunk)
    finally:
        try: response.close()
        except Exception: pass
    actual = digest.hexdigest()
    if actual.lower() != expected_sha256.lower():
        partial.unlink(missing_ok=True); raise RuntimeError("服务端镜像安装包 SHA-256 校验失败")
    if expected_size > 0 and copied != expected_size:
        partial.unlink(missing_ok=True); raise RuntimeError(f"服务端镜像安装包大小不一致：期望 {expected_size}，实际 {copied}")
    partial.replace(destination)


def _cleanup_old_packages() -> None:
    if not CACHE_ROOT.is_dir(): return
    tag_dirs = [path for path in CACHE_ROOT.iterdir() if path.is_dir() and TAG_PATTERN.fullmatch(path.name)]
    tag_dirs.sort(key=lambda path: path.stat().st_mtime, reverse=True)
    for old in tag_dirs[KEEP_PACKAGE_VERSIONS:]: shutil.rmtree(old, ignore_errors=True)


def ensure_cached_package(metadata: Dict[str, Any]) -> Path:
    metadata = _validate_metadata(metadata)
    tag = str(metadata["tag"]); tag_dir = _metadata_tag_dir(tag); tag_dir.mkdir(parents=True, exist_ok=True)
    target = tag_dir / PACKAGE_ASSET_NAME; expected_sha = str(metadata["sha256"])
    if target.is_file() and _hash_file(target).lower() == expected_sha.lower(): return target
    with _package_lock(tag):
        if target.is_file() and _hash_file(target).lower() == expected_sha.lower(): return target
        target.unlink(missing_ok=True)
        _download_package(str(metadata["download_url"]), target, expected_sha, int(metadata.get("size") or 0))
        _cleanup_old_packages(); return target


def _resolve_tag_metadata(tag: str) -> Dict[str, Any]:
    if not TAG_PATTERN.fullmatch(tag or ""):
        raise HTTPException(status_code=404, detail="版本不存在")
    metadata = _load_tag_metadata(tag)
    if metadata is not None: return metadata
    try:
        latest = get_latest_metadata()
    except Exception as exc:
        raise HTTPException(status_code=503, detail="暂时无法取得 Bot 正式版本：" + _safe_text(exc, 200))
    if str(latest.get("tag") or "").lower() != tag.lower():
        raise HTTPException(status_code=404, detail="版本不存在")
    return latest


def _package_target(metadata: Dict[str, Any]) -> Path:
    return _metadata_tag_dir(str(metadata["tag"])) / PACKAGE_ASSET_NAME


def _package_ready(metadata: Dict[str, Any]) -> bool:
    target = _package_target(metadata)
    return target.is_file() and _hash_file(target).lower() == str(metadata["sha256"]).lower()


def _package_job_snapshot(metadata: Dict[str, Any]) -> Dict[str, Any]:
    tag = str(metadata["tag"])
    with _PACKAGE_JOB_LOCK:
        job = dict(_PACKAGE_JOBS.get(tag) or {})
        thread = _PACKAGE_THREADS.get(tag)
    if job.get("ready"):
        return job
    if _package_ready(metadata):
        job.update({"tag": tag, "ready": True, "downloading": False, "error": "", "updated_at_unix": _now()})
        with _PACKAGE_JOB_LOCK: _PACKAGE_JOBS[tag] = dict(job)
        return job
    job.setdefault("tag", tag); job.setdefault("ready", False); job.setdefault("error", "")
    job["downloading"] = bool(thread is not None and thread.is_alive())
    return job


def _package_worker(metadata: Dict[str, Any]) -> None:
    tag = str(metadata["tag"])
    try:
        ensure_cached_package(metadata)
        state = {"tag": tag, "ready": True, "downloading": False, "error": "", "updated_at_unix": _now()}
    except Exception as exc:
        state = {"tag": tag, "ready": False, "downloading": False, "error": _safe_text(exc, 300), "updated_at_unix": _now()}
    with _PACKAGE_JOB_LOCK:
        _PACKAGE_JOBS[tag] = state
        _PACKAGE_THREADS.pop(tag, None)


def start_cached_package(metadata: Dict[str, Any]) -> Dict[str, Any]:
    metadata = _validate_metadata(metadata)
    tag = str(metadata["tag"])
    current = _package_job_snapshot(metadata)
    if current.get("ready") or current.get("downloading"):
        current["started"] = False
        return current
    with _PACKAGE_JOB_LOCK:
        thread = _PACKAGE_THREADS.get(tag)
        if thread is not None and thread.is_alive():
            return {"tag": tag, "ready": False, "downloading": True, "started": False, "error": ""}
        state = {"tag": tag, "ready": False, "downloading": True, "started": True, "error": "", "updated_at_unix": _now()}
        _PACKAGE_JOBS[tag] = dict(state)
        thread = threading.Thread(target=_package_worker, args=(dict(metadata),), name=f"bot-update-package-{tag}", daemon=True)
        _PACKAGE_THREADS[tag] = thread
        thread.start()
        return state


def get_cached_package_status(metadata: Dict[str, Any]) -> Dict[str, Any]:
    return _package_job_snapshot(_validate_metadata(metadata))


def _refresh_loop() -> None:
    while not _STOP_EVENT.is_set():
        try: refresh_latest_metadata()
        except Exception: pass
        _STOP_EVENT.wait(METADATA_CACHE_SECONDS)


def init_bot_update_cache() -> None:
    global _METADATA, _REFRESH_THREAD
    CACHE_ROOT.mkdir(parents=True, exist_ok=True)
    with _METADATA_LOCK:
        if _METADATA is None: _METADATA = _load_latest_disk_metadata()
    if _REFRESH_THREAD is not None and _REFRESH_THREAD.is_alive(): return
    _STOP_EVENT.clear()
    _REFRESH_THREAD = threading.Thread(target=_refresh_loop, name="bot-update-cache-refresh", daemon=True)
    _REFRESH_THREAD.start()


def stop_bot_update_cache() -> None:
    _STOP_EVENT.set()


@router.get("/api/public/v1/bot-update/latest", name="get_bot_update_latest")
def get_bot_update_latest(request: Request) -> JSONResponse:
    try: metadata = get_latest_metadata()
    except Exception as exc: raise HTTPException(status_code=503, detail="暂时无法取得 Bot 正式版本：" + _safe_text(exc, 240))
    return JSONResponse(_public_metadata(metadata, request), headers={"Cache-Control": "public, max-age=60", "X-Bot-Update-Source": "control-plane-cache"})


@router.post("/api/public/v1/bot-update/ensure/{tag}", name="ensure_bot_update_mirror")
def ensure_bot_update_mirror(tag: str, request: Request) -> JSONResponse:
    metadata = _resolve_tag_metadata(tag)
    state = start_cached_package(metadata)
    state["mirror_url"] = _absolute_mirror_url(request, str(metadata["tag"]))
    state["sha256"] = str(metadata["sha256"])
    state["size"] = int(metadata.get("size") or 0)
    return JSONResponse(state, status_code=200 if state.get("ready") else 202, headers={"Cache-Control": "no-store"})


@router.get("/api/public/v1/bot-update/status/{tag}", name="get_bot_update_mirror_status")
def get_bot_update_mirror_status(tag: str, request: Request) -> JSONResponse:
    metadata = _resolve_tag_metadata(tag)
    state = get_cached_package_status(metadata)
    state["mirror_url"] = _absolute_mirror_url(request, str(metadata["tag"]))
    state["sha256"] = str(metadata["sha256"])
    state["size"] = int(metadata.get("size") or 0)
    return JSONResponse(state, headers={"Cache-Control": "no-store"})


@router.get("/api/public/v1/bot-update/download/{tag}", name="download_bot_update_mirror")
def download_bot_update_mirror(tag: str) -> FileResponse:
    metadata = _resolve_tag_metadata(tag)
    try: package = ensure_cached_package(metadata)
    except Exception as exc: raise HTTPException(status_code=502, detail="服务端镜像准备失败：" + _safe_text(exc, 240))
    return FileResponse(path=str(package), filename=PACKAGE_ASSET_NAME, media_type="application/zip", headers={"Cache-Control": "public, max-age=86400, immutable", "X-Content-SHA256": str(metadata["sha256"])})
