from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text(encoding="utf-8")
    if old not in text:
        raise SystemExit(f"expected block not found in {path}: {old[:120]!r}")
    if text.count(old) != 1:
        raise SystemExit(f"expected exactly one block in {path}, got {text.count(old)}")
    p.write_text(text.replace(old, new), encoding="utf-8")


# 1) chat2api is a serialized browser bridge. The previous 20s timeout was based on
# older 6-15s observations, but current production evidence shows ~51s to first token
# and ~53s total. Give one request enough time to finish rather than spawning duplicate
# protocol fallbacks while the browser tab is still working.
replace_once(
    "services/api-control-plane/chat2api_runtime_guard.py",
    '''CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS = max(\n    10,\n    min(45, int(os.getenv("CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS", "20"))),\n)\n''',
    '''CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS = max(\n    20,\n    min(150, int(os.getenv("CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS", "75"))),\n)\nCHAT2API_REALTIME_TOTAL_BUDGET_SECONDS = max(\n    CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS + 5,\n    min(180, int(os.getenv("CHAT2API_REALTIME_TOTAL_BUDGET_SECONDS", "90"))),\n)\n''',
)
replace_once(
    "services/api-control-plane/chat2api_runtime_guard.py",
    '''    The normal realtime router intentionally fails over quickly (6 seconds by default),\n    but chat2api drives a real ChatGPT tab and commonly needs 6-15 seconds before the\n    full non-streaming JSON response is available. Timing it out at 6 seconds leaves the\n    browser request running, so every immediate fallback to the same bridge receives 409\n    "The selected extension is busy with another request".\n\n    Raise the router's realtime cap to the chat2api budget, then preserve the original\n    fast timeout for ordinary providers inside the call wrapper. Background routes keep\n    their existing long timeout unchanged.\n''',
    '''    The normal realtime router intentionally fails over quickly (6 seconds by default),\n    but chat2api drives a serialized real ChatGPT browser tab. Production traces have\n    shown first-token latency above 50 seconds, so the old 20-second bridge budget could\n    time out a request that later completed successfully in the upstream console. The\n    abandoned browser request kept running while protocol fallback created duplicate work.\n\n    Give chat2api one realistic realtime attempt and total budget, preserve the original\n    fast timeout for ordinary providers, and mark a bridge timeout as terminal for that\n    provider so the dispatcher will not immediately submit a second protocol to the same\n    serialized browser bridge. Background routes keep their existing long policy.\n''',
)
replace_once(
    "services/api-control-plane/chat2api_runtime_guard.py",
    '''    base_realtime_timeout = int(runtime_routing_guard.RUNTIME_ATTEMPT_TIMEOUT_SECONDS)\n    runtime_routing_guard.RUNTIME_ATTEMPT_TIMEOUT_SECONDS = max(\n        base_realtime_timeout,\n        CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS,\n    )\n    base_call = runtime_routing_guard.fast_upstream_call\n''',
    '''    base_realtime_timeout = int(runtime_routing_guard.RUNTIME_ATTEMPT_TIMEOUT_SECONDS)\n    base_total_budget = int(runtime_routing_guard.RUNTIME_TOTAL_BUDGET_SECONDS)\n    runtime_routing_guard.RUNTIME_ATTEMPT_TIMEOUT_SECONDS = max(\n        base_realtime_timeout,\n        CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS,\n    )\n    runtime_routing_guard.RUNTIME_TOTAL_BUDGET_SECONDS = max(\n        base_total_budget,\n        CHAT2API_REALTIME_TOTAL_BUDGET_SECONDS,\n    )\n    base_call = runtime_routing_guard.fast_upstream_call\n''',
)
replace_once(
    "services/api-control-plane/chat2api_runtime_guard.py",
    '''        result["attempt_timeout_seconds"] = effective_timeout\n        if is_chat2api:\n            result["upstream_profile"] = "chat2api-browser-bridge"\n        return result\n\n    runtime_routing_guard.fast_upstream_call = guarded_fast_upstream_call\n    control_plane.CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS = CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS\n''',
    '''        result["attempt_timeout_seconds"] = effective_timeout\n        if is_chat2api:\n            result["upstream_profile"] = "chat2api-browser-bridge"\n            error_text = str(result.get("error") or "").lower()\n            if (\n                not result.get("success")\n                and (\n                    "curl: (28)" in error_text\n                    or "timed out" in error_text\n                    or "timeout" in error_text\n                    or "超时" in error_text\n                )\n            ):\n                # The browser bridge is serialized and may still be processing the timed-out\n                # request. Do not immediately submit chat/responses fallback to the same\n                # provider, which creates duplicate work and can produce 409 busy.\n                result["terminal_provider_failure"] = True\n                result["terminal_provider_failure_reason"] = "serialized_browser_bridge_timeout"\n        return result\n\n    runtime_routing_guard.fast_upstream_call = guarded_fast_upstream_call\n    control_plane.CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS = CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS\n    control_plane.CHAT2API_REALTIME_TOTAL_BUDGET_SECONDS = CHAT2API_REALTIME_TOTAL_BUDGET_SECONDS\n''',
)

