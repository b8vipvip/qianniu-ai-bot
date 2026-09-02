from __future__ import annotations

import asyncio
import hashlib
import io
import os
import threading
import time
from typing import Any, Dict, Optional

from fastapi import Depends, HTTPException, Request, status
from starlette.concurrency import run_in_threadpool
from PIL import Image


_MAX_IMAGE_BYTES = max(256 * 1024, int(os.getenv("OCR_MAX_IMAGE_BYTES", str(8 * 1024 * 1024))))
_TIMEOUT_SECONDS = max(1.0, min(30.0, float(os.getenv("OCR_TIMEOUT_SECONDS", "8"))))
_MAX_CONCURRENCY = max(1, min(8, int(os.getenv("OCR_MAX_CONCURRENCY", "2"))))
_MAX_TEXT_CHARS = max(256, min(12000, int(os.getenv("OCR_MAX_TEXT_CHARS", "6000"))))
_ENGINE_LOCK = threading.Lock()
_ENGINE: Optional[Any] = None
_SEMAPHORE: Optional[asyncio.Semaphore] = None
_SEMAPHORE_LOOP: Optional[asyncio.AbstractEventLoop] = None
_INSTALLED = False


def _get_semaphore() -> asyncio.Semaphore:
    global _SEMAPHORE, _SEMAPHORE_LOOP
    loop = asyncio.get_running_loop()
    if _SEMAPHORE is None or _SEMAPHORE_LOOP is not loop:
        _SEMAPHORE = asyncio.Semaphore(_MAX_CONCURRENCY)
        _SEMAPHORE_LOOP = loop
    return _SEMAPHORE


def _get_engine() -> Any:
    global _ENGINE
    if _ENGINE is not None:
        return _ENGINE
    with _ENGINE_LOCK:
        if _ENGINE is None:
            from rapidocr import RapidOCR

            _ENGINE = RapidOCR()
    return _ENGINE


def _validate_image(raw: bytes) -> None:
    try:
        with Image.open(io.BytesIO(raw)) as image:
            image.verify()
    except Exception as exc:
        raise ValueError("上传内容不是有效图片") from exc


def _run_ocr(raw: bytes) -> Dict[str, Any]:
    started = time.perf_counter()
    _validate_image(raw)
    result = _get_engine()(raw)

    texts = list(getattr(result, "txts", None) or [])
    scores = list(getattr(result, "scores", None) or [])
    lines = [str(value).strip() for value in texts if str(value or "").strip()]
    text = "\n".join(lines).strip()
    if len(text) > _MAX_TEXT_CHARS:
        text = text[:_MAX_TEXT_CHARS] + "…"

    numeric_scores = []
    for score in scores:
        try:
            numeric_scores.append(max(0.0, min(1.0, float(score))))
        except Exception:
            continue
    confidence = sum(numeric_scores) / len(numeric_scores) if numeric_scores else 0.0
    return {
        "ok": True,
        "text": text,
        "confidence": round(confidence, 6),
        "elapsedMs": max(0, int((time.perf_counter() - started) * 1000)),
        "engine": "RapidOCR/ONNXRuntime",
    }


def _release_when_done(task: asyncio.Task, semaphore: asyncio.Semaphore) -> None:
    def release(_: asyncio.Task) -> None:
        semaphore.release()

    task.add_done_callback(release)


def install(control_plane: Any) -> None:
    global _INSTALLED
    if _INSTALLED:
        return
    _INSTALLED = True

    app = control_plane.app
    require_client = control_plane.require_client

    @app.post("/api/runtime/v1/ocr")
    async def runtime_ocr(
        request: Request,
        client: Dict[str, Any] = Depends(require_client),
    ) -> Dict[str, Any]:
        content_length = request.headers.get("content-length", "").strip()
        if content_length:
            try:
                if int(content_length) > _MAX_IMAGE_BYTES:
                    raise HTTPException(status_code=status.HTTP_413_REQUEST_ENTITY_TOO_LARGE, detail="OCR图片超过大小限制")
            except ValueError:
                pass

        raw = await request.body()
        if not raw:
            raise HTTPException(status_code=400, detail="OCR图片不能为空")
        if len(raw) > _MAX_IMAGE_BYTES:
            raise HTTPException(status_code=status.HTTP_413_REQUEST_ENTITY_TOO_LARGE, detail="OCR图片超过大小限制")

        supplied_hash = request.headers.get("x-image-sha256", "").strip().lower()
        actual_hash = hashlib.sha256(raw).hexdigest()
        if supplied_hash and supplied_hash != actual_hash:
            raise HTTPException(status_code=400, detail="OCR图片哈希校验失败")

        semaphore = _get_semaphore()
        await semaphore.acquire()
        task = asyncio.create_task(run_in_threadpool(_run_ocr, raw))
        release_immediately = True
        try:
            result = await asyncio.wait_for(asyncio.shield(task), timeout=_TIMEOUT_SECONDS)
            return {
                **result,
                "imageSha256": actual_hash,
                "cacheable": True,
            }
        except asyncio.TimeoutError as exc:
            # Keep the concurrency slot until the underlying inference actually exits.
            # This prevents timed-out native inference threads from accumulating without bound.
            release_immediately = False
            _release_when_done(task, semaphore)
            raise HTTPException(status_code=504, detail="服务端OCR推理超时") from exc
        except ValueError as exc:
            raise HTTPException(status_code=400, detail=str(exc)) from exc
        except HTTPException:
            raise
        except Exception as exc:
            # Never include OCR text or image bytes in logs/errors.
            raise HTTPException(status_code=503, detail="服务端OCR暂不可用") from exc
        finally:
            if release_immediately:
                semaphore.release()
