from __future__ import annotations

import json
from pathlib import Path
from types import SimpleNamespace

from fastapi import FastAPI

import runtime_streaming_guard as guard


ROOT = Path(__file__).resolve().parents[1]
REPO_ROOT = ROOT.parents[1]


def test_extract_delta_reads_openai_chat_stream_content():
    payload = json.dumps(
        {
            "choices": [
                {
                    "index": 0,
                    "delta": {"content": "你好"},
                    "finish_reason": None,
                }
            ]
        },
        ensure_ascii=False,
    )
    assert guard._extract_delta(payload) == "你好"


def test_extract_delta_ignores_role_only_event():
    payload = json.dumps({"choices": [{"delta": {"role": "assistant"}}]})
    assert guard._extract_delta(payload) == ""


def test_synthetic_chunk_is_valid_openai_sse_data():
    chunk = guard._synthetic_chunk("fallback-model", "完整答案").decode("utf-8")
    assert chunk.startswith("data: ")
    assert chunk.endswith("\n\n")
    body = json.loads(chunk[len("data: ") :].strip())
    assert body["object"] == "chat.completion.chunk"
    assert body["model"] == "fallback-model"
    assert body["choices"][0]["delta"]["content"] == "完整答案"


def test_install_registers_streaming_middleware():
    app = FastAPI()
    before = len(app.user_middleware)
    cp = SimpleNamespace(app=app)
    guard.install(cp)
    assert len(app.user_middleware) == before + 1


def test_streaming_source_commits_only_after_real_text_and_has_nonstream_fallback():
    source = Path(guard.__file__).read_text(encoding="utf-8")
    assert 'if route[2] == "chat"' in source
    assert "if not first_text_seen and delta:" in source
    assert "control_plane.dispatch_chat" in source
    assert '"text/event-stream"' in source
    assert '"X-Accel-Buffering": "no"' in source


def test_bootstrap_installs_streaming_guard_and_ci_packages_module():
    bootstrap = (ROOT / "bootstrap.py").read_text(encoding="utf-8")
    workflow = (REPO_ROOT / ".github" / "workflows" / "api-control-plane-ci.yml").read_text(encoding="utf-8")
    assert "import runtime_streaming_guard" in bootstrap
    assert "runtime_streaming_guard.install(control_plane)" in bootstrap
    assert "services/api-control-plane/runtime_streaming_guard.py" in workflow
