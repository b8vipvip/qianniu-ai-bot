from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_server_push_waits_indefinitely_for_complete_verified_package():
    push = read("services/api-control-plane/bot_update_push.py")
    cache = read("services/api-control-plane/bot_update_cache.py")

    # The server package is published atomically only after download, SHA and size validation.
    assert 'partial = destination.with_suffix(destination.suffix + ".partial")' in cache
    assert 'actual.lower() != expected_sha256.lower()' in cache
    assert 'copied != expected_size' in cache
    assert 'partial.replace(destination)' in cache

    # The SSE readiness gate independently rechecks final size and SHA before notification.
    assert 'target.stat().st_size != expected_size' in push
    assert 'bot_update_cache._hash_file(target).lower() == expected_sha' in push
    ready_check = push.index("if _mirror_ready(metadata):")
    event = push.index("yield _encode_event(public)", ready_check)
    assert ready_check < event
    assert 'public["mirror_ready"] = True' in push[ready_check:event]
    assert 'public["package_verified_on_server"] = True' in push[ready_check:event]


def test_server_push_has_no_grace_timeout_or_early_github_fallback():
    push = read("services/api-control-plane/bot_update_push.py")

    assert "MIRROR_READY_GRACE_SECONDS" not in push
    assert "BOT_UPDATE_PUSH_MIRROR_GRACE_SECONDS" not in push
    assert 'public["mirror_url"] = ""' not in push
    assert "mirror_wait_seconds" not in push
    assert "time.monotonic" not in push
    assert "sends no update event" in push
    assert "no timeout" in push


def test_prefetch_still_downloads_and_verifies_before_push_can_become_ready():
    prefetch = read("services/api-control-plane/bot_update_prefetch.py")

    metadata = prefetch.index("bot_update_cache.get_latest_metadata()")
    ensure = prefetch.index("bot_update_cache.ensure_cached_package(metadata)", metadata)
    assert metadata < ensure
