from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_text_ai_budget_finishes_before_absolute_generation_watchdog():
    pipeline = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    agent = read("src/Bot/ChromeNs/BuyerSessionAgent.cs")
    watchdog = read("src/Bot/ChromeNs/BuyerSessionAgentRuntimeBridge.cs")

    assert "internal const int TotalAiBudgetSeconds = 40" in pipeline
    assert "StreamPhaseBudgetSeconds = 20" in pipeline
    assert "StreamAttemptDefaultSeconds = 15" in pipeline
    assert "StreamAttemptMaxSeconds = 18" in pipeline
    assert "StructuredFallbackSeconds = 15" in pipeline
    assert "streamPhaseCts.CancelAfter(TimeSpan.FromSeconds(StreamPhaseBudgetSeconds))" in pipeline
    assert "if (token.IsCancellationRequested) throw;" in pipeline
    assert "提前进入非流式兜底" in pipeline
    assert "SmartReplyRouterService.CanUseOfflineKnowledgeFallback(plan)" in pipeline
    assert "AI失败离线安全兜底" in pipeline
    assert "AbsoluteGenerationAgeSeconds = 55" in agent
    assert "BuyerSessionAgent.AbsoluteGenerationAgeSeconds" in watchdog


def test_bot_web_sync_404_is_deployment_skew_not_five_second_error_storm():
    sync = read("src/Bot/ChromeNs/BotWebAutoReplyRulesSyncService.cs")

    assert "UnsupportedEndpointBackoffMinutes = 15" in sync
    assert "AuthFailureBackoffMinutes = 5" in sync
    assert "TransientBackoffBaseSeconds = 15" in sync
    assert "TransientBackoffMaxSeconds = 300" in sync
    assert "response.StatusCode == HttpStatusCode.NotFound" in sync
    assert "response.StatusCode == HttpStatusCode.MethodNotAllowed" in sync
    assert "保留Windows本地规则并降频探测" in sync
    assert "ScheduleTransientBackoff(state)" in sync
    assert "state.UnsupportedEndpointLogged = false" in sync