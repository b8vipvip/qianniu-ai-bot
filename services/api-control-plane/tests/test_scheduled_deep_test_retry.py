import importlib
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pytest


@pytest.fixture()
def modules(tmp_path, monkeypatch):
    monkeypatch.setenv("DATABASE_PATH", str(tmp_path / "test.db"))
    monkeypatch.setenv("DATA_DIR", str(tmp_path))
    monkeypatch.setenv("APP_SECRET", "test-secret")
    monkeypatch.setenv("API_KEY_ENCRYPTION_KEY", "iV7XoPa3z4i44n5gP5gsLt5mFMQbdbGICxVMJ4K7VQk=")
    monkeypatch.setenv("COOKIE_SECURE", "false")
    monkeypatch.setenv("DISABLE_SCHEDULER", "true")
    service_dir = Path(__file__).resolve().parents[1]
    sys.path.insert(0, str(service_dir))
    for name in ("app", "scheduled_deep_test_retry"):
        sys.modules.pop(name, None)
    control_plane = importlib.import_module("app")
    retry = importlib.import_module("scheduled_deep_test_retry")
    control_plane.init_db()
    yield control_plane, retry
    retry._STARTING_PROVIDER_IDS.clear()
    for name in ("app", "scheduled_deep_test_retry"):
        sys.modules.pop(name, None)
    if str(service_dir) in sys.path:
        sys.path.remove(str(service_dir))


def _provider(last_status, *, enabled=True, auto_test_enabled=True):
    return {
        "id": 1,
        "enabled": enabled,
        "auto_test_enabled": auto_test_enabled,
        "last_status": last_status,
    }


def _run(finished_at, *, status="completed", mode="scheduled"):
    return {
        "id": 10,
        "provider_id": 1,
        "mode": mode,
        "status": status,
        "finished_at": finished_at.isoformat(timespec="seconds"),
    }


def test_should_retry_only_after_ten_minutes_of_exact_scheduled_failure(modules):
    _, retry = modules
    now = datetime(2026, 8, 6, 6, 30, tzinfo=timezone.utc)
    failed = _provider(retry.FAILED_TEXT_STATUS)

    assert retry.should_retry(failed, _run(now - timedelta(minutes=10)), now=now)
    assert not retry.should_retry(failed, _run(now - timedelta(minutes=9, seconds=59)), now=now)
    assert not retry.should_retry(failed, _run(now - timedelta(minutes=20), status="running"), now=now)
    assert not retry.should_retry(failed, _run(now - timedelta(minutes=20), mode="deep"), now=now)
    assert not retry.should_retry(_provider("可用：文本模型 1 个，视觉模型 0 个"), _run(now - timedelta(minutes=20)), now=now)
    assert not retry.should_retry(_provider(retry.FAILED_TEXT_STATUS, auto_test_enabled=False), _run(now - timedelta(minutes=20)), now=now)
    assert not retry.should_retry(_provider(retry.FAILED_TEXT_STATUS, enabled=False), _run(now - timedelta(minutes=20)), now=now)


def test_run_once_queues_one_scheduled_retry_with_auto_apply(modules, monkeypatch):
    control_plane, retry = modules
    now = control_plane.iso_now()
    old_finished = (datetime.now(timezone.utc) - timedelta(minutes=11)).isoformat(timespec="seconds")
    with control_plane.db() as conn:
        cursor = conn.execute(
            """
            INSERT INTO providers(
                name,base_url,api_key_cipher,enabled,priority,main_text_model,
                backup_text_models_json,main_vision_model,backup_vision_models_json,
                protocol_order_json,model_capabilities_json,auto_test_enabled,
                auto_test_interval_hours,auto_test_options_json,last_test_at,next_test_at,
                last_status,last_latency_ms,created_at,updated_at
            ) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
            """,
            (
                "恢复测试供应商",
                "https://example.com/v1",
                control_plane.encrypt_secret("test-key"),
                1,
                1,
                "gpt-test",
                "[]",
                "",
                "[]",
                '["responses","chat"]',
                "{}",
                1,
                6,
                '{"chat_text":false}',
                old_finished,
                (datetime.now(timezone.utc) + timedelta(hours=6)).isoformat(timespec="seconds"),
                retry.FAILED_TEXT_STATUS,
                0,
                now,
                now,
            ),
        )
        provider_id = int(cursor.lastrowid)
        conn.execute(
            """
            INSERT INTO test_runs(
                provider_id,mode,status,options_json,result_json,analysis_markdown,
                started_at,finished_at,error,created_at
            ) VALUES(?,?,?,?,?,?,?,?,?,?)
            """,
            (provider_id, "scheduled", "completed", "{}", "{}", "", old_finished, old_finished, None, old_finished),
        )

    started_threads = []

    class FakeThread:
        def __init__(self, *, target, args, daemon, name):
            self.target = target
            self.args = args
            self.daemon = daemon
            self.name = name

        def start(self):
            started_threads.append(self)

    monkeypatch.setattr(retry.threading, "Thread", FakeThread)

    assert retry.run_once(control_plane) == 1
    assert len(started_threads) == 1
    _, queued_provider_id, options, run_id = started_threads[0].args
    assert queued_provider_id == provider_id
    assert options["auto_apply_results"] is True
    assert options["chat_text"] is False
    with control_plane.db() as conn:
        queued = conn.execute("SELECT * FROM test_runs WHERE id=?", (run_id,)).fetchone()
    assert queued["provider_id"] == provider_id
    assert queued["mode"] == "scheduled"

    # The queued record is now the latest scheduled run, so another scheduler
    # poll cannot enqueue a duplicate while that task is pending.
    retry._release_provider(provider_id)
    assert retry.run_once(control_plane) == 0


def test_disabling_auto_test_stops_retry_even_when_last_status_is_failed(modules):
    control_plane, retry = modules
    now = control_plane.iso_now()
    old_finished = (datetime.now(timezone.utc) - timedelta(minutes=30)).isoformat(timespec="seconds")
    with control_plane.db() as conn:
        cursor = conn.execute(
            """
            INSERT INTO providers(
                name,base_url,api_key_cipher,enabled,priority,main_text_model,
                backup_text_models_json,main_vision_model,backup_vision_models_json,
                protocol_order_json,model_capabilities_json,auto_test_enabled,
                auto_test_interval_hours,auto_test_options_json,last_status,created_at,updated_at
            ) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
            """,
            (
                "关闭重试供应商",
                "https://example.com/v1",
                control_plane.encrypt_secret("test-key"),
                1,
                1,
                "gpt-test",
                "[]",
                "",
                "[]",
                '["responses"]',
                "{}",
                0,
                6,
                "{}",
                retry.FAILED_TEXT_STATUS,
                now,
                now,
            ),
        )
        provider_id = int(cursor.lastrowid)
        conn.execute(
            """
            INSERT INTO test_runs(provider_id,mode,status,options_json,finished_at,created_at)
            VALUES(?,?,?,?,?,?)
            """,
            (provider_id, "scheduled", "completed", "{}", old_finished, old_finished),
        )

    assert retry.run_once(control_plane) == 0