# 2) Respect terminal-provider failures in the generic dispatcher. Other providers remain
# eligible; only additional routes for the same serialized bridge are suppressed.
replace_once(
    "services/api-control-plane/runtime_routing_guard.py",
    '''    routes = _build_routes(control_plane, requested_model, messages)\n    for provider, model, protocol in routes:\n        remaining = deadline - time.monotonic()\n''',
    '''    routes = _build_routes(control_plane, requested_model, messages)\n    blocked_provider_ids = set()\n    for provider, model, protocol in routes:\n        provider_id = provider.get("id")\n        if provider_id in blocked_provider_ids:\n            continue\n        remaining = deadline - time.monotonic()\n''',
)
replace_once(
    "services/api-control-plane/runtime_routing_guard.py",
    '''        if attempt.get("success"):\n            return {\n                "success": True,\n                "attempt": attempt,\n                "attempts": attempts,\n                "vision": vision,\n                "routing_profile": profile,\n            }\n\n    if routes and time.monotonic() >= deadline - 1:\n''',
    '''        if attempt.get("success"):\n            return {\n                "success": True,\n                "attempt": attempt,\n                "attempts": attempts,\n                "vision": vision,\n                "routing_profile": profile,\n            }\n        if attempt.get("terminal_provider_failure") and provider_id is not None:\n            blocked_provider_ids.add(provider_id)\n\n    if routes and time.monotonic() >= deadline - 1:\n''',
)

# 3) Document the new budgets.
replace_once(
    "services/api-control-plane/.env.example",
    '''# chat2api 通过真实 ChatGPT 浏览器标签页完成请求，首包/完整 JSON 通常明显慢于普通 HTTP 中转站。\n# 该值只提高 chat2api 浏览器桥的实时单次等待上限；其它普通供应商继续使用上面的 6 秒快速失败预算。\n# 过短会出现：控制面先 curl(28) 超时，但浏览器请求仍继续执行，随后同一桥的回退请求全部收到 409 busy。\nCHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS=20\n''',
    '''# chat2api 通过串行的真实 ChatGPT 浏览器标签页完成请求，首包/完整 JSON 可能超过 50 秒。\n# 2026-08 实机已出现首 token 51.1s、总耗时 53.0s；20 秒会导致控制面先 curl(28) 超时，\n# 但浏览器任务随后仍 completed，并诱发同一桥的重复协议请求。这里给单次请求足够时间完成。\n# 普通供应商仍保持上面的 6 秒单次快速失败；chat2api 超时后禁止立即对同一 provider 做协议重试。\nCHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS=75\nCHAT2API_REALTIME_TOTAL_BUDGET_SECONDS=90\n''',
)

