from __future__ import annotations

from types import SimpleNamespace

import runtime_embedding_guard as guard


class FakeResponse:
    def __init__(self, status_code=200, payload=None, text=""):
        self.status_code = status_code
        self._payload = payload
        self.text = text

    def json(self):
        if self._payload is None:
            raise ValueError("not json")
        return self._payload


class FakeControlPlane:
    def __init__(self, responses):
        self.responses = list(responses)
        self.logged = []

    @staticmethod
    def list_providers(include_secret=False, enabled_only=False):
        return [
            {"id": 1, "name": "relay-a", "base_url": "https://a.invalid", "api_key": "a"},
            {"id": 2, "name": "relay-b", "base_url": "https://b.invalid", "api_key": "b"},
        ]

    @staticmethod
    def get_api_roots(base_url, include_v1_root=True, include_root=True):
        return [base_url.rstrip("/") + "/v1"]

    def do_request(self, method, url, api_key, payload, timeout):
        value = self.responses.pop(0)
        if isinstance(value, Exception):
            return {
                "network_success": False,
                "response": None,
                "elapsed": 0.01,
                "error": str(value),
            }
        return {
            "network_success": True,
            "response": value,
            "elapsed": 0.01,
        }

    @staticmethod
    def safe_json(response):
        try:
            return response.json()
        except Exception:
            return None

    @staticmethod
    def response_error(response):
        return response.text or "error"

    @staticmethod
    def body_preview(response):
        return response.text or ""

    def log_request(self, client_name, kind, requested_model, attempt):
        self.logged.append((client_name, kind, requested_model, attempt["provider_name"], attempt["success"]))


def embedding_payload(values):
    return {
        "object": "list",
        "data": [
            {"object": "embedding", "index": index, "embedding": vector}
            for index, vector in enumerate(values)
        ],
        "model": "embed-model",
    }


def test_normalize_inputs_accepts_string_and_caps_list(monkeypatch):
    monkeypatch.setattr(guard, "EMBEDDING_MAX_INPUTS", 2)
    assert guard._normalize_inputs(" hello ") == ["hello"]
    assert guard._normalize_inputs(["a", " ", "b", "c"]) == ["a", "b"]


def test_valid_embedding_response_succeeds_and_logs():
    cp = FakeControlPlane([
        FakeResponse(payload=embedding_payload([[1.0, 0.0], [0.0, 1.0]]))
    ])
    result = guard.dispatch_embeddings(
        cp,
        "bot-client",
        "embed-model",
        ["query", "knowledge"],
        15,
    )
    assert result["success"] is True
    assert result["attempt"]["provider_name"] == "relay-a"
    assert cp.logged == [("bot-client", "embedding", "embed-model", "relay-a", True)]


def test_first_provider_failure_falls_back_to_second_provider():
    cp = FakeControlPlane([
        FakeResponse(status_code=502, payload={"error": {"message": "bad"}}, text="bad"),
        FakeResponse(payload=embedding_payload([[0.5, 0.5]])),
    ])
    result = guard.dispatch_embeddings(cp, "bot", "embed-model", ["query"], 15)
    assert result["success"] is True
    assert result["attempt"]["provider_name"] == "relay-b"
    assert [row[3] for row in cp.logged] == ["relay-a", "relay-b"]


def test_invalid_embedding_response_is_rejected():
    cp = FakeControlPlane([
        FakeResponse(payload={"data": [{"index": 0, "embedding": []}]}),
        FakeResponse(payload={"data": [{"index": 0, "embedding": []}]}),
    ])
    result = guard.dispatch_embeddings(cp, "bot", "embed-model", ["query"], 15)
    assert result["success"] is False
    assert len(result["attempts"]) == 2
    assert all("embeddings JSON" in attempt["error"] for attempt in result["attempts"])


def test_install_is_idempotent(monkeypatch):
    routes = []

    class FakeApp:
        def post(self, path):
            def decorate(func):
                routes.append((path, func))
                return func
            return decorate

    fake = SimpleNamespace(app=FakeApp(), require_client=lambda: None)
    monkeypatch.setattr(guard, "_INSTALLED", False)
    guard.install(fake)
    guard.install(fake)
    assert [path for path, _ in routes] == ["/v1/embeddings"]
