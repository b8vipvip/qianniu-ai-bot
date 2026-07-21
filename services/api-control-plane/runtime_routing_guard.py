from __future__ import annotations

import os
import time
from typing import Any, Dict, List, Sequence, Tuple


RUNTIME_TOTAL_BUDGET_SECONDS = max(15, min(120, int(os.getenv("RUNTIME_TOTAL_BUDGET_SECONDS", "45"))))
RUNTIME_ATTEMPT_TIMEOUT_SECONDS = max(5, min(20, int(os.getenv("RUNTIME_ATTEMPT_TIMEOUT_SECONDS", "6"))))
RUNTIME_MAX_URLS_PER_ROUTE = max(1, min(3, int(os.getenv("RUNTIME_MAX_URLS_PER_ROUTE", "1"))))


def install(control_plane: Any) -> None:
    """Replace app.dispatch_chat with a bounded failover dispatcher.

    FastAPI route functions resolve the module global ``dispatch_chat`` at request time,
    so installing here keeps the main app file stable while fixing runtime routing.
    """

    def guarded_dispatch_chat(
        client_name: str,
        requested_model: str,
        messages: Sequence[Dict[str, Any]],
        max_tokens: int,
        temperature: float,
        timeout: int,
    ) -> Dict[str, Any]:
        return dispatch_chat(
            control_plane,
            client_name,
            requested_model,
            messages,
            max_tokens,
            temperature,
            timeout,
        )

    control_plane.dispatch_chat = guarded_dispatch_chat


def _payload(control_plane: Any, protocol: str, model: str, messages: Sequence[Dict[str, Any]], max_tokens: int, temperature: float) -> Dict[str, Any]:
    if protocol == "responses":
        payload: Dict[str, Any] = {
            "model": model,
            "input": control_plane.convert_chat_to_responses_input(messages),
            "max_output_tokens": max_tokens,
        }
        if temperature is not None:
            payload["temperature"] = temperature
        return payload
    if protocol == "legacy":
        return {
            "model": model,
            "prompt": control_plane.flatten_prompt(messages),
            "max_tokens": max_tokens,
            "temperature": temperature,
        }
    return {
        "model": model,
        "messages": list(messages),
        "max_tokens": max_tokens,
        "temperature": temperature,
        "stream": False,
    }


def _extract_answer(control_plane: Any, protocol: str, data: Any) -> str:
    if protocol == "responses":
        return control_plane.extract_responses_text(data)
    if protocol == "legacy":
        try:
            return str(data["choices"][0]["text"]).strip()
        except Exception:
            return ""
    return control_plane.extract_chat_text(data)


def fast_upstream_call(
    control_plane: Any,
    provider: Dict[str, Any],
    model: str,
    protocol: str,
    messages: Sequence[Dict[str, Any]],
    max_tokens: int,
    temperature: float,
    timeout: int,
) -> Dict[str, Any]:
    payload = _payload(control_plane, protocol, model, messages, max_tokens, temperature)
    vision = control_plane.messages_have_image(messages)
    urls = control_plane.upstream_url_candidates(provider, model, protocol, vision)[:RUNTIME_MAX_URLS_PER_ROUTE]
    attempts: List[Dict[str, Any]] = []
    final: Dict[str, Any] | None = None

    for url in urls:
        result = control_plane.do_request("POST", url, provider["api_key"], payload, timeout=timeout)
        attempt: Dict[str, Any] = {
            "provider_id": provider["id"],
            "provider_name": provider["name"],
            "model": model,
            "protocol": protocol,
            "url": url,
            "latency_ms": int(result["elapsed"] * 1000),
            "success": False,
        }
        if not result["network_success"]:
            attempt["error"] = result["error"]
            attempts.append(dict(attempt))
            final = attempt
            continue

        response = result["response"]
        attempt["status_code"] = response.status_code
        if not (200 <= response.status_code < 300):
            attempt["error"] = control_plane.response_error(response)
            attempts.append(dict(attempt))
            final = attempt
            continue

        data = control_plane.safe_json(response)
        if data is None:
            attempt["error"] = "HTTP成功但返回内容不是JSON"
            attempt["response_preview"] = control_plane.body_preview(response)
            attempts.append(dict(attempt))
            final = attempt
            continue

        answer = _extract_answer(control_plane, protocol, data)
        if not answer:
            attempt["error"] = "未解析到模型回复文本"
            attempt["response_preview"] = control_plane.safe_text(control_plane.json_text(data), 500)
            attempts.append(dict(attempt))
            final = attempt
            continue

        attempt["success"] = True
        attempt["answer"] = answer
        attempt["raw"] = data
        attempt["url_attempts"] = attempts + [
            {k: v for k, v in attempt.items() if k not in {"raw", "answer", "url_attempts"}}
        ]
        return attempt

    if final is None:
        final = {
            "provider_id": provider["id"],
            "provider_name": provider["name"],
            "model": model,
            "protocol": protocol,
            "url": "",
            "latency_ms": 0,
            "success": False,
            "error": "没有可用的请求地址",
        }
    final["url_attempts"] = attempts
    return final


