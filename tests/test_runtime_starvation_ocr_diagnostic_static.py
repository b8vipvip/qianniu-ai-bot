from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(rel):
    return (ROOT / rel).read_text(encoding="utf-8-sig")


def test_server_ocr_reuses_shop_control_plane_credentials():
    text = read("src/Bot/ChromeNs/LocalOcrService.cs")
    assert "ShopControlPlaneConnectionStore" in text
    assert "ResolveRuntimeBySellerNick" in text
    assert 'Source = "shop-control-plane"' in text
    assert "AiEndpointStore.GetEnabledEndpoints" in text  # compatibility fallback only
    assert "LocalOcrWorker.exe" not in text


def test_ocr_first_passes_seller_scope():
    text = read("src/Bot/ChromeNs/OcrFirstKnowledgeDecisionService.cs")
    assert "task.SellerNick," in text
    assert "LocalOcrService.TryRecognizeAsync" in text


def test_runtime_reserves_threadpool_capacity_for_deadline_continuations():
    text = read("src/Bot/App.xaml.cs")
    assert "ThreadPool.GetMinThreads" in text
    assert "ThreadPool.SetMinThreads" in text
    assert "运行时线程池抗饥饿保护已启用" in text
    assert "Environment.ProcessorCount * 8" in text


def test_diagnostic_json_paths_do_not_use_first_last_brace_extraction():
    send = read("src/Bot/ChromeNs/SendFailureAnomalyService.cs")
    slow = read("src/Bot/ChromeNs/SlowResponseAnomalyService.cs")
    helper = read("src/Bot/ChromeNs/BuyerReplyOutputGuard.cs")
    assert "StructuredJsonObjectRecovery" in send
    assert "StructuredJsonObjectRecovery" in slow
    assert "ExtractBalancedObjects" in helper
    assert "inString" in helper and "escaped" in helper
    assert "LastIndexOf('}')" not in send
    assert "LastIndexOf('}')" not in slow


def test_handoff_docs_no_longer_present_2026_08_send_todos_as_pending():
    incident = read("docs/QIANNIU_SEND_RELIABILITY_INCIDENTS.md")
    progress = read("docs/QIANNIU_CHAT_AUTOMATION_PROGRESS.md")
    assert "5.1 发送状态机：已完成" in incident
    assert "1.1.1139 新事故" in incident
    assert "50 秒 deadline 实际延迟数分钟" in progress
