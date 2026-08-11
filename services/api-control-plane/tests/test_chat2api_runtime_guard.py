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

        chat_provider = {"name": "chat2api", "base_url": "https://chat2api.mv3.cn"}
        normal_provider = {"name": "normal", "base_url": "https://relay.example.test/v1"}

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
        runtime_routing_guard.fast_upstream_call = original_call
        runtime_routing_guard._chat2api_runtime_guard_installed = original_installed
