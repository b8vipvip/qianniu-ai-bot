from __future__ import annotations

import contextlib
import sqlite3

from fastapi import FastAPI, HTTPException
from fastapi.testclient import TestClient

import bot_client_shop_binding
import bot_web_console
import message_processing_traces


class FakeControlPlane:
    def __init__(self, path):
        self.path = str(path)
        self.app = FastAPI()

    @contextlib.contextmanager
    def db(self):
        conn = sqlite3.connect(self.path)
        conn.row_factory = sqlite3.Row
        try:
            yield conn
            conn.commit()
        finally:
            conn.close()

    @staticmethod
    def iso_now():
        return "2026-08-25T08:00:00+00:00"

    @staticmethod
    def require_admin(request):
        if request.headers.get("x-test-admin") != "yes":
            raise HTTPException(status_code=401, detail="管理员未登录")
        return "admin"


def test_runtime_batch_can_be_queried_by_authenticated_admin(tmp_path, monkeypatch):
    cp = FakeControlPlane(tmp_path / "message-traces.db")
    with cp.db() as conn:
        conn.executescript(
            """
            CREATE TABLE client_tokens(id INTEGER PRIMARY KEY, name TEXT NOT NULL);
            INSERT INTO client_tokens(id,name) VALUES(1,'测试客户端');
            """
        )

    monkeypatch.setattr(bot_web_console, "_runtime_client", lambda request: {"id": 1})
    monkeypatch.setattr(
        bot_client_shop_binding,
        "ensure_binding",
        lambda client_id, shop_key, force, seller: {
            "ok": True,
            "shop_key": shop_key,
        },
    )
    message_processing_traces.install(cp)
    message_processing_traces.init_db()

    with TestClient(cp.app) as client:
        uploaded = client.post(
            "/api/runtime/v1/message-processing-traces/batch",
            headers={"X-Shop-Key": "shop_test"},
            json={
                "events": [
                    {
                        "event_id": "event-1",
                        "trace_id": "trace-1",
                        "seller": "seller-a",
                        "buyer": "buyer-a",
                        "stage": "message_received",
                        "status": "processing",
                        "summary": "已识别买家消息",
                        "detail": "测试消息",
                        "occurred_at": "2026-08-25T08:00:00+00:00",
                    }
                ]
            },
        )
        assert uploaded.status_code == 200
        assert uploaded.json()["saved"] == 1

        unauthenticated = client.get("/api/admin/message-processing-traces")
        assert unauthenticated.status_code == 401

        queried = client.get(
            "/api/admin/message-processing-traces",
            headers={"X-Test-Admin": "yes"},
        )
        assert queried.status_code == 200
        rows = queried.json()
        assert len(rows) == 1
        assert rows[0]["shop_key"] == "shop_test"
        assert rows[0]["trace_id"] == "trace-1"
