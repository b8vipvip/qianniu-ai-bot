from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text(encoding="utf-8")
    if text.count(old) != 1:
        raise SystemExit(f"expected one match in {path}, got {text.count(old)} for {old[:80]!r}")
    p.write_text(text.replace(old, new), encoding="utf-8")


# Keep the 90s total budget scoped to requests that actually have a chat2api route.
# The first patch raised the module global, which could unnecessarily extend ordinary relay
# failure budgets even when chat2api is not configured.
replace_once(
    "services/api-control-plane/chat2api_runtime_guard.py",
    '''    base_realtime_timeout = int(runtime_routing_guard.RUNTIME_ATTEMPT_TIMEOUT_SECONDS)\n    base_total_budget = int(runtime_routing_guard.RUNTIME_TOTAL_BUDGET_SECONDS)\n    runtime_routing_guard.RUNTIME_ATTEMPT_TIMEOUT_SECONDS = max(\n        base_realtime_timeout,\n        CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS,\n    )\n    runtime_routing_guard.RUNTIME_TOTAL_BUDGET_SECONDS = max(\n        base_total_budget,\n        CHAT2API_REALTIME_TOTAL_BUDGET_SECONDS,\n    )\n    base_call = runtime_routing_guard.fast_upstream_call\n''',
    '''    base_realtime_timeout = int(runtime_routing_guard.RUNTIME_ATTEMPT_TIMEOUT_SECONDS)\n    runtime_routing_guard.RUNTIME_ATTEMPT_TIMEOUT_SECONDS = max(\n        base_realtime_timeout,\n        CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS,\n    )\n    base_budget_resolver = getattr(runtime_routing_guard, "_realtime_total_budget_resolver", None)\n\n    def guarded_total_budget(routes: Sequence[Any], default_budget: int) -> int:\n        resolved = int(default_budget)\n        if callable(base_budget_resolver):\n            resolved = max(resolved, int(base_budget_resolver(routes, default_budget)))\n        if any(_is_chat2api_provider(provider) for provider, _model, _protocol in routes):\n            resolved = max(resolved, CHAT2API_REALTIME_TOTAL_BUDGET_SECONDS)\n        return resolved\n\n    runtime_routing_guard._realtime_total_budget_resolver = guarded_total_budget\n    base_call = runtime_routing_guard.fast_upstream_call\n''',
)

replace_once(
    "services/api-control-plane/runtime_routing_guard.py",
    '''    profile, budget_cap, attempt_cap = _routing_policy(max_tokens)\n    requested_budget = max(5, int(timeout or budget_cap))\n    total_budget = min(requested_budget, budget_cap)\n    deadline = time.monotonic() + total_budget\n\n    routes = _build_routes(control_plane, requested_model, messages)\n    blocked_provider_ids = set()\n''',
    '''    profile, budget_cap, attempt_cap = _routing_policy(max_tokens)\n    routes = _build_routes(control_plane, requested_model, messages)\n    if profile == "realtime":\n        budget_resolver = globals().get("_realtime_total_budget_resolver")\n        if callable(budget_resolver):\n            try:\n                budget_cap = max(budget_cap, int(budget_resolver(routes, budget_cap)))\n            except Exception:\n                pass\n    requested_budget = max(5, int(timeout or budget_cap))\n    total_budget = min(requested_budget, budget_cap)\n    deadline = time.monotonic() + total_budget\n\n    blocked_provider_ids = set()\n''',
)

