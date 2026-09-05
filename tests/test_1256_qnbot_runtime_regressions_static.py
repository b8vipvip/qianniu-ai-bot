from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_generation_acceptance_time_is_independent_from_source_message_time():
    agent = read("src/Bot/ChromeNs/BuyerSessionAgent.cs")
    observation = agent[agent.index("public BuyerSessionAgentObservation ObserveBuyerMessage"):agent.index("public BuyerSessionEventResult RecordEvent")]
    assert "state.LastObservedAt = observedAt" in observation
    assert "acceptedAtUtc = DateTime.UtcNow" in observation
    assert "state.GenerationAcceptedAtUtc[generation] = acceptedAtUtc" in observation
    assert "RegisterAcceptedGeneration(" in observation


def test_generation_age_is_rechecked_synchronously_before_state_progression():
    agent = read("src/Bot/ChromeNs/BuyerSessionAgent.cs")
    current_gate = agent[agent.index("public bool IsCurrent"):agent.index("public CancellationToken GetCancellationToken")]
    transition_gate = agent[agent.index("public bool TryTransition"):agent.index("public void Cancel(")]
    assert "IsGenerationExpiredLocked" in current_gate
    assert "absolute_generation_age_current_gate" in current_gate
    assert "IsGenerationExpiredLocked" in transition_gate
    assert "absolute_generation_age_transition_gate" in transition_gate
    assert "state.ActiveGenerations.Remove(generation)" in transition_gate


def test_duplicate_cdp_recovery_queue_is_bounded_without_disabling_compensation():
    bridge = read("src/Bot/ChromeNs/DuplicateCdpInboundRecoveryBridge.cs")
    assert "MaxPendingInboundEvents = 512" in bridge
    assert "while (Pending.Count >= MaxPendingInboundEvents" in bridge
    assert "TryDeliverLive(item)" in bridge
    assert "target.DispatchInboundEvent(item.Type, item.Response)" in bridge
    assert "BeginForwardedInbound(item.SourceSession)" in bridge


def test_ordinary_fixed_rule_gate_never_fail_opens_into_ai_after_timeout():
    source = read("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")
    method = source[source.index("public static async Task<bool> HandleBeforeMergeAsync("):source.index("private static async Task<bool> HandleOffHoursExclusiveAsync")]
    assert "WaitAsync(1800)" not in method
    assert "已放行消息合并/AI链路" not in method
    assert "GetCancellationToken(" in method
    assert "await gate.WaitAsync(generationToken)" in method
    assert "固定规则串行等待期间generation已失效" in method


def test_fixed_send_checks_generation_before_ready_and_sending():
    source = read("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")
    method = source[source.index("private static async Task<bool> SendFixedAsync"):source.index("public static async Task<bool> TryHandleAsync")]
    assert "fixed_reply_ready" in method
    assert "fixed_reply_sending" in method
    assert "TryTransition(" in method
    assert "SendTextWithRetryAsync" in method


def test_repository_does_not_reintroduce_retired_hyphenated_project_name():
    retired_name = "qianniu" + "-ai-bot"
    excluded_dirs = {".git", "bin", "obj", ".vs", "packages"}
    offenders = []
    for path in ROOT.rglob("*"):
        if not path.is_file() or any(part in excluded_dirs for part in path.parts):
            continue
        if path.suffix.lower() in {".png", ".jpg", ".jpeg", ".gif", ".ico", ".dll", ".exe", ".pdb", ".zip", ".7z"}:
            continue
        try:
            text = path.read_text(encoding="utf-8-sig")
        except (UnicodeDecodeError, OSError):
            continue
        if retired_name.lower() in text.lower():
            offenders.append(str(path.relative_to(ROOT)))
    assert not offenders, "retired project name found in: " + ", ".join(offenders)


def test_legacy_appdata_identity_remains_untouched_for_existing_installations():
    # The repository rename must not orphan existing users' database/configuration directories.
    # QianniuAiBot is a historical product/data identity, not the retired hyphenated repository name.
    matches = []
    for path in (ROOT / "src").rglob("*.cs"):
        try:
            text = path.read_text(encoding="utf-8-sig")
        except (UnicodeDecodeError, OSError):
            continue
        if "QianniuAiBot" in text:
            matches.append(path)
    assert matches, "legacy persistent-data identity unexpectedly disappeared; migration compatibility must be reviewed"
