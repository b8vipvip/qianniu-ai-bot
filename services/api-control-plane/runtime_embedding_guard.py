from __future__ import annotations

import os
import time
from typing import Any, Dict, List, Sequence

from fastapi import Depends, HTTPException, Request
from fastapi.responses import JSONResponse
from starlette.concurrency import run_in_threadpool


EMBEDDING_TOTAL_BUDGET_SECONDS = max(
    5, min(60, int(os.getenv("EMBEDDING_TOTAL_BUDGET_SECONDS", "15")))
)
EMBEDDING_ATTEMPT_TIMEOUT_SECONDS = max(
    5, min(30, int(os.getenv("EMBEDDING_ATTEMPT_TIMEOUT_SECONDS", "10")))
)
EMBEDDING_MAX_INPUTS = max(1, min(256, int(os.getenv("EMBEDDING_MAX_INPUTS", "64"))))
_INSTALLED = False


def _normalize_inputs(value: Any) -> List[str]:
    if isinstance(value, str):
        items = [value]
    elif isinstance(value, list):
        items = [str(item) for item in value]
    else:
        return []
    output: List[str] = []
    for item in items[:EMBEDDING_MAX_INPUTS]:
        text = (item or "").strip()
        if text:
            output.append(text[:12000])
    return output


def _valid_embedding_response(data: Any, expected_count: int) -> bool:
    if not isinstance(data, dict) or not isinstance(data.get("data"), list):
        return False
    rows = data["data"]
    if len(rows) != expected_count:
        return False
    seen = set()
    for position, row in enumerate(rows):
        if not isinstance(row, dict):
            return False
        embedding = row.get("embedding")
        if not isinstance(embedding, list) or not embedding:
            return False
        try:
            if not all(isinstance(float(value), float) for value in embedding[:8]):
                return False
        except Exception:
            return False
        index = row.get("index", position)
        try:
            index = int(index)
        except Exception:
            return False
        if index < 0 or index >= expected_count or index in seen:
            return False
        seen.add(index)
    return len(seen) == expected_count


def dispatch_embeddings(
    control_plane: Any,
    client_name: str,
    requested_model: str,
    inputs: Sequence[str],
    timeout: int,
) -> Dict[str, Any]:
    attempts: List[Dict[str, Any]] = []
    total_budget = max(5, min(EMBEDDING_TOTAL_BUDGET_SECONDS, int(timeout or EMBEDDING_TOTAL_BUDGET_SECONDS)))
    deadline = time.monotonic() + total_budget
    payload = {
        "model": requested_model,
        "input": list(inputs),
        "encoding_format": "float",
    }

    for provider in control_plane.list_providers(include_secret=True, enabled_only=True):
        remaining = deadline - time.monotonic()
        if remaining < 3:
            break
        roots = control_plane.get_api_roots(provider["base_url"], include_v1_root=True, include_root=True)
        for root in roots:
            remaining = deadline - time.monotonic()
            if remaining < 3:
                break
            url = root.rstrip("/") + "/embeddings"
            attempt_timeout = min(
                EMBEDDING_ATTEMPT_TIMEOUT_SECONDS,
                max(3, int(remaining)),
            )
            result = control_plane.do_request(
                "POST",
                url,
                provider["api_key"],
                payload,
                timeout=attempt_timeout,
            )
            attempt: Dict[str, Any] = {
                "provider_id": provider["id"],
                "provider_name": provider["name"],
                "model": requested_model,
                "protocol": "embeddings",
                "url": url,
                "latency_ms": int(result["elapsed"] * 1000),
                "success": False,
            }
            if not result["network_success"]:
                attempt["error"] = result["error"]
                attempts.append(attempt)
                control_plane.log_request(client_name, "embedding", requested_model, attempt)
                continue

            response = result["response"]
            attempt["status_code"] = response.status_code
            if not (200 <= response.status_code < 300):
                attempt["error"] = control_plane.response_error(response)
                attempts.append(attempt)
                control_plane.log_request(client_name, "embedding", requested_model, attempt)
                continue

            data = control_plane.safe_json(response)
            if not _valid_embedding_response(data, len(inputs)):
                attempt["error"] = "HTTP成功但返回内容不是兼容的 embeddings JSON"
                attempt["response_preview"] = control_plane.body_preview(response)
                attempts.append(attempt)
                control_plane.log_request(client_name, "embedding", requested_model, attempt)
                continue

            attempt["success"] = True
            attempts.append(attempt)
            control_plane.log_request(client_name, "embedding", requested_model, attempt)
            return {
                "success": True,
                "attempt": attempt,
                "attempts": attempts,
                "raw": data,
            }

    return {"success": False, "attempts": attempts}


def install(control_plane: Any) -> None:
    global _INSTALLED
    if _INSTALLED:
        return
    _INSTALLED = True

    @control_plane.app.post("/v1/embeddings")
    async def runtime_embeddings(
        request: Request,
        client: Dict[str, Any] = Depends(control_plane.require_client),
    ) -> JSONResponse:
        payload = await request.json()
        requested_model = str(payload.get("model") or "").strip()
        inputs = _normalize_inputs(payload.get("input"))
        if not requested_model:
            raise HTTPException(status_code=400, detail="model 不能为空；请在 Bot 中配置可用的 Embedding 模型名称")
        if not inputs:
            raise HTTPException(status_code=400, detail="input 不能为空")
        timeout = int(payload.get("timeout_seconds") or EMBEDDING_TOTAL_BUDGET_SECONDS)
        dispatched = await run_in_threadpool(
            dispatch_embeddings,
            control_plane,
            client["name"],
            requested_model,
            inputs,
            max(5, min(60, timeout)),
        )
        if not dispatched["success"]:
            return JSONResponse(
                status_code=502,
                content={
                    "error": {
                        "message": "所有供应商的 Embedding 请求均失败",
                        "type": "embedding_upstream_exhausted",
                        "attempts": dispatched["attempts"],
                    }
                },
            )

        raw = dict(dispatched["raw"])
        attempt = dispatched["attempt"]
        raw.setdefault("object", "list")
        raw.setdefault("model", requested_model)
        raw["qianniu_routing"] = {
            "provider": attempt["provider_name"],
            "protocol": "embeddings",
            "latency_ms": attempt["latency_ms"],
            "fallback_attempts": len(dispatched["attempts"]) - 1,
        }
        return JSONResponse(content=raw)
