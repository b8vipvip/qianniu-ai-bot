#!/usr/bin/env python3
"""Pure, privacy-safe lifecycle comparison for qn_uia_messages.v2 snapshots."""
from __future__ import annotations

import hashlib
import json
import os
import tempfile
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from enum import Enum
from pathlib import Path
from typing import Any


class LifecycleKind(str, Enum):
    USER_TEXT = "user_text"
    SELLER_TEXT = "seller_text"
    IMAGE = "image"
    PRODUCT = "product"
    SYSTEM_CANDIDATE = "system_candidate"
    WITHDRAWAL_CANDIDATE = "withdrawal_candidate"
    RISK_CANDIDATE = "risk_candidate"
    SEND_FAILURE_CANDIDATE = "send_failure_candidate"
    ORDER_CANDIDATE = "order_candidate"
    UNKNOWN = "unknown"


class LifecycleStatus(str, Enum):
    OBSERVED = "observed"
    STABLE = "stable"
    NOT_OBSERVED = "not_observed"
    CANDIDATE_WITHDRAWN = "candidate_withdrawn"
    CANDIDATE_BLOCKED = "candidate_blocked"
    CANDIDATE_SEND_FAILED = "candidate_send_failed"
    CONFIRMED_BUYER_WITHDRAWN = "confirmed_buyer_withdrawn"
    CONFIRMED_SELLER_WITHDRAWN = "confirmed_seller_withdrawn"
    UNKNOWN = "unknown"


def _now() -> str:
    return datetime.now(timezone.utc).isoformat()


def _digest(parts: list[str]) -> str:
    return hashlib.sha256("\x1f".join(parts).encode("utf-8")).hexdigest()[:24]


def observation_key(message: dict[str, Any]) -> str:
    existing = str(message.get("observation_key", "")).strip()
    if existing:
        return existing
    return "obs:" + _digest([
        str(message.get("message_key", "")),
        str(message.get("content_hash", "")),
        str(message.get("direction", "")),
        str(message.get("original_type", message.get("type", ""))),
        str(message.get("timestamp", "")),
        "|".join(sorted(str(item) for item in message.get("semantic_flags", []))),
        str(message.get("lifecycle_kind", "")),
        str(message.get("lifecycle_status", "")),
    ])


def kind_for(message: dict[str, Any]) -> LifecycleKind:
    flags = set(message.get("semantic_flags", []))
    if "withdrawal_notice" in flags:
        return LifecycleKind.WITHDRAWAL_CANDIDATE
    message_type = message.get("type")
    if message_type == "image":
        return LifecycleKind.IMAGE
    if message_type == "product":
        return LifecycleKind.PRODUCT
    if message_type == "text" and message.get("direction") == "incoming":
        return LifecycleKind.USER_TEXT
    if message_type == "text" and message.get("direction") == "outgoing":
        return LifecycleKind.SELLER_TEXT
    if "risk_notice" in flags:
        return LifecycleKind.RISK_CANDIDATE
    if "send_failure_notice" in flags:
        return LifecycleKind.SEND_FAILURE_CANDIDATE
    if "order_notice" in flags:
        return LifecycleKind.ORDER_CANDIDATE
    if message_type == "system" or "system_tip" in flags:
        return LifecycleKind.SYSTEM_CANDIDATE
    return LifecycleKind.UNKNOWN


def _event(message: dict[str, Any], *, status: LifecycleStatus, prior: dict[str, Any] | None = None,
           ignore_reason: str = "", confidence: str = "low", evidence: dict[str, Any] | None = None,
           history_initial: bool = False, observation_count: int = 1) -> dict[str, Any]:
    direction = str(message.get("observed_direction_guess", message.get("direction", "unknown")))
    prior_direction = str((prior or {}).get("direction", ""))
    lifecycle_kind = kind_for(message)
    actionable = False
    actionable_eligible = (
        lifecycle_kind == LifecycleKind.USER_TEXT
        and not history_initial
        and status in (LifecycleStatus.OBSERVED, LifecycleStatus.STABLE)
    )
    return {
        "message_key": message.get("message_key", ""),
        "observation_key": observation_key(message),
        "node_identity_hash": message.get("node_identity_hash", ""),
        "observed_direction": direction,
        "prior_direction": prior_direction,
        "original_type": message.get("original_type", message.get("type", "unknown")),
        "lifecycle_kind": lifecycle_kind.value,
        "lifecycle_status": status.value,
        "actionable": actionable,
        "actionable_eligible": actionable_eligible,
        "ignore_reason": ignore_reason,
        "evidence": evidence or {},
        "confidence": confidence,
        "requires_local_validation": confidence != "high",
        "first_seen_at": message.get("first_seen_at", _now()),
        "last_seen_at": _now(),
        "observation_count": observation_count,
        "content_hash": message.get("content_hash", ""),
        "semantic_flags": list(message.get("semantic_flags", [])),
        "semantic_candidates": list(message.get("semantic_candidates", [])),
        "history_initial": history_initial,
    }