p = Path("services/api-control-plane/tests/test_chat2api_runtime_guard.py")
text = p.read_text(encoding="utf-8")
text = text.replace(
    '''    original_total_budget = runtime_routing_guard.RUNTIME_TOTAL_BUDGET_SECONDS\n    original_call = runtime_routing_guard.fast_upstream_call\n    original_installed = getattr(runtime_routing_guard, "_chat2api_runtime_guard_installed", False)\n''',
    '''    original_total_budget = runtime_routing_guard.RUNTIME_TOTAL_BUDGET_SECONDS\n    original_budget_resolver = getattr(runtime_routing_guard, "_realtime_total_budget_resolver", None)\n    original_call = runtime_routing_guard.fast_upstream_call\n    original_installed = getattr(runtime_routing_guard, "_chat2api_runtime_guard_installed", False)\n''',
)
text = text.replace(
    '''        assert runtime_routing_guard.RUNTIME_ATTEMPT_TIMEOUT_SECONDS >= chat2api_runtime_guard.CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS\n        assert runtime_routing_guard.RUNTIME_TOTAL_BUDGET_SECONDS >= chat2api_runtime_guard.CHAT2API_REALTIME_TOTAL_BUDGET_SECONDS\n        assert chat2api_runtime_guard.CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS >= 60\n\n        chat_provider = {"name": "chat2api", "base_url": "https://chat2api.mv3.cn"}\n        normal_provider = {"name": "normal", "base_url": "https://relay.example.test/v1"}\n''',
    '''        assert runtime_routing_guard.RUNTIME_ATTEMPT_TIMEOUT_SECONDS >= chat2api_runtime_guard.CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS\n        assert runtime_routing_guard.RUNTIME_TOTAL_BUDGET_SECONDS == 45\n        assert chat2api_runtime_guard.CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS >= 60\n\n        chat_provider = {"id": 4, "name": "chat2api", "base_url": "https://chat2api.mv3.cn"}\n        normal_provider = {"id": 5, "name": "normal", "base_url": "https://relay.example.test/v1"}\n        resolver = runtime_routing_guard._realtime_total_budget_resolver\n        assert resolver([(chat_provider, "gpt-5.5-mini", "responses")], 45) >= chat2api_runtime_guard.CHAT2API_REALTIME_TOTAL_BUDGET_SECONDS\n        assert resolver([(normal_provider, "some-model", "chat")], 45) == 45\n''',
    1,
)
text = text.replace(
    '''        runtime_routing_guard.RUNTIME_TOTAL_BUDGET_SECONDS = original_total_budget\n        runtime_routing_guard.fast_upstream_call = original_call\n        runtime_routing_guard._chat2api_runtime_guard_installed = original_installed\n''',
    '''        runtime_routing_guard.RUNTIME_TOTAL_BUDGET_SECONDS = original_total_budget\n        if original_budget_resolver is None:\n            try:\n                delattr(runtime_routing_guard, "_realtime_total_budget_resolver")\n            except AttributeError:\n                pass\n        else:\n            runtime_routing_guard._realtime_total_budget_resolver = original_budget_resolver\n        runtime_routing_guard.fast_upstream_call = original_call\n        runtime_routing_guard._chat2api_runtime_guard_installed = original_installed\n''',
)
p.write_text(text, encoding="utf-8")

# Add a direct dispatcher budget test so ordinary providers keep the 45s cap while a
# resolver can selectively enlarge a realtime request that includes the slow bridge.
p = Path("services/api-control-plane/tests/test_runtime_routing_guard.py")
text = p.read_text(encoding="utf-8")
text += '''\n\ndef test_realtime_budget_resolver_is_route_scoped(monkeypatch):\n    cp = FakeControlPlane()\n    original_resolver = getattr(guard, "_realtime_total_budget_resolver", None)\n    seen = []\n\n    def resolver(routes, default_budget):\n        seen.append((len(routes), default_budget))\n        return 90\n\n    def fake_call(control_plane, provider, model, protocol, messages, max_tokens, temperature, timeout):\n        return {\n            "provider_id": provider["id"],\n            "provider_name": provider["name"],\n            "model": model,\n            "protocol": protocol,\n            "url": "https://example.invalid",\n            "latency_ms": 10,\n            "success": True,\n            "answer": "ok",\n        }\n\n    try:\n        guard._realtime_total_budget_resolver = resolver\n        monkeypatch.setattr(guard, "fast_upstream_call", fake_call)\n        result = guard.dispatch_chat(\n            cp,\n            "client",\n            "text-default",\n            [{"role": "user", "content": "hi"}],\n            128,\n            0.1,\n            90,\n        )\n        assert result["success"] is True\n        assert seen and seen[0][1] == guard.RUNTIME_TOTAL_BUDGET_SECONDS\n    finally:\n        if original_resolver is None:\n            try:\n                delattr(guard, "_realtime_total_budget_resolver")\n            except AttributeError:\n                pass\n        else:\n            guard._realtime_total_budget_resolver = original_resolver\n'''
p.write_text(text, encoding="utf-8")

print("provider-scoped chat2api budget refinement applied")
