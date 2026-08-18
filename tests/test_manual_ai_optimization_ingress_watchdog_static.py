from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
KNOWLEDGE = (ROOT / "src/Bot/Knowledge/KnowledgeCenterWindow.cs").read_text(encoding="utf-8-sig")
SAFETY = (ROOT / "src/Bot/ChromeNs/BulkListManagementUiBridge.cs").read_text(encoding="utf-8-sig")


def test_manual_takeover_generates_compare_only_ai_and_history():
    assert 'Header = "AI优化记录"' in KNOWLEDGE
    assert '"人工接管后的AI优化对比"' in KNOWLEDGE
    assert 'desk.AddConversation(seller, buyer, question,' in KNOWLEDGE
    assert 'false, "人工接管后的AI优化对比"' in KNOWLEDGE
    assert 'accuracy_score' in KNOWLEDGE
    assert 'human_reply_reason' in KNOWLEDGE
    assert 'knowledge_strategy' in KNOWLEDGE
    assert 'ReviewedKnowledgeLearningService.ApplyReviewedKnowledge' in KNOWLEDGE


def test_ingress_watchdog_recovers_active_history_and_order_panel():
    assert 'RuntimeIngressReconciliationBridge' in KNOWLEDGE
    assert 'ReconcileActiveConversationIngressAsync' in KNOWLEDGE
    assert 'im.singlemsg.GetRemoteHisMsg' in KNOWLEDGE
    assert 'runtimePassiveIngressWatchdog' in KNOWLEDGE
    assert 'TryRecoverVisibleOrderPanelForBackgroundProbeAsync' in KNOWLEDGE


def test_log_noise_safety_override_preserves_real_faults():
    assert 'RuntimeLogNoiseSafetyOverride' in SAFETY
    assert '"SendForGetText"' not in SAFETY.split('private static string BuildPattern()', 1)[1]
    assert 'Regex.Escape("\\\"extra\\\":\\\"loop\\\"")' in SAFETY
    assert '设置界面已将“人工客服工作时间与下班回复”迁移' in SAFETY
    assert '设置界面已在构造阶段将“启用转人工规则”' in SAFETY
