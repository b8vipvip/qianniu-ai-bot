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
    assert "已保持单次安装，避免递归栈溢出" in source

    block_start = source.index("if (InstalledWrappers.TryGetValue(key, out installed))")
    block_end = source.index("var next = current;", block_start)
    existing_wrapper_block = source[block_start:block_end]

    # Once the coordinator has been wrapped by this service, later timer scans
    # must always leave it alone even when another observer has wrapped the
    # installed delegate. Re-wrapping there builds an unbounded closure chain.
    assert "continue;" in existing_wrapper_block
    assert "ReferenceEquals(current, installed))\n                        continue;" not in source


def test_periodic_scan_only_discovers_new_coordinators():
    source = source_text()
    assert "_patchTimer = new Timer(_ => PatchExisting(), null, 350, 700);" in source
    assert "InstalledWrappers[key] = wrapped;" in source
    assert "0xc00000fd (stack overflow)" in source
