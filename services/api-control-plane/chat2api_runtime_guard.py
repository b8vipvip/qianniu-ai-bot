from __future__ import annotations

import os
from typing import Any, Dict, Sequence

import runtime_routing_guard


CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS = max(
    20,
    min(150, int(os.getenv("CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS", "75"))),
)
CHAT2API_REALTIME_TOTAL_BUDGET_SECONDS = max(
    CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS + 5,
    min(180, int(os.getenv("CHAT2API_REALTIME_TOTAL_BUDGET_SECONDS", "90"))),
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
    but chat2api drives a serialized real ChatGPT browser tab. Production traces have
    shown first-token latency above 50 seconds, so the old 20-second bridge budget could
    time out a request that later completed successfully in the upstream console. The
    abandoned browser request kept running while protocol fallback created duplicate work.

    Give chat2api one realistic realtime attempt and total budget, preserve the original
    fast timeout for ordinary providers, and mark a bridge timeout as terminal for that
    provider so the dispatcher will not immediately submit a second protocol to the same
    serialized browser bridge. Background routes keep their existing long policy.
    """

    if getattr(runtime_routing_guard, "_chat2api_runtime_guard_installed", False):
        return
    runtime_routing_guard._chat2api_runtime_guard_installed = True

    base_realtime_timeout = int(runtime_routing_guard.RUNTIME_ATTEMPT_TIMEOUT_SECONDS)
    runtime_routing_guard.RUNTIME_ATTEMPT_TIMEOUT_SECONDS = max(
        base_realtime_timeout,
        CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS,
    )
    base_budget_resolver = getattr(runtime_routing_guard, "_realtime_total_budget_resolver", None)

    def guarded_total_budget(routes: Sequence[Any], default_budget: int) -> int:
        resolved = int(default_budget)
        if callable(base_budget_resolver):
            resolved = max(resolved, int(base_budget_resolver(routes, default_budget)))
        if any(_is_chat2api_provider(provider) for provider, _model, _protocol in routes):
            resolved = max(resolved, CHAT2API_REALTIME_TOTAL_BUDGET_SECONDS)
        return resolved

    runtime_routing_guard._realtime_total_budget_resolver = guarded_total_budget
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
            error_text = str(result.get("error") or "").lower()
            if (
                not result.get("success")
                and (
                    "curl: (28)" in error_text
                    or "timed out" in error_text
                    or "timeout" in error_text
                    or "超时" in error_text
                )
            ):
                # The browser bridge is serialized and may still be processing the timed-out
                # request. Do not immediately submit chat/responses fallback to the same
                # provider, which creates duplicate work and can produce 409 busy.
                result["terminal_provider_failure"] = True
                result["terminal_provider_failure_reason"] = "serialized_browser_bridge_timeout"
        return result

    runtime_routing_guard.fast_upstream_call = guarded_fast_upstream_call
    control_plane.CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS = CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS
    control_plane.CHAT2API_REALTIME_TOTAL_BUDGET_SECONDS = CHAT2API_REALTIME_TOTAL_BUDGET_SECONDS
