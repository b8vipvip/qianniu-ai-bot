from __future__ import annotations

import hashlib
import importlib
import json
import sys
import time
from pathlib import Path

import pytest


MODULE_NAME = "bot_update_cache"


def load_module(monkeypatch: pytest.MonkeyPatch, tmp_path: Path):
    monkeypatch.setenv("DATA_DIR", str(tmp_path))
    monkeypatch.setenv("BOT_UPDATE_CACHE_DIR", str(tmp_path / "update-cache"))
    monkeypatch.setenv("BOT_UPDATE_METADATA_CACHE_SECONDS", "300")
    monkeypatch.setenv("BOT_UPDATE_METADATA_STALE_SECONDS", "86400")
    sys.modules.pop(MODULE_NAME, None)
    module = importlib.import_module(MODULE_NAME)
    module._METADATA = None
    module._REFRESH_THREAD = None
    module._STOP_EVENT.clear()
    return module


def sample_metadata(module, payload: bytes = b"verified-package"):
    return {
        "version": "1.1.600",
        "tag": "bot-v1.1.600",
        "name": "Qianniu AI Bot 1.1.600",
        "notes": "test",
        "html_url": "https://github.com/b8vipvip/qnbot/releases/tag/bot-v1.1.600",
        "download_url": "https://github.com/b8vipvip/qnbot/releases/download/bot-v1.1.600/qianniu-bot-x64.zip",
        "manifest_url": "https://github.com/b8vipvip/qnbot/releases/download/bot-v1.1.600/update.json",
        "sha256": hashlib.sha256(payload).hexdigest(),
        "size": len(payload),
        "published_at": "2026-08-06T11:00:00Z",
        "commit": "a" * 40,
        "source": "github-latest",
        "fetched_at_unix": time.time(),
    }


def test_latest_metadata_is_cached_and_persisted(monkeypatch, tmp_path):
    module = load_module(monkeypatch, tmp_path)
    calls = []
    metadata = sample_metadata(module)

    def fake_fetch():
        calls.append(1)
        return dict(metadata)

    monkeypatch.setattr(module, "_fetch_latest_from_github", fake_fetch)

    first = module.get_latest_metadata()
    second = module.get_latest_metadata()

    assert first["tag"] == "bot-v1.1.600"
    assert second["sha256"] == metadata["sha256"]
    assert len(calls) == 1
    assert module.METADATA_CACHE_PATH.is_file()
    persisted = json.loads(module.METADATA_CACHE_PATH.read_text(encoding="utf-8"))
    assert persisted["commit"] == "a" * 40


def test_stale_cache_is_returned_when_github_temporarily_fails(monkeypatch, tmp_path):
    module = load_module(monkeypatch, tmp_path)
    metadata = sample_metadata(module)
    metadata["fetched_at_unix"] = time.time() - 600
    module._save_metadata(metadata)
    module._METADATA = dict(metadata)

    def fail_fetch():
        raise RuntimeError("temporary github failure")

    monkeypatch.setattr(module, "_fetch_latest_from_github", fail_fetch)
    result = module.get_latest_metadata()

    assert result["tag"] == metadata["tag"]
    assert result["stale"] is True
    assert "temporary github failure" in result["refresh_error"]


def test_service_response_contains_direct_and_mirror_urls(monkeypatch, tmp_path):
    module = load_module(monkeypatch, tmp_path)
    metadata = sample_metadata(module)

    class Request:
        base_url = "https://fallback.example/"

    monkeypatch.setattr(module, "PUBLIC_BASE_URL", "https://bot.example.com")
    result = module._public_metadata(metadata, Request())

    assert result["download_url"].startswith("https://github.com/")
    assert result["mirror_url"] == (
        "https://bot.example.com/api/public/v1/bot-update/download/"
        "bot-v1.1.600"
    )
    assert result["source"] == "control-plane-cache"


def test_package_mirror_downloads_once_and_verifies_sha(monkeypatch, tmp_path):
    module = load_module(monkeypatch, tmp_path)
    payload = b"verified-package"
    metadata = sample_metadata(module, payload)
    calls = []

    def fake_download(url, destination, expected_sha256, expected_size):
        calls.append(url)
        destination.write_bytes(payload)
        assert hashlib.sha256(payload).hexdigest() == expected_sha256
        assert len(payload) == expected_size

    monkeypatch.setattr(module, "_download_package", fake_download)

    first = module.ensure_cached_package(metadata)
    second = module.ensure_cached_package(metadata)

    assert first == second
    assert first.read_bytes() == payload
    assert len(calls) == 1


def test_invalid_tag_and_sha_are_rejected(monkeypatch, tmp_path):
    module = load_module(monkeypatch, tmp_path)
    metadata = sample_metadata(module)
    metadata["tag"] = "../../bad"
    with pytest.raises(RuntimeError):
        module._validate_metadata(metadata)

    metadata = sample_metadata(module)
    metadata["sha256"] = "bad"
    with pytest.raises(RuntimeError):
        module._validate_metadata(metadata)
