from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_adaptive_timing_learns_only_realistic_intervals_and_is_bounded():
    code = read("src/Bot/ChromeNs/AdaptiveReplyTimingService.cs")
    assert "interval < 120 || interval > 4500" in code
    assert "RecentIntervalsMs.Count > 24" in code
    assert "p75 + 180" in code
    assert "Clamp(p75 + 180, 350, 1600)" in code
    assert "profile.RecentIntervalsMs.Count < 3" in code
    assert "AdaptiveDelayKind.Fragment" in code
    assert "AdaptiveDelayKind.Complete" in code
    assert "Clamp(adjusted, 300, 950)" in code
    assert "Clamp(adjusted, 650, 1550)" in code


def test_burst_coordinator_records_interval_after_duplicate_filter():
    code = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    duplicate = code.index("state.Items.Any(x => string.Equals(x.MessageKey")
    previous = code.index("var previousReceivedAt", duplicate)
    record = code.index("AdaptiveReplyTimingService.RecordInterval", previous)
    add = code.index("state.Items.Add(item)", record)
    assert duplicate < previous < record < add


def test_existing_base_rules_and_four_second_cap_remain():
    code = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    assert "TimeSpan.FromSeconds(4)" in code
    assert "return 80;" in code
    assert "baseline = 700" in code
    assert "baseline = 950" in code
    assert "baseline = 1200" in code
    assert "baseline = 800" in code
    assert "baseline = 350" in code
    assert "AdaptiveReplyTimingService.AdjustDelay" in code


def test_timing_profile_is_persistent_private_and_flushed_on_shutdown():
    service = read("src/Bot/ChromeNs/AdaptiveReplyTimingService.cs")
    app = read("src/Bot/App.xaml.cs")
    targets = read("src/Directory.Build.targets")
    assert "adaptive-reply-timing.json" in service
    assert "Environment.SpecialFolder.LocalApplicationData" in service
    assert "TimeSpan.FromSeconds(30)" in service
    assert "Take(2000)" in service
    assert "AddDays(-30)" in service
    assert "AdaptiveReplyTimingService.Flush()" in app
    assert "AdaptiveReplyTimingService.cs" in targets