def _messages(snapshot: dict[str, Any]) -> dict[str, dict[str, Any]]:
    if snapshot.get("schema_version") != "qn_uia_messages.v2":
        raise ValueError("snapshot schema must be qn_uia_messages.v2")
    result = {}
    for item in snapshot.get("messages", []):
        if isinstance(item, dict) and item.get("message_key"):
            result[str(item["message_key"])] = item
    return result


def compare_snapshots(before: dict[str, Any], after: dict[str, Any], *, history_initial: bool = False,
                      previously_not_observed: set[str] | None = None) -> dict[str, Any]:
    old = _messages(before)
    new = _messages(after)
    previously_not_observed = previously_not_observed or set()
    changes = {name: [] for name in ("added", "updated", "unchanged", "not_observed", "reobserved")}
    candidate_events: list[dict[str, Any]] = []
    confirmed_events: list[dict[str, Any]] = []

    for key, current in new.items():
        prior = old.get(key)
        if prior is None:
            event = _event(current, status=LifecycleStatus.OBSERVED,
                           ignore_reason="history_initial" if history_initial else "new_observation",
                           history_initial=history_initial)
            if key in previously_not_observed:
                event["ignore_reason"] = "reobserved"
                changes["reobserved"].append(event)
            else:
                changes["added"].append(event)
                if "withdrawal_notice" in current.get("semantic_flags", []):
                    candidate = _event(current, status=LifecycleStatus.CANDIDATE_WITHDRAWN,
                                       ignore_reason="standalone_withdrawal_candidate",
                                       confidence="low", evidence={"evidence_source": "standalone_observation"})
                    candidate_events.append(candidate)
            continue

        same_observation = observation_key(prior) == observation_key(current)
        if same_observation:
            changes["unchanged"].append(_event(current, status=LifecycleStatus.STABLE,
                                                prior=prior, ignore_reason="unchanged",
                                                history_initial=history_initial, observation_count=2))
            continue

        updated = _event(current, status=LifecycleStatus.OBSERVED, prior=prior,
                         ignore_reason="updated", observation_count=2)
        changes["updated"].append(updated)
        strong = (
            prior.get("message_key") == current.get("message_key")
            and prior.get("node_identity_hash")
            and prior.get("node_identity_hash") == current.get("node_identity_hash")
            and prior.get("content_hash") != current.get("content_hash")
            and prior.get("type") == "text"
            and "withdrawal_notice" in current.get("semantic_flags", [])
        )
        if strong and prior.get("direction") in ("incoming", "outgoing"):
            buyer = prior.get("direction") == "incoming"
            status = (LifecycleStatus.CONFIRMED_BUYER_WITHDRAWN if buyer
                      else LifecycleStatus.CONFIRMED_SELLER_WITHDRAWN)
            confirmed_events.append(_event(
                current, status=status, prior=prior,
                ignore_reason="buyer_withdrawn" if buyer else "seller_withdrawn",
                confidence="high", evidence={"evidence_source": "in_place_same_message_key_update",
                                             "same_message_key": True, "same_node_identity": True,
                                             "content_changed": True}, observation_count=2,
            ))
        elif "withdrawal_notice" in current.get("semantic_flags", []):
            candidate_events.append(_event(current, status=LifecycleStatus.CANDIDATE_WITHDRAWN,
                                           prior=prior, ignore_reason="insufficient_in_place_evidence",
                                           evidence={"evidence_source": "updated_observation"}, observation_count=2))

    for key, prior in old.items():
        if key not in new:
            changes["not_observed"].append(_event(prior, status=LifecycleStatus.NOT_OBSERVED,
                                                   prior=prior, ignore_reason="not_observed",
                                                   history_initial=history_initial))

    return {
        "schema_version": "qn_uia_lifecycle_evidence.v2",
        "changes": changes,
        "candidate_events": candidate_events,
        "confirmed_events": confirmed_events,
        "safety": {"raw_text_stored": False, "raw_sender_stored": False,
                   "raw_automation_id_stored": False, "actionable_events": 0},
    }


ALLOWED_STATE_FIELDS = {
    "message_key", "observation_key", "content_hash", "node_identity_hash", "status",
    "first_seen_at", "last_seen_at", "observation_count", "processed", "history_initial",
}


def load_state(path: Path) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise ValueError(f"invalid state file: {exc}") from exc
    if not isinstance(value, dict) or not isinstance(value.get("records", {}), dict):
        raise ValueError("invalid state file structure")
    for record in value["records"].values():
        if not isinstance(record, dict) or not set(record).issubset(ALLOWED_STATE_FIELDS):
            raise ValueError("state file contains unsupported fields")
    return value


def write_state_atomic(path: Path, state: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    for record in state.get("records", {}).values():
        if not isinstance(record, dict) or not set(record).issubset(ALLOWED_STATE_FIELDS):
            raise ValueError("state contains unsupported fields")
    fd, temp_name = tempfile.mkstemp(prefix=path.name + ".", suffix=".tmp", dir=str(path.parent))
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as handle:
            json.dump(state, handle, ensure_ascii=False, indent=2)
            handle.write("\n")
        os.replace(temp_name, path)
    finally:
        if os.path.exists(temp_name):
            os.unlink(temp_name)