def _build_routes(control_plane: Any, requested_model: str, messages: Sequence[Dict[str, Any]]) -> List[Tuple[Dict[str, Any], str, str]]:
    vision = control_plane.messages_have_image(messages)
    routes: List[Tuple[Dict[str, Any], str, str]] = []

    for provider in control_plane.list_providers(include_secret=True, enabled_only=True):
        models = control_plane.model_candidates(provider, requested_model, vision)
        protocols_by_model = {
            model: control_plane.protocol_candidates(provider, model, vision)
            for model in models
        }
        max_protocol_count = max([len(x) for x in protocols_by_model.values()] or [0])

        # Round-robin protocols across models: main and backup models all get their preferred
        # protocol chance before one slow model consumes the whole request on secondary protocols.
        for protocol_index in range(max_protocol_count):
            for model in models:
                protocols = protocols_by_model.get(model, [])
                if protocol_index < len(protocols):
                    routes.append((provider, model, protocols[protocol_index]))
    return routes


def dispatch_chat(
    control_plane: Any,
    client_name: str,
    requested_model: str,
    messages: Sequence[Dict[str, Any]],
    max_tokens: int,
    temperature: float,
    timeout: int,
) -> Dict[str, Any]:
    vision = control_plane.messages_have_image(messages)
    attempts: List[Dict[str, Any]] = []
    requested_budget = max(5, int(timeout or RUNTIME_TOTAL_BUDGET_SECONDS))
    total_budget = min(requested_budget, RUNTIME_TOTAL_BUDGET_SECONDS)
    deadline = time.monotonic() + total_budget

    routes = _build_routes(control_plane, requested_model, messages)
    for provider, model, protocol in routes:
        remaining = deadline - time.monotonic()
        if remaining < 5:
            break
        attempt_timeout = min(RUNTIME_ATTEMPT_TIMEOUT_SECONDS, max(5, int(remaining)))
        attempt = fast_upstream_call(
            control_plane,
            provider,
            model,
            protocol,
            messages,
            max_tokens,
            temperature,
            attempt_timeout,
        )
        attempts.append({k: v for k, v in attempt.items() if k != "raw"})
        control_plane.log_request(
            client_name,
            "vision" if vision else "text",
            requested_model,
            attempt,
        )
        if attempt.get("success"):
            return {
                "success": True,
                "attempt": attempt,
                "attempts": attempts,
                "vision": vision,
            }

    if routes and time.monotonic() >= deadline - 1:
        attempts.append(
            {
                "provider_name": "runtime-router",
                "model": requested_model,
                "protocol": "budget",
                "success": False,
                "latency_ms": total_budget * 1000,
                "error": f"运行时路由总预算 {total_budget} 秒已耗尽；未继续等待剩余低优先级候选。",
            }
        )
    return {"success": False, "attempts": attempts, "vision": vision}
