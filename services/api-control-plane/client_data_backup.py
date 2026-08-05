from __future__ import annotations

import hashlib
import os
import re
from pathlib import Path
from typing import Any, Dict

from fastapi import APIRouter, Depends, HTTPException, Request
from fastapi.responses import FileResponse

import bot_web_console as core


router = APIRouter()
DATA_DIR = Path(os.getenv("DATA_DIR", "/data")).resolve()
BACKUP_ROOT = (DATA_DIR / "client-data-backups").resolve()
MAX_BACKUP_BYTES = max(
    1024 * 1024,
    int(os.getenv("CLIENT_DATA_BACKUP_MAX_BYTES", str(64 * 1024 * 1024))),
)


def install(control_plane: Any) -> None:
    control_plane.app.include_router(router)


def init_db() -> None:
    BACKUP_ROOT.mkdir(parents=True, exist_ok=True)
    cp = core._cp
    with cp.db() as conn:
        conn.executescript(
            """
            CREATE TABLE IF NOT EXISTS bot_client_data_backups (
                client_id INTEGER PRIMARY KEY,
                revision INTEGER NOT NULL DEFAULT 0,
                content_hash TEXT NOT NULL DEFAULT '',
                size_bytes INTEGER NOT NULL DEFAULT 0,
                file_count INTEGER NOT NULL DEFAULT 0,
                data_bytes INTEGER NOT NULL DEFAULT 0,
                device_name TEXT NOT NULL DEFAULT '',
                app_version TEXT NOT NULL DEFAULT '',
                created_at TEXT,
                updated_at TEXT NOT NULL,
                FOREIGN KEY(client_id) REFERENCES client_tokens(id) ON DELETE CASCADE
            );
            """
        )


def _clean_header(value: str, limit: int = 200) -> str:
    value = re.sub(r"[\x00-\x1f\x7f]+", " ", str(value or "")).strip()
    return value[:limit]


def _backup_path(client_id: int) -> Path:
    directory = BACKUP_ROOT / str(int(client_id))
    directory.mkdir(parents=True, exist_ok=True)
    return directory / "latest.qab"


def _status(client_id: int) -> Dict[str, Any]:
    cp = core._cp
    with cp.db() as conn:
        row = conn.execute(
            """
            SELECT revision,content_hash,size_bytes,file_count,data_bytes,
                   device_name,app_version,created_at,updated_at
            FROM bot_client_data_backups WHERE client_id=?
            """,
            (int(client_id),),
        ).fetchone()
    path = _backup_path(client_id)
    if not row or not path.exists():
        return {
            "exists": False,
            "revision": 0,
            "content_hash": "",
            "size_bytes": 0,
            "file_count": 0,
            "data_bytes": 0,
            "device_name": "",
            "app_version": "",
            "created_at": None,
            "updated_at": None,
            "max_backup_bytes": MAX_BACKUP_BYTES,
        }
    result = dict(row)
    result["exists"] = True
    result["max_backup_bytes"] = MAX_BACKUP_BYTES
    return result


@router.get("/api/runtime/v1/client-data-backup/status")
def runtime_client_data_backup_status(
    client: Dict[str, Any] = Depends(core._runtime_client),
) -> Dict[str, Any]:
    return _status(int(client["id"]))


@router.put("/api/runtime/v1/client-data-backup")
async def runtime_upload_client_data_backup(
    request: Request,
    client: Dict[str, Any] = Depends(core._runtime_client),
) -> Dict[str, Any]:
    content_length = request.headers.get("content-length", "").strip()
    if content_length:
        try:
            if int(content_length) > MAX_BACKUP_BYTES:
                raise HTTPException(status_code=413, detail="云备份文件超过服务端大小限制")
        except ValueError:
            raise HTTPException(status_code=400, detail="Content-Length 无效")

    payload = await request.body()
    if not payload:
        raise HTTPException(status_code=400, detail="云备份文件不能为空")
    if len(payload) > MAX_BACKUP_BYTES:
        raise HTTPException(status_code=413, detail="云备份文件超过服务端大小限制")

    digest = hashlib.sha256(payload).hexdigest()
    expected = request.headers.get("x-backup-sha256", "").strip().lower()
    if expected and expected != digest:
        raise HTTPException(status_code=400, detail="云备份校验值不匹配")

    client_id = int(client["id"])
    target = _backup_path(client_id)
    temp = target.with_suffix(".uploading")
    temp.write_bytes(payload)
    os.replace(str(temp), str(target))

    cp = core._cp
    now = core._now()
    created_at = _clean_header(request.headers.get("x-backup-created-at", ""), 80) or now
    device_name = _clean_header(request.headers.get("x-backup-device", ""), 200)
    app_version = _clean_header(request.headers.get("x-backup-app-version", ""), 100)
    try:
        file_count = max(0, int(request.headers.get("x-backup-file-count", "0") or 0))
        data_bytes = max(0, int(request.headers.get("x-backup-data-bytes", "0") or 0))
    except ValueError:
        raise HTTPException(status_code=400, detail="云备份元数据无效")

    with cp.db() as conn:
        current = conn.execute(
            "SELECT revision FROM bot_client_data_backups WHERE client_id=?",
            (client_id,),
        ).fetchone()
        revision = int(current["revision"] or 0) + 1 if current else 1
        conn.execute(
            """
            INSERT INTO bot_client_data_backups(
                client_id,revision,content_hash,size_bytes,file_count,data_bytes,
                device_name,app_version,created_at,updated_at
            ) VALUES(?,?,?,?,?,?,?,?,?,?)
            ON CONFLICT(client_id) DO UPDATE SET
                revision=excluded.revision,
                content_hash=excluded.content_hash,
                size_bytes=excluded.size_bytes,
                file_count=excluded.file_count,
                data_bytes=excluded.data_bytes,
                device_name=excluded.device_name,
                app_version=excluded.app_version,
                created_at=excluded.created_at,
                updated_at=excluded.updated_at
            """,
            (
                client_id,
                revision,
                digest,
                len(payload),
                file_count,
                data_bytes,
                device_name,
                app_version,
                created_at,
                now,
            ),
        )

    result = _status(client_id)
    result["ok"] = True
    return result


@router.get("/api/runtime/v1/client-data-backup")
def runtime_download_client_data_backup(
    client: Dict[str, Any] = Depends(core._runtime_client),
) -> FileResponse:
    client_id = int(client["id"])
    status = _status(client_id)
    path = _backup_path(client_id)
    if not status.get("exists") or not path.exists():
        raise HTTPException(status_code=404, detail="云端还没有该 Bot 令牌的数据备份")

    headers = {
        "X-Backup-Revision": str(status.get("revision") or 0),
        "X-Backup-Sha256": str(status.get("content_hash") or ""),
        "X-Backup-Size": str(status.get("size_bytes") or 0),
    }
    return FileResponse(
        str(path),
        media_type="application/octet-stream",
        filename="qianniu-bot-client-data.qab",
        headers=headers,
    )
