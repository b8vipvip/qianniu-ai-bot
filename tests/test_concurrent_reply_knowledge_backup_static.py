from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_policy_switch_is_shop_scoped_and_neutral_when_disabled():
    source = read("src/Bot/ChromeNs/KnowledgePolicyProfileService.cs")
    assert 'EnabledSettingsKey = "knowledge.policy_reliability_enabled"' in source
    assert 'ShopScopedSettingsStore' in source
    assert 'public static bool IsEnabled(ShopContext shop = null)' in source
    assert 'Reason = "知识策略与可靠度已关闭' in source
    assert 'AllowDirect = true' in source


def test_policy_window_has_explicit_switch_and_full_import_export():
    source = read("src/Bot/Knowledge/KnowledgePolicyProfileUi.cs")
    assert 'Content = "启用知识策略与可靠度"' in source
    assert 'Content = "导入全部"' in source
    assert 'Content = "导出全部"' in source
    assert 'ImportKnowledgePolicies(this)' in source
    assert 'ExportKnowledgePolicies(this)' in source


def test_policy_full_export_contains_reliability_stats():
    source = read("src/Bot/Knowledge/RulePolicyImportExportUi.cs")
    for field in ["directSelectedCount", "contextualSelectedCount", "acceptedCount", "sellerCorrectionCount", "sellerWithdrawCount", "lastEvidenceType"]:
        assert field in source
    assert 'KnowledgePolicyProfileService.ImportCompleteProfile' in source


def test_knowledge_center_has_complete_package_import_export():
    center = read("src/Bot/Knowledge/KnowledgeCenterWindow.cs")
    io = read("src/Bot/Knowledge/RulePolicyImportExportUi.cs")
    assert '导入知识库完整包' in center
    assert '导出知识库完整包' in center
    assert 'KnowledgePackageSchema = "qnbot.knowledge-package"' in io
    assert 'BotFeatureStore.SaveKnowledgeBase(importedKnowledge)' in io
    assert '["policy"] = policy' in io
    assert '["settings"] = settings' in io


def test_dispatched_buyer_work_is_not_cancelled_by_next_buyer_message():
    coordinator = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    pipeline = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    enqueue = coordinator[coordinator.index("public void Enqueue(BuyerMessageBurstItem item)"):coordinator.index("private bool HasPendingBuyerMessages")]
    assert "InvalidateDispatchedAnswerOnArrival" not in enqueue
    assert "HardCancelVersion" in coordinator
    assert "state.HardCancelVersion == capturedHardCancelVersion" in coordinator
    assert "ParallelReplyRelevanceGate.ShouldSend" in pipeline
    assert "允许作为补充答案发送" in pipeline


def test_order_empty_message_center_event_triggers_independent_probe():
    source = read("src/Bot/ChromeNs/OrderPaymentNotificationFallback.cs")
    assert 'ObserveGenericPaymentSignal(qn, "messageCenterNotify空载荷")' in source
    assert '已转入独立订单补扫' in source


def test_demonstrative_question_can_still_use_high_confidence_local_direct():
    source = read("src/Bot/ChromeNs/SmartReplyRouterService.cs")
    assert "IsSelfContainedDemonstrativeQuestion" in source
    assert "selfContainedDemonstrative" in source

# CI trigger: final feature branch contains no temporary workflow or patch helper files.
