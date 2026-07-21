from __future__ import annotations

from types import SimpleNamespace

import runtime_routing_guard as guard


class FakeControlPlane:
    def __init__(self):
        self.logged = []

    @staticmethod
    def messages_have_image(messages):
        return False

    @staticmethod
    def list_providers(include_secret=False, enabled_only=False):
        return [
            {
                "id": 1,
                "name": "provider-a",
                "api_key": "test",
                "main_text_model": "main-model",
                "backup_text_models": ["backup-model"],
            }
        ]

    @staticmethod
    def model_candidates(provider, requested_model, vision):
        return ["main-model", "backup-model"]

    @staticmethod
    def protocol_candidates(provider, model, vision):
        return ["chat", "responses", "legacy"]

    def log_request(self, client_name, kind, requested_model, attempt):
        self.logged.append((client_name, kind, requested_model, attempt["model"], attempt["protocol"]))


def test_route_order_gives_backup_model_a_chance_before_secondary_protocol():
    cp = FakeControlPlane()
    routes = guard._build_routes(cp, "text-default", [{"role": "user", "content": "hi"}])
    assert [(model, protocol) for _, model, protocol in routes[:4]] == [
        ("main-model", "chat"),
        ("backup-model", "chat"),
        ("main-model", "responses"),
        ("backup-model", "responses"),
    ]


def test_dispatch_rotates_to_backup_model_with_small_per_attempt_timeout(monkeypatch):
    cp = FakeControlPlane()
    calls = []

    def fake_call(control_plane, provider, model, protocol, messages, max_tokens, temperature, timeout):
        calls.append((model, protocol, timeout))
        if model == "backup-model" and protocol == "chat":
            return {
                "provider_id": 1,
                "provider_name": "provider-a",
                "model": model,
                "protocol": protocol,
                "url": "https://example.invalid/v1/chat/completions",
                "latency_ms": 10,
                "success": True,
                "answer": "ok",
            }
        return {
            "provider_id": 1,
            "provider_name": "provider-a",
            "model": model,
            "protocol": protocol,
            "url": "https://example.invalid/v1/chat/completions",
            "latency_ms": 10,
            "success": False,
            "error": "timeout",
        }

    monkeypatch.setattr(guard, "fast_upstream_call", fake_call)
    result = guard.dispatch_chat(
        cp,
        "client",
        "text-default",
        [{"role": "user", "content": "hi"}],
        128,
        0.1,
        120,
    )

    assert result["success"] is True
    assert calls[:2][0][:2] == ("main-model", "chat")
    assert calls[:2][1][:2] == ("backup-model", "chat")
    assert all(timeout <= guard.RUNTIME_ATTEMPT_TIMEOUT_SECONDS for _, _, timeout in calls)
    assert len(calls) == 2


def test_install_replaces_app_dispatcher():
    cp = SimpleNamespace(dispatch_chat=lambda *args, **kwargs: {"old": True})
    guard.install(cp)
    assert callable(cp.dispatch_chat)
    assert cp.dispatch_chat.__name__ == "guarded_dispatch_chat"
