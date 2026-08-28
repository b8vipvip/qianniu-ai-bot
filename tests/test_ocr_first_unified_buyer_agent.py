from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_ocr_first_runs_before_vision_provider_selection():
    props = read("src/Directory.Build.props")
    vision = read("src/Bot/ChromeNs/VisionRequestService.cs")
    local = read("src/Bot/ChromeNs/OcrFirstKnowledgeDecisionService.cs")

    assert "OcrFirstKnowledgeDecisionService.cs" in props
    assert "OcrFirstKnowledgeDecisionService.TryResolveAsync" in vision
    assert vision.index("OcrFirstKnowledgeDecisionService.TryResolveAsync") < vision.index("GetVisionEnabledEndpoints")
    assert "DirectKnowledgeMinOcrConfidence = 0.88" in local
    assert "KnowledgeEngineV2Service.IsSnapshotReady" in local
    assert "decision.CanDirectReply" in local
    assert 'EndpointName = "local-ocr+knowledge-v2"' in local
    assert "未调用视觉API" in local


def test_buyer_agent_has_shared_ordered_event_ledger_and_stale_human_gate():
    props = read("src/Directory.Build.props")
    agent = read("src/Bot/ChromeNs/BuyerSessionAgent.cs")
    bridge = read("src/Bot/ChromeNs/BuyerSessionAgentRuntimeBridge.cs")

    assert "BuyerSessionAgentRuntimeBridge.cs" in props
    assert "static readonly ConcurrentDictionary<string, SessionState> Sessions" in agent
    assert "BuyerSessionEventKind" in agent
    assert "RecentEvents" in agent
    assert "LastBuyerEventSortValue" in agent
    assert "IsOlderThanLatestBuyerLocked" in agent
    assert "cancelCurrentGeneration" in agent
    assert "StaleAgainstLatestBuyer" in agent

    assert "EvRecieveNewMessage" in bridge
    assert "EvShopRobotReceriveNewMessage" in bridge
    assert "OrderCardParser.TryParse" in bridge
    assert "BuyerProductCard" in bridge
    assert "BuyerImage" in bridge
    assert "BuyerWithdrawal" in bridge
    assert "BuyerSystem" in bridge
    assert "SellerBotEcho" in bridge
    assert "SellerHumanReply" in bridge
    assert "result.CancelledCurrentGeneration" in bridge
    assert "result.StaleAgainstLatestBuyer" in bridge


def test_agent_keeps_generation_boundary_separate_from_raw_events():
    agent = read("src/Bot/ChromeNs/BuyerSessionAgent.cs")

    # Only an accepted actionable buyer message advances generation. Raw order/system/manual
    # observations are ledger events and cannot create a new reply generation by themselves.
    observe_start = agent.index("public BuyerSessionAgentObservation ObserveBuyerMessage")
    record_start = agent.index("public BuyerSessionEventResult RecordEvent")
    observe_body = agent[observe_start:record_start]
    record_body = agent[record_start:agent.index("public bool IsCurrent", record_start)]
    assert "state.Generation++" in observe_body
    assert "state.Generation++" not in record_body
