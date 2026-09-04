from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
JS = ROOT / "services" / "api-control-plane" / "static" / "bot-web-v2.js"


def test_bot_web_settings_distinguish_saved_sync_applied_and_failed_states():
    text = JS.read_text(encoding="utf-8-sig")

    assert "applied_revision" in text
    assert "last_error" in text
    assert "已保存 · 等待同步" in text
    assert "等待同步" in text
    assert "应用成功" in text
    assert "应用失败" in text
    assert "已应用 · 等待状态回传" in text


def test_bot_web_settings_compare_current_and_desired_before_claiming_success():
    text = JS.read_text(encoding="utf-8-sig")

    assert "botWebDesiredSettingsApplied" in text
    assert "botWebSettingValueEqual" in text
    assert "Object.prototype.hasOwnProperty.call(c,key)" in text
    assert "applied<revision" in text


def test_bot_web_settings_status_escapes_server_error_before_rendering_html():
    text = JS.read_text(encoding="utf-8-sig")

    assert "const lastError=String(settings.last_error||\"\").trim()" in text
    assert "${esc(detail)}" in text
    assert "settingsHint\").innerHTML" in text
