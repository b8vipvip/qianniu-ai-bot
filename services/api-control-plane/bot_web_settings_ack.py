from __future__ import annotations

import hashlib
import json
from typing import Any, Dict, Optional


CLIENT_APPLIED_SETTING_KEYS = (
    "auto_reply_enabled",
    "message_sync_enabled",
    "allow_web_manual_reply",
    "sync_interval_seconds",
)
SERVER_ONLY_SETTING_KEYS = ("message_retention_days",)

_cp: Any = None
_console: Any = None
_original_settings_for: Any = None


def install(control_plane: Any, console_module: Any) -> None:
    """Extend Bot Web settings with a real desired -> observed acknowledgement loop.

    The existing console routes keep ownership of authentication and the settings
    whitelist.  This module only augments `_settings_for`, so a setting is reported
    as applied only after a later Windows sync reports the same client-applied
    values.  Server-only values are reflected locally and never require a fake
    Windows acknowledgement.
    """
    global _cp, _console, _original_settings_for
    _cp = control_plane
    _console = console_module
    if _original_settings_for is not None:
        return
    _original_settings_for = console_module._settings_for
    console_module._settings_for = _settings_for_with_ack


def init_db() -> None:
    if _cp is None:
        raise RuntimeError("bot_web_settings_ack.install() must run before init_db()")
    with _cp.db() as conn:
        conn.executescript(
            """
            CREATE TABLE IF NOT EXISTS bot_web_settings_ack (
                client_id INTEGER PRIMARY KEY,
                desired_fingerprint TEXT NOT NULL DEFAULT '',
                revision INTEGER NOT NULL DEFAULT 1,
                applied_revision INTEGER NOT NULL DEFAULT 0,
                last_error TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL,
                FOREIGN KEY(client_id) REFERENCES client_tokens(id) ON DELETE CASCADE
            );
            """
        )


def _canonical_fingerprint(desired: Dict[str, Any]) -> str:
    payload = json.dumps(desired or {}, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
    return hashlib.sha256(payload.encode("utf-8")).hexdigest()


def _setting_equal(key: str, left: Any, right: Any) -> bool:
    if key in {"auto_reply_enabled", "message_sync_enabled", "allow_web_manual_reply"}:
        return bool(left) == bool(right)
    if key == "sync_interval_seconds":
        try:
            return int(left) == int(right)
        except (TypeError, ValueError):
            return False
    return left == right


def _client_settings_match(desired: Dict[str, Any], current: Dict[str, Any]) -> bool:
    for key in CLIENT_APPLIED_SETTING_KEYS:
        if key not in current or key not in desired:
            return False
        if not _setting_equal(key, current.get(key), desired.get(key)):
            return False
    return True


def _ack_row(client_id: int, desired: Dict[str, Any], current: Dict[str, Any]) -> Dict[str, Any]:
    fingerprint = _canonical_fingerprint(desired)
    now = _cp.iso_now()
    with _cp.db() as conn:
        conn.execute(
            """
            INSERT OR IGNORE INTO bot_web_settings_ack(
                client_id,desired_fingerprint,revision,applied_revision,last_error,updated_at
            ) VALUES(?,?,1,0,'',?)
            """,
            (client_id, fingerprint, now),
        )
        row = conn.execute(
            "SELECT desired_fingerprint,revision,applied_revision,last_error FROM bot_web_settings_ack WHERE client_id=?",
            (client_id,),
        ).fetchone()
        if row is None:
            return {"revision": 1, "applied_revision": 0, "last_error": ""}

        revision = max(1, int(row["revision"] or 1))
        applied = max(0, int(row["applied_revision"] or 0))
        last_error = str(row["last_error"] or "")
        previous_fingerprint = str(row["desired_fingerprint"] or "")

        if previous_fingerprint != fingerprint:
            changed = conn.execute(
                """
                UPDATE bot_web_settings_ack
                SET desired_fingerprint=?,revision=revision+1,last_error='',updated_at=?
                WHERE client_id=? AND desired_fingerprint=?
                """,
                (fingerprint, now, client_id, previous_fingerprint),
            ).rowcount
            row = conn.execute(
                "SELECT revision,applied_revision,last_error FROM bot_web_settings_ack WHERE client_id=?",
                (client_id,),
            ).fetchone()
            revision = max(1, int(row["revision"] or 1))
            applied = max(0, int(row["applied_revision"] or 0))
            last_error = str(row["last_error"] or "")
            if changed:
                # A new target revision must be observed again by Windows before it
                # can be called applied, even if a previous revision was successful.
                applied = min(applied, revision - 1)

        if _client_settings_match(desired, current) and applied != revision:
            conn.execute(
                "UPDATE bot_web_settings_ack SET applied_revision=?,last_error='',updated_at=? WHERE client_id=?",
                (revision, now, client_id),
            )
            applied = revision
            last_error = ""

        return {
            "revision": revision,
            "applied_revision": applied,
            "last_error": last_error,
        }


def _settings_for_with_ack(client_id: int) -> Dict[str, Any]:
    if _original_settings_for is None:
        raise RuntimeError("Bot Web settings acknowledgement is not installed")
    base = _original_settings_for(client_id)
    desired = dict(base.get("desired") or {})
    current = dict(base.get("current") or {})

    # Retention is enforced by the control plane itself.  Reflect it as current so
    # the Web UI does not wait forever for a Windows field that Windows does not own.
    for key in SERVER_ONLY_SETTING_KEYS:
        if key in desired:
            current[key] = desired[key]

    ack = _ack_row(client_id, desired, current)
    return {
        "desired": desired,
        "current": current,
        "revision": ack["revision"],
        "applied_revision": ack["applied_revision"],
        "last_error": ack["last_error"],
        "client_applied_keys": list(CLIENT_APPLIED_SETTING_KEYS),
        "server_only_keys": list(SERVER_ONLY_SETTING_KEYS),
    }
