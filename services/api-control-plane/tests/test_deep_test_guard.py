from __future__ import annotations

from pathlib import Path
import sys


ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

import deep_test_guard


def test_synthetic_vision_benchmark_does_not_use_captcha_prompt() -> None:
    source = (ROOT / "deep_test_guard.py").read_text(encoding="utf-8")
    assert "请读取图片中的验证码" not in source
    assert "TEST_ID=<标识符>;LEFT=BLUE_SQUARE;RIGHT=RED_CIRCLE" in source
    assert "synthetic-ocr-shapes-v2" in source
    assert "未正确识别视觉测试标识符" in source


def test_chat2api_deprecated_and_special_models_are_not_deep_test_targets() -> None:
    assert deep_test_guard._deprecated_chat2api_id("default") is True
    assert deep_test_guard._deprecated_chat2api_id("chatgpt-web") is True
    assert deep_test_guard._deprecated_chat2api_id("gpt-5.6-sol-high") is True
    assert deep_test_guard._deprecated_chat2api_id("gpt-5.6-sol") is False

    ok, reason = deep_test_guard._testability(
        {"id": "gpt-image", "capabilities": ["image-generation", "image-reference"]},
        True,
    )
    assert ok is False and reason == "image-generation-only"

    ok, reason = deep_test_guard._testability(
        {"id": "gpt-live", "capabilities": ["text", "voice-generation", "voice-conversation"]},
        True,
    )
    assert ok is False and reason == "voice-route-not-deep-text-vision"

    ok, reason = deep_test_guard._testability(
        {"id": "gpt-5.5", "capabilities": ["text", "vision", "file-understanding"]},
        True,
    )
    assert ok is True and reason == ""


def test_model_catalog_parser_preserves_capabilities_and_owner() -> None:
    rows = deep_test_guard._model_rows({
        "data": [
            {
                "id": "gpt-5.6-sol",
                "owned_by": "chat2api",
                "capabilities": ["text", "vision", "file-understanding"],
            }
        ]
    })
    assert rows == [{
        "id": "gpt-5.6-sol",
        "owned_by": "chat2api",
        "capabilities": ["text", "vision", "file-understanding"],
    }]
    assert deep_test_guard._is_chat2api_catalog(rows, {}) is True
