from __future__ import annotations

import os
from typing import Any, Dict, Sequence

import runtime_routing_guard


CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS = max(
    10,
    min(45, int(os.getenv("CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS", "20"))),
)


def _is_chat2api_provider(provider: Dict[str, Any]) -> bool:
    name = str(provider.get("name") or "").strip().lower()
    base_url = str(provider.get("base_url") or "").strip().lower()
    return (
        name == "chat2api"
        or name.startswith("chat2api ")
        or name.startswith("chat2api-")
        or "chat2api.mv3.cn" in base_url
    )


def install(control_plane: Any) -> None:
    """Give the serialized ChatGPT browser bridge a realistic realtime timeout.

    The normal realtime router intentionally fails over quickly (6 seconds by default),
    but chat2api drives a real ChatGPT tab and commonly needs 6-15 seconds before the
    full non-streaming JSON response is available. Timing it out at 6 seconds leaves the
    browser request running, so every immediate fallback to the same bridge receives 409
    "The selected extension is busy with another request".

    Raise the router's realtime cap to the chat2api budget, then preserve the original
    fast timeout for ordinary providers inside the call wrapper. Background routes keep
    their existing long timeout unchanged.
    """

    if getattr(runtime_routing_guard, "_chat2api_runtime_guard_installed", False):
        return
    runtime_routing_guard._chat2api_runtime_guard_installed = True

    base_realtime_timeout = int(runtime_routing_guard.RUNTIME_ATTEMPT_TIMEOUT_SECONDS)
    runtime_routing_guard.RUNTIME_ATTEMPT_TIMEOUT_SECONDS = max(
        base_realtime_timeout,
        CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS,
    )
    base_call = runtime_routing_guard.fast_upstream_call

    def guarded_fast_upstream_call(
        control_plane_module: Any,
        provider: Dict[str, Any],
        model: str,
        protocol: str,
        messages: Sequence[Dict[str, Any]],
        max_tokens: int,
        temperature: float,
        timeout: int,
    ) -> Dict[str, Any]:
        is_background = int(max_tokens or 0) >= runtime_routing_guard.BACKGROUND_MIN_MAX_TOKENS
        is_chat2api = _is_chat2api_provider(provider)
        effective_timeout = int(timeout)

        # dispatch_chat now computes realtime attempts against the larger chat2api cap.
        # Ordinary realtime providers retain the original quick-fail timeout, while
        # background work and chat2api both keep the timeout selected by the router.
        if not is_background and not is_chat2api:
            effective_timeout = min(effective_timeout, base_realtime_timeout)

        result = base_call(
            control_plane_module,
            provider,
            model,
            protocol,
            messages,
            max_tokens,
            temperature,
            effective_timeout,
        )
        result["attempt_timeout_seconds"] = effective_timeout
        if is_chat2api:
            result["upstream_profile"] = "chat2api-browser-bridge"
        return result

    runtime_routing_guard.fast_upstream_call = guarded_fast_upstream_call
    control_plane.CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS = CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS
