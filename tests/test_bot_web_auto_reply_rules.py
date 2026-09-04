from __future__ import annotations

import importlib.util
import sqlite3
from contextlib import contextmanager
from pathlib import Path

import pytest


ROOT = Path(__file__).resolve().parents[1]
MODULE_PATH = ROOT / "services" / "api-control-plane" / "bot_web_auto_reply_rules.py"
WINDOWS_SYNC_PATH = ROOT / "src" / "Bot" / "ChromeNs" / "BotWebAutoReplyRulesSyncService.cs"
WINDOWS_PROPS_PATH = ROOT / "src" / "Bot" / "Directory.Build.props"
BOOTSTRAP_PATH = ROOT / "services" / "api-control-plane" / "bootstrap.py"
DOCKERFILE_PATH = ROOT / "services" / "api-control-plane" / "Dockerfile"
WEB_JS_PATH = ROOT / "services" / "api-control-plane" / "static" / "bot-web-auto-reply-rules.js"
LOADER_JS_PATH = ROOT / "services" / "api-control-plane" / "static" / "bot-web-bot-enabled.js"
HAS_FASTAPI = importlib.util.find_spec("fastapi") is not None
needs_server_deps = pytest.mark.skipif(not HAS_FASTAPI, reason="server dependencies are not installed in Windows static CI")


def load_module():
    spec = importlib.util.spec_from_file_location("bot_web_auto_reply_rules_under_test", MODULE_PATH)
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
        return "2026-09-04T10:00:00+00:00"


class FakeConsole:
    @staticmethod
    def _is_online(value):
        return bool(value)


def prepare(module, tmp_path: Path):
    cp = FakeControlPlane(tmp_path / "rules.db")
    module._cp = cp
    module._console = FakeConsole()
    with cp.db() as conn:
        conn.executescript(
            """
            CREATE TABLE client_tokens (id INTEGER PRIMARY KEY);
            CREATE TABLE bot_client_state (client_id INTEGER PRIMARY KEY, last_seen_at TEXT);
            INSERT INTO client_tokens(id) VALUES(1),(2);
            INSERT INTO bot_client_state(client_id,last_seen_at) VALUES(1,'2026-09-04T10:00:00+00:00');
            """
        )
    module.init_db()
    return cp


def local_rules(module, **changes):
    data = dict(module.DEFAULT_RULE_SETTINGS)
    data.update(changes)
    return data


@needs_server_deps
def test_first_runtime_sync_adopts_windows_rules_without_overwrite(tmp_path):
    module = load_module()
    prepare(module, tmp_path)
    current = local_rules(
        module,
        manual_handoff_keywords="退款,我要真人",
        work_start_time="10:30",
        work_end_time="23:15",
        off_hours_reply_mode="固定预设答案",
        off_hours_fixed_text="当前已下班，请明天联系。",
    )

    result = module.runtime_sync_auto_reply_rules(
        module.RuntimeAutoReplyRuleSyncInput(current_settings=current),
        client={"id": 1},
    )

    assert result["revision"] == 1
    assert result["applied_revision"] == 1
    assert result["desired_settings"] == current
    snapshot = module._snapshot(1)
    assert snapshot["desired"] == current
    assert snapshot["current"] == current


@needs_server_deps
def test_web_change_waits_for_actual_windows_confirmation(tmp_path):
    module = load_module()
    prepare(module, tmp_path)
    original = local_rules(module)
    module.runtime_sync_auto_reply_rules(
        module.RuntimeAutoReplyRuleSyncInput(current_settings=original),
        client={"id": 1},
    )

    changed_input = module.AutoReplyRuleSettingsInput(
        manual_handoff_keywords="退款,投诉,联系人工",
        work_hours_enabled=True,
        work_start_time="08:30",
        work_end_time="20:45",
        off_hours_reply_mode="固定预设答案",
        off_hours_fixed_text="人工已下班，请在工作时间联系我们。",
    )
    waiting = module.put_auto_reply_rules(changed_input, client={"id": 1})
    assert waiting["revision"] == 2
    assert waiting["applied_revision"] == 1

    response = module.runtime_sync_auto_reply_rules(
        module.RuntimeAutoReplyRuleSyncInput(current_settings=original),
        client={"id": 1},
    )
    assert response["revision"] == 2
    assert response["applied_revision"] == 1
    desired = response["desired_settings"]
    assert desired["manual_handoff_keywords"] == "退款,投诉,联系人工"
    assert desired["work_start_time"] == "08:30"

    applied = module.runtime_sync_auto_reply_rules(
        module.RuntimeAutoReplyRuleSyncInput(current_settings=desired),
        client={"id": 1},
    )
    assert applied["applied_revision"] == 2
    assert module._snapshot(1)["applied_revision"] == 2


