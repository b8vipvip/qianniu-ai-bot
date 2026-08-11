from __future__ import annotations

import importlib
import sys
from pathlib import Path

import pytest


MODULE_NAME = "bot_update_prefetch"


def load_module(monkeypatch: pytest.MonkeyPatch):
    monkeypatch.setenv("BOT_UPDATE_PREFETCH_ENABLED", "true")
    monkeypatch.setenv("BOT_UPDATE_PREFETCH_POLL_SECONDS", "60")
    sys.modules.pop(MODULE_NAME, None)
    module = importlib.import_module(MODULE_NAME)
    module._THREAD = None
    module._STOP_EVENT.clear()
    return module


def test_prefetch_once_warms_verified_latest_package(monkeypatch):
    module = load_module(monkeypatch)
    metadata = {"tag": "bot-v1.1.700"}
    expected = Path("/data/bot-update-cache/bot-v1.1.700/qianniu-bot-x64.zip")
    calls = []

    def fake_latest():
        calls.append("metadata")
        return dict(metadata)

    def fake_ensure(value):
        calls.append(("package", value["tag"]))
        return expected

    monkeypatch.setattr(module.bot_update_cache, "get_latest_metadata", fake_latest)
    monkeypatch.setattr(module.bot_update_cache, "ensure_cached_package", fake_ensure)

    assert module._prefetch_once() == expected
    assert calls == ["metadata", ("package", "bot-v1.1.700")]


def test_prefetch_can_be_disabled(monkeypatch):
    monkeypatch.setenv("BOT_UPDATE_PREFETCH_ENABLED", "false")
    sys.modules.pop(MODULE_NAME, None)
    module = importlib.import_module(MODULE_NAME)
    module._THREAD = None
    module._STOP_EVENT.clear()

    module.init_bot_update_prefetch()

    assert module._THREAD is None
