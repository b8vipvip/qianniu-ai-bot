from __future__ import annotations

from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "Bot" / "ChromeNs" / "BotWebConsoleSyncService.cs"


def source_text() -> str:
    return SOURCE.read_text(encoding="utf-8-sig")


def test_bot_web_observer_wraps_each_coordinator_at_most_once():
    source = source_text()
    assert "InstalledWrappers.TryGetValue(key, out installed)" in source
    assert "HandlerReplacementWarnings.TryAdd(key, 0)" in source
    assert "保持单次安装" in source
    assert "避免闭包链增长" in source

    block_start = source.index("if (InstalledWrappers.TryGetValue(key, out installed))")
    block_end = source.index("var capturedQn = qn;", block_start)
    existing_wrapper_block = source[block_start:block_end]

    # Once this service has wrapped a coordinator, later discovery scans must
    # leave that coordinator alone even if another observer wraps the delegate.
    assert "continue;" in existing_wrapper_block
    assert "ReferenceEquals(current, installed))\n                        continue;" not in source


def test_periodic_scan_only_discovers_new_coordinators():
    source = source_text()
    assert "_patchTimer = new Timer(_ => PatchExisting()" in source
    assert "InstalledWrappers[key] = wrapped;" in source
    assert "PatchExisting();" in source
    assert "handlerField.SetValue(coordinator, wrapped);" in source