@needs_server_deps
def test_rule_state_is_isolated_by_client_id(tmp_path):
    module = load_module()
    prepare(module, tmp_path)
    one = local_rules(module, manual_handoff_keywords="店铺一")
    two = local_rules(module, manual_handoff_keywords="店铺二")
    module.runtime_sync_auto_reply_rules(module.RuntimeAutoReplyRuleSyncInput(current_settings=one), client={"id": 1})
    module.runtime_sync_auto_reply_rules(module.RuntimeAutoReplyRuleSyncInput(current_settings=two), client={"id": 2})
    assert module._snapshot(1)["desired"]["manual_handoff_keywords"] == "店铺一"
    assert module._snapshot(2)["desired"]["manual_handoff_keywords"] == "店铺二"


@needs_server_deps
def test_validation_rejects_bad_time_and_empty_fixed_reply(tmp_path):
    from fastapi import HTTPException

    module = load_module()
    prepare(module, tmp_path)
    with pytest.raises(HTTPException) as bad_time:
        module.put_auto_reply_rules(
            module.AutoReplyRuleSettingsInput(work_start_time="99:00"),
            client={"id": 1},
        )
    assert bad_time.value.status_code == 422

    with pytest.raises(HTTPException) as empty_reply:
        module.put_auto_reply_rules(
            module.AutoReplyRuleSettingsInput(
                off_hours_reply_mode="固定预设答案",
                off_hours_fixed_text="",
            ),
            client={"id": 1},
        )
    assert empty_reply.value.status_code == 422


def test_windows_sync_only_mutates_whitelisted_non_secret_rule_fields():
    text = WINDOWS_SYNC_PATH.read_text(encoding="utf-8-sig")
    props = WINDOWS_PROPS_PATH.read_text(encoding="utf-8-sig")
    assert "/api/runtime/v1/bot-web/auto-reply-rules/sync" in text
    assert "BuildCurrentSettings()" in text
    assert "BotFeatureStore.GetAutoReplyRules()" in text
    assert "BotFeatureStore.SaveAutoReplyRules(cfg)" in text
    assert "cfg.ManualKeywords =" in text
    assert "cfg.NoAutoReplyKeywords =" in text
    assert "cfg.EnableWorkHours =" in text
    assert "cfg.WorkStartTime =" in text
    assert "cfg.WorkEndTime =" in text
    assert "cfg.OffHoursReplyMode =" in text
    assert "cfg.OffHoursFixedText =" in text
    assert "ChromeNs\\BotWebAutoReplyRulesSyncService.cs" in props
    assert "cfg.WeChatWebhook =" not in text
    assert "cfg.SmtpPassword =" not in text
    assert "cfg.OrderPlacedApiToken =" not in text
    assert "cfg.FeishuWebhook =" not in text
    assert "cfg.DingTalkWebhook =" not in text


def test_mobile_ui_exposes_rules_without_secret_inputs():
    text = WEB_JS_PATH.read_text(encoding="utf-8")
    assert "强制转人工关键词" in text
    assert "仅人工确认关键词" in text
    assert "启用工作时间判断" in text
    assert "下班回复方式" in text
    assert "/api/bot-web/auto-reply-rules" in text
    lowered = text.lower()
    for forbidden in (
        "smtp_password",
        "smtppassword",
        "orderplacedapitoken",
        "wechatwebhook",
        "feishuwebhook",
        "dingtalkwebhook",
        "clienttoken",
        "cookie",
    ):
        assert forbidden not in lowered

    loader = LOADER_JS_PATH.read_text(encoding="utf-8")
    assert "/static/bot-web-auto-reply-rules.js?v=1" in loader


def test_bootstrap_and_container_package_rule_service():
    bootstrap = BOOTSTRAP_PATH.read_text(encoding="utf-8-sig")
    dockerfile = DOCKERFILE_PATH.read_text(encoding="utf-8")
    assert "import bot_web_auto_reply_rules" in bootstrap
    assert "bot_web_auto_reply_rules.install(control_plane, bot_web_console)" in bootstrap
    assert "bot_web_auto_reply_rules.init_db()" in bootstrap
    assert "bot_web_auto_reply_rules.py" in dockerfile
