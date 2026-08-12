from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_ai_failure_fallback_uses_50_percent_only_for_authenticated_control_plane_exhaustion():
    client = read("src/Bot/ChromeNs/ReplyDeduplicationService.cs")
    server = read("services/api-control-plane/app.py")

    assert "MinimumFallbackScore = 0.50" in client
    assert "HTTP 502" in client
    assert "upstream_exhausted" in client
    assert 'string.Equals(x.Type, "服务端控制面"' in client
    assert "所有供应商、模型和请求协议均调用失败" in client

    route_pos = server.index('@app.post("/v1/chat/completions")')
    auth_pos = server.index("Depends(require_client)", route_pos)
    dispatch_pos = server.index("dispatch_chat", auth_pos)
    exhausted_pos = server.index('"type": "upstream_exhausted"', dispatch_pos)
    assert route_pos < auth_pos < dispatch_pos < exhausted_pos
    assert "status_code=502" in server[dispatch_pos:exhausted_pos + 400]


def test_normal_knowledge_threshold_is_not_lowered_when_ai_is_healthy():
    normal = read("src/Bot/ChromeNs/KnowledgeLearningService.cs")
    fallback = read("src/Bot/ChromeNs/ReplyDeduplicationService.cs")

    assert "return matched != null && score >= 0.84;" in normal
    assert "score < MinimumFallbackScore" in fallback
    assert "MinimumFallbackScore = 0.50" in fallback


def test_ai_failure_answer_is_replaced_before_production_error_gate_and_is_source_tracked():
    reply = read("src/Bot/ChromeNs/ReplyDeduplicationService.cs")
    qn = read("src/Bot/ChromeNs/QN.cs")

    assert "AiFailureKnowledgeFallbackService.TryResolve" in reply
    assert '"AI异常本地兜底"' in reply
    assert "&& !aiFailureFallbackApplied" in reply
    assert "PreSendAnswerValidator.Validate" in reply

    distinct_pos = qn.index("ReplyDeduplicationService.EnsureDistinct")
    error_gate_pos = qn.index('answer.StartsWith("错误："', distinct_pos)
    send_pos = qn.index("SendTextWithRetryAsync", error_gate_pos)
    assert distinct_pos < error_gate_pos < send_pos


def test_ai_failure_fallback_is_current_shop_scoped_and_blocks_withdrawn_answers():
    source = read("src/Bot/ChromeNs/ReplyDeduplicationService.cs")

    assert "ShopContextLocator.ResolveRuntimeBySellerNick(seller)" in source
    assert "using (ShopSettingsScope.Enter(shop))" in source
    assert "BotFeatureStore.GetKnowledgeBase()" in source
    assert "ConversationContextStore.IsWithdrawnAnswer(seller, buyer, answer)" in source
    assert "最高匹配不足50%" in source
