from __future__ import annotations

import io
from pathlib import Path
from types import SimpleNamespace

from PIL import Image
import pytest

import runtime_ocr


def _png_bytes() -> bytes:
    buffer = io.BytesIO()
    Image.new("RGB", (8, 8), "white").save(buffer, format="PNG")
    return buffer.getvalue()


def test_validate_image_rejects_non_image():
    with pytest.raises(ValueError, match="有效图片"):
        runtime_ocr._validate_image(b"not-an-image")


def test_run_ocr_returns_text_and_average_confidence(monkeypatch):
    class FakeEngine:
        def __call__(self, raw):
            assert raw.startswith(b"\x89PNG")
            return SimpleNamespace(txts=[" 第一行 ", "第二行"], scores=[0.8, 1.0])

    monkeypatch.setattr(runtime_ocr, "_get_engine", lambda: FakeEngine())
    result = runtime_ocr._run_ocr(_png_bytes())
    assert result["ok"] is True
    assert result["text"] == "第一行\n第二行"
    assert result["confidence"] == 0.9
    assert result["engine"] == "RapidOCR/ONNXRuntime"


def test_install_is_idempotent(monkeypatch):
    routes = []

    class FakeApp:
        def post(self, path):
            def decorate(func):
                routes.append((path, func))
                return func
            return decorate

    fake = SimpleNamespace(app=FakeApp(), require_client=lambda: {"name": "test"})
    monkeypatch.setattr(runtime_ocr, "_INSTALLED", False)
    runtime_ocr.install(fake)
    runtime_ocr.install(fake)
    assert [path for path, _ in routes] == ["/api/runtime/v1/ocr"]


def test_server_container_includes_runtime_ocr_module():
    dockerfile = (Path(__file__).resolve().parents[1] / "Dockerfile").read_text(encoding="utf-8")
    copy_lines = [line for line in dockerfile.splitlines() if line.startswith("COPY ")]
    assert any("runtime_ocr.py" in line for line in copy_lines)
