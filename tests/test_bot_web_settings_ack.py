from __future__ import annotations

import importlib.util
import sqlite3
from contextlib import contextmanager
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
MODULE_PATH = ROOT / "services" / "api-control-plane" / "bot_web_settings_ack.py"
BOOTSTRAP_PATH = ROOT / "services" / "api-control-plane" / "bootstrap.py"
DOCKERFILE_PATH = ROOT / "services" / "api-control-plane" / "Dockerfile"


def load_module():
    spec = importlib.util.spec_from_file_location("bot_web_settings_ack_under_test", MODULE_PATH)
    module = importlib.util.module_from_spec(spec)
    assert spec and spec.loader
    spec.loader.exec_module(module)
    return module


class FakeControlPlane:
    def __init__(self, path: Path):
        self.path = path

    @contextmanager
    def db(self):
        conn = sqlite3.connect(str(self.path))
        conn.row_factory = sqlite3.Row
        try:
            yield conn
            conn.commit()
        except Exception:
            conn.rollback()
            raise
        finally:
            conn.close()

    @staticmethod
    def iso_now() -> str:
        return "2026-09-04T09:00:00+00:00"


def test_ack_waits_for_windows_observation_then_advances(tmp_path):
    module = load_module()
    cp = FakeControlPlane(tmp_path / "ack.db")
    module._cp = cp
    module.init_db()

    desired = {
        "auto_reply_enabled": True,
        "message_sync_enabled": True,
        "allow_web_manual_reply": True,
        "sync_interval_seconds": 3,
        "message_retention_days": 7,
    }
    current = {
        "auto_reply_enabled": True,
        "message_sync_enabled": True,
        "allow_web_manual_reply": True,
        "sync_interval_seconds": 3,
    }

    first = module._ack_row(1, desired, current)
    assert first["revision"] == 1
    assert first["applied_revision"] == 1

    changed = dict(desired, sync_interval_seconds=5)
    waiting = module._ack_row(1, changed, current)
    assert waiting["revision"] == 2
    assert waiting["applied_revision"] == 1

    observed = dict(current, sync_interval_seconds=5)
    applied = module._ack_row(1, changed, observed)
    assert applied["revision"] == 2
    assert applied["applied_revision"] == 2


def test_server_only_retention_never_requires_fake_windows_field(tmp_path):
    module = load_module()
    cp = FakeControlPlane(tmp_path / "settings.db")
    module._cp = cp
    module.init_db()

    desired = {
        "auto_reply_enabled": True,
        "message_sync_enabled": True,
        "allow_web_manual_reply": True,
        "sync_interval_seconds": 3,
        "message_retention_days": 14,
    }
    windows_current = {
        "auto_reply_enabled": True,
        "message_sync_enabled": True,
        "allow_web_manual_reply": True,
        "sync_interval_seconds": 3,
    }
    module._original_settings_for = lambda client_id: {
        "desired": dict(desired),
        "current": dict(windows_current),
    }

    settings = module._settings_for_with_ack(7)
    assert settings["current"]["message_retention_days"] == 14
    assert "message_retention_days" not in settings["client_applied_keys"]
    assert settings["server_only_keys"] == ["message_retention_days"]
    assert settings["applied_revision"] == settings["revision"]


def test_bootstrap_installs_ack_after_console_and_initializes_table():
    text = BOOTSTRAP_PATH.read_text(encoding="utf-8-sig")
    assert "import bot_web_settings_ack" in text
    assert "bot_web_console.install(control_plane)\nbot_web_settings_ack.install(control_plane, bot_web_console)" in text
    assert "bot_web_console.init_bot_web_db()\n    bot_web_settings_ack.init_db()" in text


def test_api_control_plane_image_packages_ack_module_imported_by_bootstrap():
    dockerfile = DOCKERFILE_PATH.read_text(encoding="utf-8-sig")
    copy_lines = [line for line in dockerfile.splitlines() if line.startswith("COPY ")]
    assert any("bot_web_settings_ack.py" in line and "bootstrap.py" in line for line in copy_lines)