# 4) The settings diagnostic must not abort stage 6 when AI fails. It uses a fixed local
# marker for the send-only verification, never forwards the upstream error body to a buyer.
replace_once(
    "src/Bot/ShopScope/ShopApiDiagnosticsService.cs",
    "        private const int RequestTimeoutSeconds = 70;",
    "        private const int RequestTimeoutSeconds = 105;",
)
replace_once(
    "src/Bot/ShopScope/ShopApiDiagnosticsService.cs",
    '''                ["timeout_seconds"] = 45\n''',
    '''                ["timeout_seconds"] = 90\n''',
)
replace_once(
    "src/Bot/ShopScope/ShopApiDiagnosticsService.cs",
    '''                        if (!response.IsSuccessStatusCode)\n                        {\n                            overall.Stop();\n                            return Failure(\n                                "AI回答链路失败",\n                                "阶段1/6 API网络：通过\\n"\n                                + "阶段2/6 Token/ShopKey：通过\\n"\n                                + "阶段3/6 Control Plane 路由：已进入\\n"\n                                + "阶段4/6 上游供应商/模型调用：失败\\n"\n                                + "阶段5/6 AI回复文本解析：未执行\\n"\n                                + "阶段6/6 千牛真实发送：未执行\\n"\n                                + "HTTP：" + (int)response.StatusCode + " " + response.ReasonPhrase + "\\n"\n                                + "AI阶段耗时：" + aiWatch.ElapsedMilliseconds + " ms\\n"\n                                + "响应：" + Safe(body, 1800));\n                        }\n''',
    '''                        if (!response.IsSuccessStatusCode)\n                        {\n                            return await ContinueAfterAiFailureAsync(\n                                shop,\n                                seller,\n                                "阶段1/6 API网络：通过\\n"\n                                + "阶段2/6 Token/ShopKey：通过\\n"\n                                + "阶段3/6 Control Plane 路由：已进入\\n"\n                                + "阶段4/6 上游供应商/模型调用：失败\\n"\n                                + "阶段5/6 AI回复文本解析：未执行\\n"\n                                + "HTTP：" + (int)response.StatusCode + " " + response.ReasonPhrase + "\\n"\n                                + "AI阶段耗时：" + aiWatch.ElapsedMilliseconds + " ms\\n"\n                                + "响应：" + Safe(body, 1800),\n                                overall,\n                                cancellationToken);\n                        }\n''',
)
replace_once(
    "src/Bot/ShopScope/ShopApiDiagnosticsService.cs",
    '''                        if (string.IsNullOrWhiteSpace(answer))\n                        {\n                            overall.Stop();\n                            return Failure(\n                                "AI回答链路失败：未解析到AI文本",\n                                "阶段1/6 API网络：通过\\n"\n                                + "阶段2/6 Token/ShopKey：通过\\n"\n                                + "阶段3/6 Control Plane 路由：通过\\n"\n                                + "阶段4/6 上游供应商/模型调用：通过\\n"\n                                + "阶段5/6 AI回复文本解析：失败\\n"\n                                + "阶段6/6 千牛真实发送：未执行\\n"\n                                + "服务端已返回 HTTP 2xx，但 choices[0].message.content 为空。\\n"\n                                + "响应：" + Safe(body, 1800));\n                        }\n''',
    '''                        if (string.IsNullOrWhiteSpace(answer))\n                        {\n                            return await ContinueAfterAiFailureAsync(\n                                shop,\n                                seller,\n                                "阶段1/6 API网络：通过\\n"\n                                + "阶段2/6 Token/ShopKey：通过\\n"\n                                + "阶段3/6 Control Plane 路由：通过\\n"\n                                + "阶段4/6 上游供应商/模型调用：通过\\n"\n                                + "阶段5/6 AI回复文本解析：失败\\n"\n                                + "服务端已返回 HTTP 2xx，但 choices[0].message.content 为空。\\n"\n                                + "响应：" + Safe(body, 1800),\n                                overall,\n                                cancellationToken);\n                        }\n''',
)
replace_once(
    "src/Bot/ShopScope/ShopApiDiagnosticsService.cs",
    '''            catch (Exception ex)\n            {\n                aiWatch.Stop();\n                overall.Stop();\n                return Failure(\n                    "AI回答链路失败",\n                    "API与鉴权已通过，但调用AI路由或真实发送时发生异常。\\n"\n                    + "AI阶段耗时：" + aiWatch.ElapsedMilliseconds + " ms\\n"\n                    + "错误：" + Safe(ex.Message, 1600));\n            }\n        }\n\n        private static async Task<ShopApiDiagnosticReport> SendDiagnosticAnswerAsync(\n''',
    '''            catch (Exception ex)\n            {\n                aiWatch.Stop();\n                return await ContinueAfterAiFailureAsync(\n                    shop,\n                    seller,\n                    "阶段1/6 API网络：通过\\n"\n                    + "阶段2/6 Token/ShopKey：通过\\n"\n                    + "阶段3/6 Control Plane 路由：已进入\\n"\n                    + "阶段4/6 上游供应商/模型调用：异常\\n"\n                    + "阶段5/6 AI回复文本解析：未执行\\n"\n                    + "AI阶段耗时：" + aiWatch.ElapsedMilliseconds + " ms\\n"\n                    + "错误：" + Safe(ex.Message, 1600),\n                    overall,\n                    cancellationToken);\n            }\n        }\n\n        private static async Task<ShopApiDiagnosticReport> ContinueAfterAiFailureAsync(\n            ShopContext shop,\n            string seller,\n            string aiFailureDetails,\n            Stopwatch overall,\n            CancellationToken cancellationToken)\n        {\n            const string sendOnlyProbe = "AI阶段异常，本条仅用于独立验证千牛真实发送链路。";\n            var sendResult = await SendDiagnosticAnswerAsync(\n                shop,\n                seller,\n                sendOnlyProbe,\n                cancellationToken);\n            if (overall.IsRunning) overall.Stop();\n\n            var details =\n                (aiFailureDetails ?? string.Empty).TrimEnd()\n                + "\\n\\nAI阶段没有产出可用文本；诊断测试未中断。"\n                + "已改用本地固定测试文本继续阶段6，不会把上游错误正文发送给买家。\\n"\n                + sendResult.Details\n                + "\\n链路总耗时：" + overall.ElapsedMilliseconds + " ms";\n\n            return Failure(\n                sendResult.Success\n                    ? "AI回答链路失败，但千牛真实发送链路已独立验证通过"\n                    : "AI回答链路失败，千牛真实发送链路也失败",\n                details);\n        }\n\n        private static async Task<ShopApiDiagnosticReport> SendDiagnosticAnswerAsync(\n''',
)

