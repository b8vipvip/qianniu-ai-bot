from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_quality_metrics_are_daily_private_and_bounded():
    code = read("src/Bot/ChromeNs/ReplyQualityMetricsService.cs")
    daily = code.split("internal sealed class ReplyQualityDailyMetric", 1)[1].split(
        "internal sealed class ReplyQualitySummary", 1
    )[0]
    assert "reply-quality-metrics.json" in code
    assert "AddDays(-89)" in code
    assert "samples.Count > 240" in code
    assert "TimeSpan.FromSeconds(30)" in code
    assert "RouteDirect" in daily
    assert "ValidationPass" in daily
    assert "SendSuccess" in daily
    assert "HumanCorrection" in daily
    assert "AnswerLatencyMs" in daily
    for forbidden in ("Buyer", "Question", "AnswerText", "OrderId", "MessageContent"):
        assert forbidden not in daily


def test_quality_center_has_ranges_reports_and_privacy_notice():
    ui = read("src/Bot/Options/ReplyQualityCenterUi.cs")
    assert 'TabHeader = "回复质量中心"' in ui
    assert 'Name = "今天", Days = 1' in ui
    assert 'Name = "最近7天", Days = 7' in ui
    assert 'Name = "最近30天", Days = 30' in ui
    assert 'Name = "最近90天", Days = 90' in ui
    assert "复制质量报告" in ui
    assert "打开数据目录" in ui
    assert "不保存买家名称、聊天内容、答案正文或订单信息" in ui
    # WPF TextBlock does not support Padding; card spacing must use Margin.
    card = ui.split("private static TextBlock Card", 1)[1].split("private static void AddCard", 1)[0]
    assert "Padding =" not in card
    assert "Margin =" in card


def test_response_progress_records_route_answer_latency_and_cancellation():
    code = read("src/Bot/ChromeNs/ResponseProgressTracker.cs")
    assert "ReplyQualityMetricsService.RecordRoute" in code
    assert "ReplyQualityMetricsService.RecordAnswerReady" in code
    assert "ReplyQualityMetricsService.RecordCancellation(true)" in code
    assert "ReplyQualityMetricsService.RecordCancellation(false)" in code
    assert "ResolveQualityRoute" in code
    assert 'return "DIRECT_KNOWLEDGE"' in code
    assert 'return "CONTEXTUAL_KNOWLEDGE"' in code
    assert 'return "VISION"' in code


def test_real_send_metrics_use_seller_echo_or_watchdog_timeout():
    code = read("src/Bot/ChromeNs/SendDeliveryWatchdog.cs")
    timeout_remove = code.index("Pending.TryRemove(pending.Id")
    timeout_metric = code.index("ReplyQualityMetricsService.RecordSendResult", timeout_remove)
    confirm = code.index("public static bool ConfirmDelivery")
    confirm_remove = code.index("Pending.TryRemove(pair.Key", confirm)
    confirm_metric = code.index("ReplyQualityMetricsService.RecordSendResult", confirm_remove)
    assert timeout_remove < timeout_metric
    assert confirm < confirm_remove < confirm_metric
    assert "true," in code[confirm_metric:confirm_metric + 160]
    assert "false," in code[timeout_metric:timeout_metric + 500]


def test_validator_and_human_review_feed_quality_metrics():
    dedup = read("src/Bot/ChromeNs/ReplyDeduplicationService.cs")
    review = read("src/Bot/ChromeNs/ReviewedKnowledgeLearningService.cs")
    assert dedup.count("ReplyQualityMetricsService.RecordValidation") >= 3
    assert "ReplyQualityMetricsService.RecordRepair(false)" in dedup
    assert "ReplyQualityMetricsService.RecordRepair(true)" in dedup
    assert "ReplyQualityMetricsService.RecordDuplicateRewrite()" in dedup
    assert "ReplyQualityMetricsService.RecordHumanEvidence(evidenceType)" in review
    assert 'ReplyQualityMetricsService.RecordHumanEvidence("human_confirmed")' in review


def test_quality_center_is_built_initialized_and_flushed():
    targets = read("src/Directory.Build.targets")
    app = read("src/Bot/App.xaml.cs")
    assert "ReplyQualityMetricsService.cs" in targets
    assert "ReplyQualityCenterUi.cs" in targets
    assert "ReplyQualityCenterUi.Initialize()" in app
    assert "ReplyQualityMetricsService.Flush()" in app
