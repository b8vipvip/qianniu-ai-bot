from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_admin_console_formats_server_timestamps_as_china_time():
    source = read("services/api-control-plane/static/app.js")
    assert 'CONSOLE_TIME_ZONE="Asia/Shanghai"' in source
    assert "Intl.DateTimeFormat" in source
    assert "function cnTime(" in source

    for raw in (
        'esc(t.finished_at||t.created_at||"")',
        "esc(r.created_at)",
        'esc(p.next_test_at||"")',
        'esc(p.last_test_at||"")',
        'esc(t.started_at||t.created_at||"")',
        'esc(t.finished_at||"-")',
        "esc(r.started_at)",
        "esc(r.finished_at)",
        "esc(c.created_at)",
        'esc(c.last_used_at||"-")',
    ):
        assert raw not in source

    assert "cnTime(t.finished_at||t.created_at" in source
    assert "cnTime(r.created_at" in source
    assert "cnTime(p.next_test_at" in source
    assert "cnTime(p.last_test_at" in source
    assert "cnTime(c.created_at" in source
    assert "cnTime(c.last_used_at" in source
    assert "时间（北京时间）" in source
    assert "开始（北京时间）" in source
    assert "创建时间（北京时间）" in source


def test_console_cache_busts_timezone_fix():
    page = read("services/api-control-plane/static/index.html")
    assert '/static/app.js?v=3' in page


def test_server_keeps_canonical_utc_storage():
    backend = read("services/api-control-plane/app.py")
    assert "return datetime.now(timezone.utc)" in backend
    assert 'return utcnow().isoformat(timespec="seconds")' in backend