# 5) Regression tests for long bridge latency, terminal timeout suppression and documented defaults.
test_path = Path("services/api-control-plane/tests/test_chat2api_runtime_guard.py")
test_text = test_path.read_text(encoding="utf-8")
test_text = test_text.replace(
    '''    original_timeout = runtime_routing_guard.RUNTIME_ATTEMPT_TIMEOUT_SECONDS\n    original_call = runtime_routing_guard.fast_upstream_call\n''',
    '''    original_timeout = runtime_routing_guard.RUNTIME_ATTEMPT_TIMEOUT_SECONDS\n    original_total_budget = runtime_routing_guard.RUNTIME_TOTAL_BUDGET_SECONDS\n    original_call = runtime_routing_guard.fast_upstream_call\n''',
    1,
)
test_text = test_text.replace(
    '''        assert runtime_routing_guard.RUNTIME_ATTEMPT_TIMEOUT_SECONDS >= chat2api_runtime_guard.CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS\n''',
    '''        assert runtime_routing_guard.RUNTIME_ATTEMPT_TIMEOUT_SECONDS >= chat2api_runtime_guard.CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS\n        assert runtime_routing_guard.RUNTIME_TOTAL_BUDGET_SECONDS >= chat2api_runtime_guard.CHAT2API_REALTIME_TOTAL_BUDGET_SECONDS\n        assert chat2api_runtime_guard.CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS >= 60\n''',
    1,
)
test_text = test_text.replace(
    '''    finally:\n        runtime_routing_guard.RUNTIME_ATTEMPT_TIMEOUT_SECONDS = original_timeout\n        runtime_routing_guard.fast_upstream_call = original_call\n        runtime_routing_guard._chat2api_runtime_guard_installed = original_installed\n''',
    '''    finally:\n        runtime_routing_guard.RUNTIME_ATTEMPT_TIMEOUT_SECONDS = original_timeout\n        runtime_routing_guard.RUNTIME_TOTAL_BUDGET_SECONDS = original_total_budget\n        runtime_routing_guard.fast_upstream_call = original_call\n        runtime_routing_guard._chat2api_runtime_guard_installed = original_installed\n''',
    1,
)
test_text += '''\n\ndef test_chat2api_timeout_is_terminal_for_same_serialized_provider() -> None:\n    original_timeout = runtime_routing_guard.RUNTIME_ATTEMPT_TIMEOUT_SECONDS\n    original_total_budget = runtime_routing_guard.RUNTIME_TOTAL_BUDGET_SECONDS\n    original_call = runtime_routing_guard.fast_upstream_call\n    original_installed = getattr(runtime_routing_guard, "_chat2api_runtime_guard_installed", False)\n\n    def fake_timeout(*_args, **_kwargs):\n        return {"success": False, "error": "Failed to perform, curl: (28) Operation timed out"}\n\n    try:\n        runtime_routing_guard.RUNTIME_ATTEMPT_TIMEOUT_SECONDS = 6\n        runtime_routing_guard.RUNTIME_TOTAL_BUDGET_SECONDS = 45\n        runtime_routing_guard.fast_upstream_call = fake_timeout\n        runtime_routing_guard._chat2api_runtime_guard_installed = False\n        chat2api_runtime_guard.install(SimpleNamespace())\n\n        result = runtime_routing_guard.fast_upstream_call(\n            SimpleNamespace(),\n            {"id": 4, "name": "chat2api", "base_url": "https://chat2api.mv3.cn"},\n            "gpt-5.5-mini",\n            "responses",\n            [],\n            96,\n            0.1,\n            chat2api_runtime_guard.CHAT2API_REALTIME_ATTEMPT_TIMEOUT_SECONDS,\n        )\n        assert result["terminal_provider_failure"] is True\n        assert result["terminal_provider_failure_reason"] == "serialized_browser_bridge_timeout"\n    finally:\n        runtime_routing_guard.RUNTIME_ATTEMPT_TIMEOUT_SECONDS = original_timeout\n        runtime_routing_guard.RUNTIME_TOTAL_BUDGET_SECONDS = original_total_budget\n        runtime_routing_guard.fast_upstream_call = original_call\n        runtime_routing_guard._chat2api_runtime_guard_installed = original_installed\n'''
test_path.write_text(test_text, encoding="utf-8")

