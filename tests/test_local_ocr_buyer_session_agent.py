from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_local_ocr_service_is_compiled_and_vision_uses_it():
    props = read("src/Directory.Build.props")
    service = read("src/Bot/ChromeNs/LocalOcrService.cs")
    vision = read("src/Bot/ChromeNs/VisionRequestService.cs")

    assert "LocalOcrService.cs" in props
    assert "LocalOcrWorker.exe" in service
    assert "ComputeSha256" in service
    assert "ocr-cache" in service
    assert "No image bytes are uploaded" in service
    assert "LocalOcrService.TryRecognizeAsync(image.LocalCachePath" in vision
    assert "LocalOcrService.BuildPromptEvidence(localOcr)" in vision
    assert "本地OCR预识别，仅作辅助证据" in service


def test_local_ocr_worker_is_multilingual_onnx_and_bundled():
    project = read("tools/LocalOcrWorker/LocalOcrWorker.csproj")
    program = read("tools/LocalOcrWorker/Program.cs")
    workflow = read(".github/workflows/windows-build.yml")

    assert 'PackageReference Include="RapidOcrNet" Version="4.0.2"' in project
    assert "RapidOcrModelSet.PPOCRv6Small" in program
    assert "RapidOcrOptions.PPOCRv6" in program
    assert "--self-contained true" in workflow
    assert "PP-OCRv6_det_small.onnx" in workflow
    assert "PP-OCRv6_rec_small.onnx" in workflow
    assert "ppocrv6_small_dict.txt" in workflow
    assert "090f04abcd9d9a7498bc4ebf677e4cb9bdce1fe4197ddb7e529f1ef44e1ff94f" in workflow
    assert "6f327246b50388f3c176ae304bd95767ea6dc0c9ae92153ef8cbe210b3c14884" in workflow
    assert "package\\Bin\\local-ocr\\LocalOcrWorker.exe" in workflow


def test_buyer_session_agent_keeps_independent_generations_and_retires_merged_ones():
    props = read("src/Directory.Build.props")
    agent = read("src/Bot/ChromeNs/BuyerSessionAgent.cs")
    burst = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")

    assert "BuyerSessionAgent.cs" in props
    assert "state.Generation++" in agent
    assert "previous.Cancel()" not in agent
    assert "ActiveGenerations" in agent
    assert "independentGeneration=True" in agent
    assert "ReusedCoalescingGeneration = false" in agent
    assert "Duplicate = true" in agent
    assert "public void CancelAll" in agent
    assert "Sending = 6" in agent
    assert "Waiting = 7" in agent
    assert "Completed = 8" in agent
    assert "_sessionAgent.ObserveBuyerMessage" in burst
    assert "if (observation.Duplicate)" in burst
    assert "SessionGeneration" in burst
    assert "CompleteMergedAwayGenerations" in burst
    assert "coalesced_into_generation_" in burst
    assert "_sessionAgent.CancelAll(seller, buyer, reason)" in burst
    assert "coalescing_buffer_trimmed" in burst
    assert "_sessionAgent.IsCurrent" in burst
    assert "MarkReady(\"send_barrier_stable\")" in burst

    # Completion is now conditional: if a replyable pipeline returns while still Generating,
    # timeout/error must become Failed instead of being overwritten as Completed.
    assert "returnedWithoutReady && burst.HasReplyableItem" in burst
    assert "MarkFailed(\"reply_pipeline_returned_without_ready\")" in burst
    assert '"reply_pipeline_completed"' in burst
