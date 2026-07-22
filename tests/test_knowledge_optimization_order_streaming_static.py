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


def test_order_auto_reply_arms_exact_manual_bypass_at_actual_send_point():
    order = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    app = read("src/Bot/App.xaml.cs")
    targets = read("src/Directory.Build.targets")

    bypass = "KnowledgeLearningService.AllowNextManualSend(plan.Seller, plan.Buyer, answer);"
    send = "var sendOk = await SendTextWithRetryAsync(plan.Buyer, answer, 1);"
    assert bypass in order
    assert send in order
    assert order.index(bypass) < order.index(send)
    assert "下单自动回复已登记精确发送豁免" in order
    assert "InstallOrderAutoReplyGuard" not in app
    assert "CtlConversation.OrderAutoReplyGuard.cs" not in targets


def test_streaming_pipeline_cancels_stale_buyer_generation_and_only_sends_final_answer():
    pipeline = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    formatter = read("src/Bot/ChromeNs/ReplyDeduplicationService.cs")
    app = read("src/Bot/App.xaml.cs")
    targets = read("src/Directory.Build.targets")

    assert '["stream"] = true' in pipeline
    assert "HttpCompletionOption.ResponseHeadersRead" in pipeline
    assert "if (!lease.IsCurrent)" in pipeline
    assert "generationCts.Cancel();" in pipeline
    assert "旧AI流已取消" in pipeline
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