routing_test = Path("services/api-control-plane/tests/test_runtime_routing_guard.py")
routing_text = routing_test.read_text(encoding="utf-8")
routing_text += '''\n\ndef test_terminal_provider_failure_skips_secondary_protocol_for_same_provider(monkeypatch):\n    cp = FakeControlPlane()\n    calls = []\n\n    def fake_call(control_plane, provider, model, protocol, messages, max_tokens, temperature, timeout):\n        calls.append((provider["id"], model, protocol))\n        return {\n            "provider_id": provider["id"],\n            "provider_name": provider["name"],\n            "model": model,\n            "protocol": protocol,\n            "url": "https://example.invalid",\n            "latency_ms": 10,\n            "success": False,\n            "error": "timeout",\n            "terminal_provider_failure": True,\n        }\n\n    monkeypatch.setattr(guard, "fast_upstream_call", fake_call)\n    result = guard.dispatch_chat(\n        cp,\n        "client",\n        "text-default",\n        [{"role": "user", "content": "hi"}],\n        128,\n        0.1,\n        120,\n    )\n\n    assert result["success"] is False\n    assert len(calls) == 1\n    assert calls[0] == (1, "main-model", "chat")\n'''
routing_test.write_text(routing_text, encoding="utf-8")

print("chat2api diagnostic fix applied")
