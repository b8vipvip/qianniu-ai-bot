from __future__ import annotations

import os
import threading
import traceback
from datetime import datetime, timedelta, timezone
from typing import Any, Dict, Optional


FAILED_TEXT_STATUS = "不可用：没有模型通过文本调用测试"
RETRY_MINUTES = max(1, int(os.getenv("SCHEDULED_DEEP_TEST_RETRY_MINUTES", "10")))
POLL_SECONDS = max(10, int(os.getenv("SCHEDULED_DEEP_TEST_RETRY_POLL_SECONDS", "60")))

_STOP = threading.Event()
_THREAD: Optional[threading.Thread] = None
_THREAD_GUARD = threading.Lock()
_STARTING_PROVIDER_IDS: set[int] = set()
_STARTING_GUARD = threading.Lock()


def _parse_time(value: Any) -> Optional[datetime]:
    text = str(value or "").strip()
    if not text:
        return None
    try:
        parsed = datetime.fromisoformat(text.replace("Z", "+00:00"))
    except Exception:
        return None
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=timezone.utc)
    return parsed.astimezone(timezone.utc)


def _latest_scheduled_run(control_plane: Any, provider_id: int) -> Optional[Dict[str, Any]]:
    with control_plane.db() as conn:
        row = conn.execute(
            """
            SELECT id, provider_id, mode, status, finished_at
            FROM test_runs
            WHERE provider_id=? AND mode='scheduled'
            ORDER BY id DESC
            LIMIT 1
            """,
            (provider_id,),
        ).fetchone()
    return dict(row) if row else None


def should_retry(
    provider: Dict[str, Any],
    latest_run: Optional[Dict[str, Any]],
    *,
    now: Optional[datetime] = None,
    retry_minutes: int = RETRY_MINUTES,
) -> bool:
    if not provider.get("enabled") or not provider.get("auto_test_enabled"):
        return False
    if str(provider.get("last_status") or "").strip() != FAILED_TEXT_STATUS:
        return False
    if not latest_run or latest_run.get("mode") != "scheduled":
        return False
    if latest_run.get("status") != "completed":
        return False
    finished_at = _parse_time(latest_run.get("finished_at"))
    if finished_at is None:
        return False
    current = now or datetime.now(timezone.utc)
    if current.tzinfo is None:
        current = current.replace(tzinfo=timezone.utc)
    return current.astimezone(timezone.utc) >= finished_at + timedelta(minutes=max(1, retry_minutes))


def _claim_provider(provider_id: int) -> bool:
    with _STARTING_GUARD:
        if provider_id in _STARTING_PROVIDER_IDS:
            return False
        _STARTING_PROVIDER_IDS.add(provider_id)
        return True


def _release_provider(provider_id: int) -> None:
    with _STARTING_GUARD:
        _STARTING_PROVIDER_IDS.discard(provider_id)


def _run_retry_worker(control_plane: Any, provider_id: int, options: Dict[str, Any], run_id: int) -> None:
    try:
        control_plane.test_worker(provider_id, "deep", options, run_id)
    finally:
        _release_provider(provider_id)


def start_retry(control_plane: Any, provider: Dict[str, Any]) -> bool:
    provider_id = int(provider["id"])
    if not _claim_provider(provider_id):
        return False
    try:
        if control_plane.provider_test_lock(provider_id).locked():
            _release_provider(provider_id)
            return False
        current = control_plane.get_provider(provider_id)
        latest_run = _latest_scheduled_run(control_plane, provider_id)
        if not should_retry(current, latest_run):
            _release_provider(provider_id)
            return False
        options = control_plane.default_deep_test_options()
        options.update(current.get("auto_test_options") or {})
        options["auto_apply_results"] = True
        run_id = control_plane.create_test_run(provider_id, "scheduled", options)
        thread = threading.Thread(
            target=_run_retry_worker,
            args=(control_plane, provider_id, options, run_id),
            daemon=True,
            name=f"provider-recovery-test-{provider_id}",
        )
        thread.start()
        return True
    except Exception:
        _release_provider(provider_id)
        raise


def run_once(control_plane: Any, *, now: Optional[datetime] = None) -> int:
    started = 0
    for provider in control_plane.list_providers():
        try:
            latest_run = _latest_scheduled_run(control_plane, int(provider["id"]))
            if not should_retry(provider, latest_run, now=now):
                continue
            if start_retry(control_plane, provider):
                started += 1
        except Exception:
            traceback.print_exc()
    return started


def _loop(control_plane: Any) -> None:
    while not _STOP.wait(POLL_SECONDS):
        try:
            run_once(control_plane)
        except Exception:
            traceback.print_exc()


def _startup(control_plane: Any) -> None:
    global _THREAD
    with _THREAD_GUARD:
        if _THREAD is not None and _THREAD.is_alive():
            return
        _STOP.clear()
        _THREAD = threading.Thread(
            target=_loop,
            args=(control_plane,),
            daemon=True,
            name="scheduled-deep-test-retry",
        )
        _THREAD.start()


def _shutdown() -> None:
    _STOP.set()


def install(control_plane: Any) -> None:
    @control_plane.app.on_event("startup")
    def start_scheduled_deep_test_retry() -> None:
        if os.getenv("DISABLE_SCHEDULER", "false").lower() in {"1", "true", "yes"}:
            return
        _startup(control_plane)

    @control_plane.app.on_event("shutdown")
    def stop_scheduled_deep_test_retry() -> None:
        _shutdown()
