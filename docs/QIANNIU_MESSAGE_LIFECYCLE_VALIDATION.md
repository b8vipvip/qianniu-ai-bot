# Qianniu message lifecycle local validation guide

Last updated: 2026-07-15

## What this PR implements in Codex Cloud

- A generic lifecycle model for `qn_uia_messages.v2`-style dictionaries/JSON snapshots.
- A read-only evidence probe that compares two user-captured snapshots and emits a redacted bundle.
- Fictional unit-test fixtures for lifecycle transitions, candidate classification, reobservation, history reload, and corrupted state handling.

This work does **not** call AI, does **not** send messages, and is **not** connected to the production bot.

## Verified local Qianniu validation

Buyer withdrawal has one confirmed Qianniu `9.97.56N` UIA structure: the same `message_key` remains visible and changes in place from `incoming`/`text` user content to an `unknown`/`system` withdrawal notice. The original content fingerprint changes, the original body is no longer present, no stable key is added or removed, and the evidence source is `in_place_same_message_key_update`. Only this exact in-place same-key shape can produce `confirmed_withdrawn` with `requires_local_validation=false`.

Standalone withdrawal notices are still only candidates because historical off-screen withdrawal nodes can be present in the UIA tree. Do not globally search for “撤回” and associate it with the nearest message.

## What still requires local Qianniu validation

The following remain `requires_local_validation` until a local operator captures UIA/CDP evidence:

- seller withdrawal
- system tips
- risk/block notices
- outgoing send failure
- virtualized list redraw/off-screen node recycling
- history reload

Node disappearance is only `not_observed`; it is not proof of withdrawal. Phrase matching is only a candidate rule, never a confirmed rule except for the verified same-`message_key` buyer-withdrawal update described above.

## Short local commands

```powershell
python tools\qn_discovery_lab\qn_uia_lifecycle_probe.py capture --output C:\qn_evidence\before.json
python tools\qn_discovery_lab\qn_uia_lifecycle_probe.py capture --output C:\qn_evidence\after.json
python tools\qn_discovery_lab\qn_uia_lifecycle_probe.py compare --scenario buyer_withdrawal --before C:\qn_evidence\before.json --after C:\qn_evidence\after.json --output C:\qn_evidence\buyer_withdrawal_bundle.json
```

Sensitive raw snapshots are disabled by default. If a future local run needs raw text to identify real system wording, use only an out-of-repository path and the explicit confirmation flag:

```powershell
python tools\qn_discovery_lab\qn_uia_lifecycle_probe.py capture --include-sensitive --confirm-local-sensitive STORE_LOCAL_SENSITIVE --output C:\qn_private_evidence\raw_before.json
```

Never upload sensitive snapshots, screenshots, customer names, account IDs, chat text, product/private order data, or raw AutomationIds.

## Scenario evidence steps

1. Capture `before.json` with the conversation visible and idle.
2. Manually perform exactly one local scenario action in Qianniu.
3. Capture `after.json` without scrolling, clicking, switching sessions, sending keys, or using AI from the probe.
4. Run `compare` with one of: `buyer_withdrawal`, `seller_withdrawal`, `system_tip`, `risk_or_block`, `outgoing_send_failure`, `virtualized_list`, `history_reload`.
5. Review only the redacted evidence bundle.

## Reading the evidence bundle

- `changes.added`: stable keys first observed in the after snapshot.
- `changes.updated`: same stable identity with changed direction/type/status/content fingerprint/observation key.
- `changes.reobserved`: previously `not_observed`, now visible again.
- `changes.not_observed`: previously visible, currently absent; not a withdrawal conclusion.
- `candidate_events`: conservative candidate classification and correlation hints.

## Evidence needed to promote candidate to confirmed

A confirmed lifecycle status requires stronger local evidence, such as a verified message ID/CDP state transition or a stable UIA structure that uniquely identifies the platform state. Unverified text phrases, disappearing UIA nodes, or “previous message” position alone are insufficient.

## Production bot boundary review

Read-only review found these existing boundaries for future shadow-mode integration:

- `src/Bot/ChromeNs/QN.cs` and `src/Bot/ChromeNs/QNRpa.cs` own existing Qianniu/Chrome automation surfaces.
- `src/Bot/ChromeNs/CDPClient.cs` owns existing CDP/WebSocket plumbing.
- `src/Bot/ChatRecord/` stores chat request/response records.
- `src/Bot/Automation/ChatDeskNs/` locates chat desk windows/accounts and exposes existing desk automation abstractions.

Future lifecycle integration should first run as shadow mode beside the existing buyer-message trigger path, using redacted stable keys and hashes only. Production sending must not change until local evidence proves current-conversation detection, structured extraction, deduplication, send-result verification, and failure handling.
