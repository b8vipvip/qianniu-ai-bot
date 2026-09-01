from pathlib import Path


def read(path):
    return Path(path).read_text(encoding="utf-8-sig")


def write(path, text):
    Path(path).write_text(text, encoding="utf-8")


def replace_once(text, old, new, label):
    if old not in text:
        raise SystemExit("missing patch anchor: " + label)
    return text.replace(old, new, 1)


# New regression: verify no network/API symbols instead of banning a word that also
# appears in an explanatory comment.
path = "tests/test_runtime_1095_context_order_ocr_static.py"
text = read(path)
text = replace_once(
    text,
    '    assert "trade" not in helper.lower()\n    assert "http" not in helper.lower()\n',
    '    assert "CallReplyApiAsync" not in helper\n    assert "HttpClient" not in helper\n    assert "GetRemote" not in helper\n',
    "local snapshot regression assertion",
)
# ONNX Runtime PE import table directly requires MSVCP140_1.dll too.
text = replace_once(
    text,
    '    assert "package\\\\Bin\\\\local-ocr\\\\msvcp140.dll" in workflow\n',
    '    assert "package\\\\Bin\\\\local-ocr\\\\msvcp140.dll" in workflow\n    assert "package\\\\Bin\\\\local-ocr\\\\msvcp140_1.dll" in workflow\n',
    "ocr package regression",
)
write(path, text)


# Existing 1.1.1088 regression used implementation-local variable names. The new
# stronger invariant is exact-generation terminal state, not the old latest snapshot.
path = "tests/test_runtime_send_failure_1088_static.py"
text = read(path)
old = '''def test_failed_generation_is_terminal_after_deterministic_and_ai_paths():
    source = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    deterministic = source.split("deterministicSnapshot", 1)[1].split(
        "private bool HasPendingBuyerMessages", 1
    )[0]
    assert "BuyerSessionAgentState.Failed" in deterministic
    assert "固定规则发送失败后保留Failed终态" in deterministic

    dispatch = source.split("var failed = snapshot != null", 1)[1].split(
        "catch (OperationCanceledException)", 1
    )[0]
    assert "BuyerSessionAgentState.Failed" in dispatch
    failed_branch = dispatch.split("if (failed)", 1)[1].split("else if", 1)[0]
    assert "MarkCompleted" not in failed_branch
    assert "回复管线返回时会话已是Failed" in failed_branch
'''
new = '''def test_failed_generation_is_terminal_after_deterministic_and_ai_paths():
    source = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    agent = read("src/Bot/ChromeNs/BuyerSessionAgent.cs")
    deterministic = source.split("BuyerSessionAgentState deterministicState", 1)[1].split(
        "private void AttachSemanticContinuation", 1
    )[0]
    assert "TryGetGenerationState" in deterministic
    assert "deterministicState == BuyerSessionAgentState.Failed" in deterministic
    assert "固定规则发送失败后保留Failed终态" in deterministic

    dispatch = source.split("BuyerSessionAgentState generationState", 1)[1].split(
        "catch (OperationCanceledException)", 1
    )[0]
    assert "TryGetGenerationState" in dispatch
    assert "generationState == BuyerSessionAgentState.Failed" in dispatch
    failed_branch = dispatch.split("if (failed)", 1)[1].split("else if", 1)[0]
    assert "MarkCompleted" not in failed_branch
    assert "回复管线返回时会话已是Failed" in failed_branch
    assert "Dictionary<long, BuyerSessionAgentState> GenerationStates" in agent
    assert "SetGenerationStateLocked(state, generation, next)" in agent
'''
text = replace_once(text, old, new, "1.1.1088 terminal-state regression")
write(path, text)


# Production package verification must require every direct MSVC dependency used by
# onnxruntime.dll, not merely copy it opportunistically.
path = ".github/workflows/windows-build.yml"
text = read(path)
needle_worker = "            (Join-Path $workerOut 'msvcp140.dll'),\n"
if "(Join-Path $workerOut 'msvcp140_1.dll')," not in text:
    text = replace_once(
        text,
        needle_worker,
        needle_worker + "            (Join-Path $workerOut 'msvcp140_1.dll'),\n",
        "worker msvcp140_1 requirement",
    )
needle_package = "            'package\\Bin\\local-ocr\\msvcp140.dll',\n"
if "'package\\Bin\\local-ocr\\msvcp140_1.dll'," not in text:
    text = replace_once(
        text,
        needle_package,
        needle_package + "            'package\\Bin\\local-ocr\\msvcp140_1.dll',\n",
        "package msvcp140_1 requirement",
    )
write(path, text)
