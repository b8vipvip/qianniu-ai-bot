from __future__ import annotations

from types import SimpleNamespace

import chat2api_runtime_guard
import runtime_routing_guard


def test_chat2api_provider_detection() -> None:
    assert chat2api_runtime_guard._is_chat2api_provider({"name": "chat2api", "base_url": "https://example.test"})
    assert chat2api_runtime_guard._is_chat2api_provider({"name": "relay", "base_url": "https://chat2api.mv3.cn"})
    assert not chat2api_runtime_guard._is_chat2api_provider({"name": "normal-relay", "base_url": "https://relay.example.test/v1"})


def test_chat2api_gets_longer_realtime_timeout_without_slowing_other_relays() -> None:
    original_timeout = runtime_routing_guard.RUNTIME_ATTEMPT_TIMEOUT_SECONDS
    original_total_budget = runtime_routing_guard.RUNTIME_TOTAL_BUDGET_SECONDS
    original_budget_resolver = getattr(runtime_routing_guard, "_realtime_total_budget_resolver", None)
    original_call = runtime_routing_guard.fast_upstream_call
    original_installed = getattr(runtime_routing_guard, "_chat2api_runtime_guard_installed", False)
    seen: list[int] = []

    def fake_call(
        _control_plane,
        _provider,
        _model,
        _protocol,
        _messages,
        _max_tokens,
        _temperature,
        timeout,
    ):
        seen.append(int(timeout))
        return {"success": True}

    try:
        runtime_routing_guard.RUNTIME_ATTEMPT_TIMEOUT_SECONDS = 6
        runtime_routing_guard.fast_upstream_call = fake_call
        runtime_routing_guard._chat2api_runtime_guard_installed = False

        fake_control_plane = SimpleNamespace()
        chat2api_runtime_guard.install(fake_control_plane)

        assert runtime_routing_guard.RUNTIME_ATTEMPT_TIMEOUT_SECONDS >= chat2api_runtime_guard.CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS
        assert runtime_routing_guard.RUNTIME_TOTAL_BUDGET_SECONDS == 45
        assert chat2api_runtime_guard.CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS >= 60

        chat_provider = {"id": 4, "name": "chat2api", "base_url": "https://chat2api.mv3.cn"}
        normal_provider = {"id": 5, "name": "normal", "base_url": "https://relay.example.test/v1"}
        resolver = runtime_routing_guard._realtime_total_budget_resolver
        assert resolver([(chat_provider, "gpt-5.5-mini", "responses")], 45) >= chat2api_runtime_guard.CHAT2API_REALTIME_TOTAL_BUDGET_SECONDS
        assert resolver([(normal_provider, "some-model", "chat")], 45) == 45

        result = runtime_routing_guard.fast_upstream_call(
            fake_control_plane,
            chat_provider,
            "gpt-5.6-sol",
            "responses",
            [],
            96,
            0.1,
            chat2api_runtime_guard.CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS,
        )
        assert seen[-1] == chat2api_runtime_guard.CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS
        assert result["attempt_timeout_seconds"] == chat2api_runtime_guard.CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS
        assert result["upstream_profile"] == "chat2api-browser-bridge"

        result = runtime_routing_guard.fast_upstream_call(
            fake_control_plane,
            normal_provider,
            "some-model",
            "chat",
            [],
            96,
            0.1,
            chat2api_runtime_guard.CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS,
        )
        assert seen[-1] == 6
        assert result["attempt_timeout_seconds"] == 6
        assert "upstream_profile" not in result

        result = runtime_routing_guard.fast_upstream_call(
            fake_control_plane,
            normal_provider,
            "some-model",
            "chat",
            [],
            runtime_routing_guard.BACKGROUND_MIN_MAX_TOKENS,
            0.1,
            90,
        )
        assert seen[-1] == 90
        assert result["attempt_timeout_seconds"] == 90
    finally:
        runtime_routing_guard.RUNTIME_ATTEMPT_TIMEOUT_SECONDS = original_timeout
        runtime_routing_guard.RUNTIME_TOTAL_BUDGET_SECONDS = original_total_budget
        if original_budget_resolver is None:
            try:
                delattr(runtime_routing_guard, "_realtime_total_budget_resolver")
            except AttributeError:
                pass
        else:
            runtime_routing_guard._realtime_total_budget_resolver = original_budget_resolver
        runtime_routing_guard.fast_upstream_call = original_call
        runtime_routing_guard._chat2api_runtime_guard_installed = original_installed


def test_chat2api_timeout_is_terminal_for_same_serialized_provider() -> None:
    original_timeout = runtime_routing_guard.RUNTIME_ATTEMPT_TIMEOUT_SECONDS
    original_total_budget = runtime_routing_guard.RUNTIME_TOTAL_BUDGET_SECONDS
    original_budget_resolver = getattr(runtime_routing_guard, "_realtime_total_budget_resolver", None)
    original_call = runtime_routing_guard.fast_upstream_call
    original_installed = getattr(runtime_routing_guard, "_chat2api_runtime_guard_installed", False)

    def fake_timeout(*_args, **_kwargs):
        return {"success": False, "error": "Failed to perform, curl: (28) Operation timed out"}

    try:
        runtime_routing_guard.RUNTIME_ATTEMPT_TIMEOUT_SECONDS = 6
        runtime_routing_guard.RUNTIME_TOTAL_BUDGET_SECONDS = 45
        runtime_routing_guard.fast_upstream_call = fake_timeout
        runtime_routing_guard._chat2api_runtime_guard_installed = False
        chat2api_runtime_guard.install(SimpleNamespace())

        result = runtime_routing_guard.fast_upstream_call(
            SimpleNamespace(),
            {"id": 4, "name": "chat2api", "base_url": "https://chat2api.mv3.cn"},
            "gpt-5.5-mini",
            "responses",
            [],
            96,
            0.1,
            chat2api_runtime_guard.CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS,
        )
        assert result["terminal_provider_failure"] is True
        assert result["terminal_provider_failure_reason"] == "serialized_browser_bridge_timeout"
    finally:
        runtime_routing_guard.RUNTIME_ATTEMPT_TIMEOUT_SECONDS = original_timeout
        runtime_routing_guard.RUNTIME_TOTAL_BUDGET_SECONDS = original_total_budget
        if original_budget_resolver is None:
            try:
                delattr(runtime_routing_guard, "_realtime_total_budget_resolver")
            except AttributeError:
                pass
        else:
            runtime_routing_guard._realtime_total_budget_resolver = original_budget_resolver
        runtime_routing_guard.fast_upstream_call = original_call
        runtime_routing_guard._chat2api_runtime_guard_installed = original_installed
