from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_internal_english_reasoning_is_detected_before_qianniu_send():
    guard = read("src/Bot/ChromeNs/BuyerReplyOutputGuard.cs")
    send = read("src/Bot/ChromeNs/QNRpa.ReliableSend.cs")

    for marker in [
        "we\\s+need",
        "respond\\s+(?:in\\s+)?chinese",
        "likely\\s+(?:say|reply)",
        "one\\s+sentence",
        "internal\\s+reasoning",
    ]:
        assert marker in guard
    assert "异常英文文本" in guard
    assert "AllowedShortLatinTokenRegex" in guard

    validation = send.index("BuyerReplyOutputGuard.TryNormalizeForBuyer(expected")
    editor_read = send.index("TryGetEditorText(out text)")
    assert validation < editor_read
    assert "已阻止异常AI内容发送给买家" in send
    assert "发送前内容安全检查" in send


def test_manual_agent_fragments_are_combined_and_attached_to_visual_semantics():
    source = read("src/Bot/ChromeNs/ManualVisualReplyLearningService.cs")

    assert "VisionImageCacheService.TryGetRecentReference" in source
    assert "pending.Fragments.Add" in source
    assert 'string.Join("；", parts)' in source
    assert "VisualKnowledgeObservationEntity" in source
    assert "VisualKnowledgeEntryEntity" in source
    assert 'SourceType = "视觉人工即时学习"' in source
    assert 'observation.Status = "已通过人工即时回复学习"' in source
    assert "MergeTags(observation.VisualTags, ExtractManualTags(answer))" in source
    assert "SendDeliveryWatchdog.IsKnownBotAnswer" in source


def test_manual_visual_learning_waits_for_inflight_vision_instead_of_cancelling_it():
    source = read("src/Bot/ChromeNs/ManualVisualReplyLearningService.cs")
    withdrawal = read("src/Bot/ChromeNs/VisionWithdrawalAwarePipeline.cs")

    assert "for (var attempt = 0; attempt < 60; attempt++)" in source
    assert "await Task.Delay(2000)" in source
    assert "LoadObservation(pending)" in source
    assert "Vision.ExecuteAsync(task, CancellationToken.None)" in withdrawal
    assert "人工视觉回复等待两分钟仍未取得图片语义" in source


def test_non_visual_manual_text_learning_remains_session_based_and_human_evidence_only():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    bridge = read("src/Bot/ChromeNs/ConversationSessionLearningRuntimeBridge.cs")

    assert "InactivityMinutes = 5" in service
    assert "SellerQuietSeconds = 30" in service
    assert "ConversationSessionLearningService.ObserveLiveMessage" in bridge
    assert 'suggestion.Confidence < 0.86' in service
    for evidence in [
        '"manual_reply"',
        '"manual_correction"',
        '"withdrawn_bot_then_manual"',
        '"repeated_human_pattern"',
    ]:
        assert evidence in service
    assert "人工最终有效回复优先于Bot旧答案" in service
    assert "缺少可靠人工证据，禁止Bot自我学习" in service


def test_startup_and_legacy_build_include_new_services():
    app = read("src/Bot/App.xaml.cs")
    targets = read("src/Directory.Build.targets")

    assert "ManualVisualReplyLearningService.Initialize();" in app
    assert "BuyerReplyOutputGuard.cs" in targets
    assert "ManualVisualReplyLearningService.cs" in targets
