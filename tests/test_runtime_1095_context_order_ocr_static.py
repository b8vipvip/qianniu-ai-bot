from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_deictic_followup_uses_recent_buyer_context_without_global_long_delay():
    source = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    assert "SemanticContinuationWindowSeconds = 15" in source
    assert "买家上一句与当前指代续问，请合并理解为一个完整问题" in source
    assert '"这个", "这款", "这种"' in source
    assert "semantic_continuation_superseded" in source
    assert "QuietDelayMilliseconds" in source
    assert "SemanticContinuationWindowSeconds * 1000" not in source


def test_generation_terminal_state_is_tracked_per_generation():
    agent = read("src/Bot/ChromeNs/BuyerSessionAgent.cs")
    coordinator = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    assert "Dictionary<long, BuyerSessionAgentState> GenerationStates" in agent
    assert "TryGetGenerationState" in agent
    assert "SetGenerationStateLocked(state, generation, next)" in agent
    assert "hasGenerationState && generationState == BuyerSessionAgentState.Generating" in coordinator
    post = coordinator.split("await DispatchScopedAsync(burst, lease);", 1)[1].split("catch (OperationCanceledException)", 1)[0]
    assert "GetSnapshot(burst.SellerNick, burst.BuyerNick)" not in post


def test_fixed_preset_refreshes_only_local_hub_snapshot_before_render():
    service = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    hub = read("src/Bot/ChromeNs/OrderEventHub.cs")
    assert "RefreshLocalSnapshotBeforeRenderAsync" in service
    assert "Task.Delay(120)" in service
    assert "OrderEventHub.RefreshFromCanonical(plan.Snapshot)" in service
    helper = service.split("private static async Task RefreshLocalSnapshotBeforeRenderAsync", 1)[1].split("public static void Complete", 1)[0]
    assert "CallReplyApiAsync" not in helper
    assert "HttpClient" not in helper
    assert "GetRemote" not in helper
    assert "public static OrderSnapshot RefreshFromCanonical" in hub
    assert "Merge(existing.Snapshot, snapshot)" in hub


def test_ocr_release_bundles_onnx_vc_runtime_dependencies_at_publish():
    project = read("tools/LocalOcrWorker/LocalOcrWorker.csproj")
    assert 'AfterTargets="Publish"' in project
    assert 'CopyOnnxVcRuntimeDependencies' in project
    assert 'vcruntime140.dll' in project
    assert 'vcruntime140_1.dll' in project
    assert 'msvcp140.dll' in project
    assert 'msvcp140_1.dll' in project
    assert 'SourceFiles="@(VcRuntimeDependency)"' in project
    assert 'DestinationFolder="$(PublishDir)"' in project
