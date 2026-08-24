from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_knowledge_manager_has_ai_optimization_runtime_ui():
    ui = read("src/Bot/Knowledge/KnowledgeOptimizationUi.cs")
    service = read("src/Bot/Knowledge/KnowledgeOptimizationService.cs")
    app = read("src/Bot/App.xaml.cs")
    targets = read("src/Directory.Build.targets")

    assert 'Content = "优化问答"' in ui
    assert "KnowledgeOptimizationService.OptimizeAsync" in ui
    assert "仅优化 智能导入 / 历史扫描 / 自动学习 / AI生成" in ui
    assert "knowledge-before-optimize-" in service
    assert "BatchSize = 5" in service
    assert "BackgroundTimeoutSeconds = 300" in service
    assert "明显截断" in service
    assert "不得新增价格" in service
    assert "KnowledgeOptimizationUi.Initialize();" in app
    assert "Knowledge\\KnowledgeOptimizationService.cs" in targets
    assert "Knowledge\\KnowledgeOptimizationUi.cs" in targets


def test_order_auto_reply_uses_segment_sender_but_keeps_legacy_bypass_for_non_preset_answers():
    order = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    app = read("src/Bot/App.xaml.cs")
    targets = read("src/Directory.Build.targets")

    preset_send = "presetSendResult = await SendOrderPresetAnswerAsync(plan, answer);"
    bypass = "KnowledgeLearningService.AllowNextManualSend(plan.Seller, plan.Buyer, answer);"
    legacy_send = "sendOk = await SendTextWithRetryAsync(plan.Buyer, answer, 1);"
    assert preset_send in order
    assert bypass in order
    assert legacy_send in order
    assert order.index(preset_send) < order.index(bypass) < order.index(legacy_send)
    assert "KnowledgeLearningService.AllowNextManualSend(plan.Seller, plan.Buyer, segment);" in order
    assert "下单固定预设分段已登记精确发送豁免" in order
    assert "InstallOrderAutoReplyGuard" not in app
    assert "CtlConversation.OrderAutoReplyGuard.cs" not in targets


def test_streaming_pipeline_hard_cancels_only_invalid_work_and_allows_relevant_parallel_completion():
    pipeline = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    formatter = read("src/Bot/ChromeNs/ReplyDeduplicationService.cs")
    app = read("src/Bot/App.xaml.cs")
    targets = read("src/Directory.Build.targets")

    assert '["stream"] = true' in pipeline
    assert "HttpCompletionOption.ResponseHeadersRead" in pipeline
    assert "if (!lease.IsCurrent)" in pipeline
    assert "generationCts.Cancel();" in pipeline
    assert "人工接管或显式取消" in pipeline
    assert "ParallelReplyRelevanceGate.ShouldSend" in pipeline
    assert "买家后续消息明确纠正/取消了前一问题" in pipeline
    assert "await lease.ConfirmStableAsync(180)" in pipeline
    assert "await qn.SendTextWithRetryAsync" in pipeline
    assert "正在流式生成答案" in pipeline
    assert "MyOpenAI.CallStructuredChat(messages, 220, 0.15, 30, token)" in pipeline
    assert "BuyerStreamingReplyPipeline.Initialize();" in app
    assert "ChromeNs\\BuyerStreamingReplyPipeline.cs" in targets

    assert 'StreamAbortMarker = "[[QN_STREAM_ABORTED]]"' in formatter
    assert "value.IndexOf(StreamAbortMarker" in formatter
    assert "已阻止发送半截答案" in formatter
    assert "return \"错误：AI流式输出中断" in formatter