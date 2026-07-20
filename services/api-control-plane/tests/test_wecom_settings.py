from __future__ import annotations

import sqlite3
from pathlib import Path

from fastapi import HTTPException

import wecom_settings


def payload(**overrides):
    values = {
        "enabled": True,
        "corp_id": "ww-test-corp",
        "app_secret": "secret-value-not-plaintext",
        "agent_id": "1000002",
        "to_users": "alice|bob",
        "callback_token": "callback-token-not-plaintext",
        "callback_aes_key": "abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG",
        "allowed_reply_users": "alice",
        "ticket_hours": 24,
    }
    values.update(overrides)
    return wecom_settings.WeComSettingsInput(**values)


def test_secrets_are_encrypted_and_not_returned(tmp_path: Path):
    db_path = tmp_path / "settings.db"
    saved = wecom_settings.save_settings(payload(), db_path)
    public = wecom_settings.public_settings(saved)
    assert public["outbound_configured"] is True
    assert public["callback_configured"] is True
    assert public["app_secret_configured"] is True
    assert "app_secret" not in public
    assert "callback_token" not in public
    assert "callback_aes_key" not in public
    with sqlite3.connect(db_path) as conn:
        row = conn.execute("SELECT app_secret_cipher,callback_token_cipher,callback_aes_key_cipher FROM wecom_settings WHERE id=1").fetchone()
    joined = "|".join(row)
    assert "secret-value-not-plaintext" not in joined
    assert "callback-token-not-plaintext" not in joined
    assert "abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG" not in joined


def test_blank_secret_fields_keep_existing_values(tmp_path: Path):
    db_path = tmp_path / "settings.db"
    wecom_settings.save_settings(payload(), db_path)
    saved = wecom_settings.save_settings(payload(app_secret="", callback_token="", callback_aes_key="", agent_id="1000003"), db_path)
    assert saved["app_secret"] == "secret-value-not-plaintext"
    assert saved["callback_token"] == "callback-token-not-plaintext"
    assert saved["agent_id"] == "1000003"


def test_enabled_configuration_is_validated(tmp_path: Path):
    db_path = tmp_path / "settings.db"
    try:
        wecom_settings.save_settings(payload(agent_id="not-number"), db_path)
    except HTTPException as exc:
        assert exc.status_code == 400
    else:
        raise AssertionError("invalid AgentId should fail")


def test_page_and_example_do_not_store_wecom_secrets():
    root = Path(__file__).resolve().parents[1]
    page = (root / "static" / "wecom.html").read_text(encoding="utf-8")
    example = (root / ".env.example").read_text(encoding="utf-8")
    bootstrap = (root / "bootstrap.py").read_text(encoding="utf-8")
    assert "/api/admin/wecom/settings" in page
    assert "企业微信" in page
    assert "WECOM_APP_SECRET=" not in example
    assert "WECOM_CALLBACK_TOKEN=" not in example
    assert "include_router(wecom_settings.router)" in bootstrap
    assert "apply_to_bridge" in bootstrap
