from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_buyer_message_burst_coordinator_is_in_build():
    targets = text("src/Directory.Build.targets")
    coordinator = text("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    assert "BuyerMessageBurstCoordinator.cs" in targets
    assert "QuietDelayMilliseconds" in coordinator
    assert "ConfirmStableAsync" in coordinator
    assert "买家本轮连续消息" in coordinator


def test_qn_ingests_all_messages_and_invalidates_stale_drafts():
    qn = text("src/Bot/ChromeNs/QN.cs")
    assert "只处理该批次最新一条" not in qn
    assert "_buyerMessageBurstCoordinator.Enqueue" in qn
    assert "旧文本草稿已作废" in qn
    assert "旧视觉草稿已作废" in qn
    assert "ConfirmStableAsync(450)" in qn
    assert "deferLearningUntilDelivered" in text("src/Bot/ChromeNs/MyOpenAI.cs")


def test_media_placeholders_and_human_conversation_rules():
    safety = text("src/Bot/ChromeNs/IncomingMessageSafety.cs")
    prompt = text("src/Bot/ChromeNs/MyOpenAI.cs")
    assert "[图片]" in safety and "[视频]" in safety
    assert "[语音]" in safety and "[表情]" in safety
    assert "不要按每一行逐条作答" in prompt
    assert "后一条明确纠正前文" in prompt
    assert "旧答案应作废" in prompt


def test_wecom_notification_contains_buyer_message_text_only():
    bridge = text("services/api-control-plane/wecom_bridge.py")
    handoff = text("src/Bot/ChromeNs/HandoffNotificationService.cs")
    assert "买家消息：\\n" in bridge
    assert "safe_buyer_message" in bridge
    assert "买家消息：\\n" in handoff
    assert "SafeBuyerMessage" in handoff
