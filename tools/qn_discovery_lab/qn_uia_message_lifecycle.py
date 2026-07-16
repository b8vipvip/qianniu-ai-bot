"""Generic, redaction-first lifecycle model for qn_uia_messages.v2 snapshots.

This module is intentionally decoupled from Qianniu/UIA.  It accepts Python
``dict`` objects (or JSON lists) shaped like qn_uia_messages.v2 extraction
results and never sends messages, calls AI, clicks, scrolls, or inspects a live
application.
"""
from __future__ import annotations

import hashlib
import json
import os
import tempfile
from dataclasses import dataclass, field, asdict
from datetime import datetime, timezone
from enum import Enum
from typing import Any, Dict, Iterable, List, Optional, Tuple

SCHEMA_VERSION = "qn_uia_lifecycle_state.v1"
EVIDENCE_SCHEMA_VERSION = "qn_uia_lifecycle_evidence.v1"


class LifecycleKind(str, Enum):
    USER_TEXT = "user_text"
    SELLER_TEXT = "seller_text"
    IMAGE = "image"
    PRODUCT = "product"
    SYSTEM_CANDIDATE = "system_candidate"
    WITHDRAWAL_CANDIDATE = "withdrawal_candidate"
    RISK_CANDIDATE = "risk_candidate"
    ORDER_CANDIDATE = "order_candidate"
    SEND_FAILURE_CANDIDATE = "send_failure_candidate"
    UNKNOWN = "unknown"


class LifecycleStatus(str, Enum):
    OBSERVED = "observed"
    STABLE = "stable"
    NOT_OBSERVED = "not_observed"
    CANDIDATE_WITHDRAWN = "candidate_withdrawn"
    CANDIDATE_BLOCKED = "candidate_blocked"
    CANDIDATE_REJECTED = "candidate_rejected"
    CANDIDATE_SEND_FAILED = "candidate_send_failed"
    CONFIRMED_WITHDRAWN = "confirmed_withdrawn"
    CONFIRMED_BLOCKED = "confirmed_blocked"
    CONFIRMED_REJECTED = "confirmed_rejected"
    CONFIRMED_SEND_FAILED = "confirmed_send_failed"
    UNKNOWN = "unknown"


CANDIDATE_RULES: Tuple[Dict[str, Any], ...] = (
    {"id": "withdrawal.generic", "phrases": ["撤回了一条消息", "消息已撤回"], "kind": LifecycleKind.WITHDRAWAL_CANDIDATE, "status": LifecycleStatus.CANDIDATE_WITHDRAWN, "confidence": 0.45},
    {"id": "withdrawal.seller", "phrases": ["你撤回了一条消息"], "kind": LifecycleKind.WITHDRAWAL_CANDIDATE, "status": LifecycleStatus.CANDIDATE_WITHDRAWN, "confidence": 0.50},
    {"id": "withdrawal.buyer", "phrases": ["对方撤回了一条消息"], "kind": LifecycleKind.WITHDRAWAL_CANDIDATE, "status": LifecycleStatus.CANDIDATE_WITHDRAWN, "confidence": 0.50},
    {"id": "system.tip", "phrases": ["温馨提示", "系统消息", "服务提醒"], "kind": LifecycleKind.SYSTEM_CANDIDATE, "status": LifecycleStatus.OBSERVED, "confidence": 0.40},
    {"id": "risk.block", "phrases": ["风险提示", "内容涉嫌违规", "该消息无法展示", "内容已被屏蔽"], "kind": LifecycleKind.RISK_CANDIDATE, "status": LifecycleStatus.CANDIDATE_BLOCKED, "confidence": 0.45},
    {"id": "send.failure", "phrases": ["消息发送失败", "发送失败"], "kind": LifecycleKind.SEND_FAILURE_CANDIDATE, "status": LifecycleStatus.CANDIDATE_SEND_FAILED, "confidence": 0.45},
)


def utc_now() -> str:
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat()


def sha256_text(value: Any) -> str:
    return hashlib.sha256(str(value or "").encode("utf-8")).hexdigest()


def _get(msg: Dict[str, Any], *names: str) -> Any:
    for name in names:
        if name in msg and msg[name] not in (None, ""):
            return msg[name]
    return None


def normalize_direction(value: Any) -> str:
    v = str(value or "unknown").lower()
    if v in {"incoming", "buyer", "in"}:
        return "incoming"
    if v in {"outgoing", "seller", "out"}:
        return "outgoing"
    return "unknown"


def text_fingerprint(msg: Dict[str, Any]) -> Optional[str]:
    existing = _get(msg, "text_hash", "body_hash", "content_hash", "message_hash")
    if existing:
        return str(existing)
    text = _get(msg, "text", "body", "content", "name")
    return sha256_text(text) if text else None


def stable_message_key(msg: Dict[str, Any]) -> str:
    key = _get(msg, "message_key", "stable_key")
    if key:
        return str(key)
    raw = "|".join(str(_get(msg, n) or "") for n in ("key_source", "direction", "type", "timestamp", "sender_hash"))
    fp = text_fingerprint(msg) or sha256_text(raw)
    return "derived:" + sha256_text(raw + "|" + fp)


def node_identity_hash(msg: Dict[str, Any]) -> str:
    raw = "|".join(str(_get(msg, n) or "") for n in ("automation_id", "runtime_id", "bounding_rect"))
    return sha256_text(raw or stable_message_key(msg))


def observation_key(msg: Dict[str, Any]) -> str:
    """Return the current observation-state fingerprint for a stable message.

    ``message_key`` is the stable node/message identity.  The observation key is
    intentionally more volatile: it changes when the same stable node changes
    direction, type, content fingerprint, timestamp, or inferred lifecycle state.
    This lets compare_snapshot report in-place platform state transitions as
    ``updated`` instead of losing them behind a stable UIA key.
    """
    kind, status, _conf, _evidence, _requires = infer_kind_status(msg)
    raw = "|".join([
        stable_message_key(msg),
        normalize_direction(_get(msg, "direction")),
        str(_get(msg, "type", "message_type", "original_type") or "unknown"),
        str(text_fingerprint(msg) or ""),
        str(_get(msg, "timestamp", "time") or ""),
        kind.value,
        status.value,
    ])
    return "obs:" + sha256_text(raw)


def match_candidate_rule(msg: Dict[str, Any]) -> Optional[Dict[str, Any]]:
    text = str(_get(msg, "text", "body", "content", "name") or "")
    for rule in CANDIDATE_RULES:
        for phrase in rule["phrases"]:
            if phrase in text:
                return {
                    "matched_rule_id": rule["id"],
                    "evidence_source": "redacted_text_phrase_candidate",
                    "confidence": rule["confidence"],
                    "validation_state": "requires_local_validation",
                    "matched_phrase_hash": sha256_text(phrase)[:16],
                }
    return None


def infer_kind_status(msg: Dict[str, Any]) -> Tuple[LifecycleKind, LifecycleStatus, float, List[Dict[str, Any]], bool]:
    evidence: List[Dict[str, Any]] = []
    rule = match_candidate_rule(msg)
    if rule:
        evidence.append(rule)
        rid = rule["matched_rule_id"]
        for r in CANDIDATE_RULES:
            if r["id"] == rid:
                return r["kind"], r["status"], r["confidence"], evidence, True
    direction = normalize_direction(_get(msg, "direction"))
    typ = str(_get(msg, "type", "message_type", "original_type") or "text").lower()
    if "image" in typ:
        return LifecycleKind.IMAGE, LifecycleStatus.OBSERVED, 0.55, evidence, False
    if "product" in typ or "item" in typ:
        return LifecycleKind.PRODUCT, LifecycleStatus.OBSERVED, 0.55, evidence, False
    if direction == "incoming" and text_fingerprint(msg):
        return LifecycleKind.USER_TEXT, LifecycleStatus.OBSERVED, 0.70, evidence, False
    if direction == "outgoing" and text_fingerprint(msg):
        return LifecycleKind.SELLER_TEXT, LifecycleStatus.OBSERVED, 0.70, evidence, False
    return LifecycleKind.UNKNOWN, LifecycleStatus.UNKNOWN, 0.20, evidence, True


@dataclass
class LifecycleRecord:
    message_key: str
    observation_key: str
    direction: str
    original_type: str
    lifecycle_kind: str
    lifecycle_status: str
    actionable: bool = False
    ignore_reason: str = "fail_closed"
    evidence: List[Dict[str, Any]] = field(default_factory=list)
    confidence: float = 0.0
    requires_local_validation: bool = True
    first_seen_at: str = field(default_factory=utc_now)
    last_seen_at: str = field(default_factory=utc_now)
    observation_count: int = 1
    content_hash: Optional[str] = None
    seen_actionable: bool = False
    history_initial: bool = False
    node_identity_hash: Optional[str] = None

    def public_dict(self) -> Dict[str, Any]:
        data = asdict(self)
        return data


@dataclass
class LifecycleConfig:
    stable_observation_count: int = 2
    processed_message_keys: set = field(default_factory=set)
    history_initial_keys: set = field(default_factory=set)


class LifecycleStateError(RuntimeError):
    pass


class LifecycleState:
    def __init__(self, records: Optional[Dict[str, LifecycleRecord]] = None, processed: Optional[Iterable[str]] = None):
        self.records = records or {}
        self.processed_message_keys = set(processed or [])

    @classmethod
    def load(cls, path: str) -> "LifecycleState":
        try:
            with open(path, "r", encoding="utf-8") as f:
                data = json.load(f)
        except Exception as exc:
            raise LifecycleStateError(f"failed to load lifecycle state; fail closed: {exc}") from exc
        if data.get("schema_version") != SCHEMA_VERSION:
            raise LifecycleStateError("unsupported lifecycle state schema; fail closed")
        records = {k: LifecycleRecord(**v) for k, v in data.get("records", {}).items()}
        return cls(records, data.get("processed_message_keys", []))

    def save_atomic(self, path: str) -> None:
        data = {"schema_version": SCHEMA_VERSION, "records": {k: v.public_dict() for k, v in self.records.items()}, "processed_message_keys": sorted(self.processed_message_keys)}
        directory = os.path.dirname(os.path.abspath(path)) or "."
        fd, tmp = tempfile.mkstemp(prefix=".lifecycle.", suffix=".json", dir=directory, text=True)
        try:
            with os.fdopen(fd, "w", encoding="utf-8") as f:
                json.dump(data, f, ensure_ascii=False, indent=2, sort_keys=True)
                f.write("\n")
            os.replace(tmp, path)
        finally:
            if os.path.exists(tmp):
                os.unlink(tmp)


def record_from_message(msg: Dict[str, Any], now: Optional[str] = None) -> LifecycleRecord:
    now = now or utc_now()
    kind, status, conf, evidence, requires = infer_kind_status(msg)
    return LifecycleRecord(
        message_key=stable_message_key(msg), observation_key=observation_key(msg), direction=normalize_direction(_get(msg, "direction")),
        original_type=str(_get(msg, "type", "message_type", "original_type") or "unknown"), lifecycle_kind=kind.value,
        lifecycle_status=status.value, evidence=evidence, confidence=conf, requires_local_validation=requires,
        first_seen_at=now, last_seen_at=now, content_hash=text_fingerprint(msg), history_initial=bool(msg.get("history_initial", False)),
        node_identity_hash=node_identity_hash(msg),
    )


def is_verified_qn99756n_buyer_withdrawal_update(old: LifecycleRecord, rec: LifecycleRecord, current_content_hashes: Iterable[Optional[str]]) -> bool:
    """Detect the verified Qianniu 9.97.56N in-place buyer withdrawal shape.

    Verified local evidence showed the same ``message_key`` changing in place
    from an incoming text message to a system withdrawal notice, with a changed
    text fingerprint and the original text fingerprint absent from the after
    snapshot.  Standalone withdrawal notices remain candidates only.
    """
    if old.message_key != rec.message_key:
        return False
    if old.direction != "incoming" or old.lifecycle_kind != LifecycleKind.USER_TEXT.value:
        return False
    if old.node_identity_hash != rec.node_identity_hash:
        return False
    if rec.original_type.lower() != "system":
        return False
    if rec.lifecycle_kind != LifecycleKind.WITHDRAWAL_CANDIDATE.value:
        return False
    if not old.content_hash or not rec.content_hash or old.content_hash == rec.content_hash:
        return False
    if old.content_hash in set(h for h in current_content_hashes if h):
        return False
    return True


def apply_confirmed_buyer_withdrawal(rec: LifecycleRecord) -> None:
    rec.lifecycle_status = LifecycleStatus.CONFIRMED_WITHDRAWN.value
    rec.actionable = False
    rec.ignore_reason = "buyer_withdrawn"
    rec.confidence = 0.95
    rec.requires_local_validation = False
    rec.evidence.append({
        "matched_rule_id": "withdrawal.buyer.in_place_qn99756n",
        "evidence_source": "in_place_same_message_key_update",
        "confidence": "high",
        "validation_state": "verified_qn_9_97_56n",
        "requires_local_validation": False,
    })


def evaluate_actionable(rec: LifecycleRecord, config: LifecycleConfig, state: LifecycleState) -> None:
    rec.actionable = False
    rec.ignore_reason = "fail_closed"
    if rec.direction != "incoming": rec.ignore_reason = "not_incoming"; return
    if rec.lifecycle_kind != LifecycleKind.USER_TEXT.value: rec.ignore_reason = "not_user_text"; return
    if rec.lifecycle_status != LifecycleStatus.STABLE.value: rec.ignore_reason = "not_stable"; return
    if not rec.content_hash: rec.ignore_reason = "missing_body_hash"; return
    if rec.observation_count < config.stable_observation_count: rec.ignore_reason = "insufficient_observations"; return
    if rec.message_key in config.processed_message_keys or rec.message_key in state.processed_message_keys or rec.seen_actionable: rec.ignore_reason = "already_seen"; return
    if rec.history_initial or rec.message_key in config.history_initial_keys: rec.ignore_reason = "history_initial"; return
    rec.actionable = True
    rec.ignore_reason = ""
    rec.seen_actionable = True
    state.processed_message_keys.add(rec.message_key)


def correlate_candidate(rec: LifecycleRecord, records: Iterable[LifecycleRecord]) -> Optional[Dict[str, Any]]:
    if rec.lifecycle_kind == LifecycleKind.WITHDRAWAL_CANDIDATE.value:
        target_dir = "outgoing" if any(e.get("matched_rule_id") == "withdrawal.seller" for e in rec.evidence) else "incoming"
        candidates = [r for r in records if r.direction == target_dir and r.lifecycle_kind in (LifecycleKind.USER_TEXT.value, LifecycleKind.SELLER_TEXT.value)]
        if candidates:
            return {"type": "correlation_candidate", "candidate_event_key": rec.message_key, "target_message_key_hash": sha256_text(candidates[-1].message_key)[:16], "basis": f"recent_{target_dir}_text_before_candidate", "confidence": 0.30, "requires_local_validation": True}
    if rec.lifecycle_kind == LifecycleKind.SEND_FAILURE_CANDIDATE.value:
        candidates = [r for r in records if r.direction == "outgoing"]
        if candidates:
            return {"type": "correlation_candidate", "candidate_event_key": rec.message_key, "target_message_key_hash": sha256_text(candidates[-1].message_key)[:16], "basis": "recent_outgoing_before_failure_candidate", "confidence": 0.30, "requires_local_validation": True}
    return None


def compare_snapshot(previous: LifecycleState, messages: List[Dict[str, Any]], config: Optional[LifecycleConfig] = None, now: Optional[str] = None) -> Dict[str, Any]:
    config = config or LifecycleConfig()
    now = now or utc_now()
    current: Dict[str, LifecycleRecord] = {}
    added=[]; updated=[]; reobserved=[]; unchanged=[]; candidate_events=[]
    current_content_hashes = [text_fingerprint(msg) for msg in messages]
    for msg in messages:
        rec = record_from_message(msg, now)
        old = previous.records.get(rec.message_key)
        if old:
            was_not = old.lifecycle_status == LifecycleStatus.NOT_OBSERVED.value
            changed = old.observation_key != rec.observation_key
            rec.first_seen_at = old.first_seen_at; rec.observation_count = old.observation_count + 1; rec.seen_actionable = old.seen_actionable
            confirmed_buyer_withdrawal = is_verified_qn99756n_buyer_withdrawal_update(old, rec, current_content_hashes)
            if confirmed_buyer_withdrawal:
                apply_confirmed_buyer_withdrawal(rec)
            elif rec.lifecycle_status == LifecycleStatus.OBSERVED.value and rec.observation_count >= config.stable_observation_count:
                rec.lifecycle_status = LifecycleStatus.STABLE.value
            if not confirmed_buyer_withdrawal:
                evaluate_actionable(rec, config, previous)
            (reobserved if was_not else updated if changed else unchanged).append(rec.public_dict())
            if old.node_identity_hash != rec.node_identity_hash and old.content_hash == rec.content_hash:
                candidate_events.append({"type":"identity_change_candidate","message_key_hash":sha256_text(rec.message_key)[:16],"basis":"node_identity_changed_content_hash_same","confidence":0.35,"requires_local_validation":True})
        else:
            evaluate_actionable(rec, config, previous)
            added.append(rec.public_dict())
        current[rec.message_key] = rec
        corr = correlate_candidate(rec, list(previous.records.values()) + list(current.values()))
        if rec.requires_local_validation or corr:
            candidate_events.append({"message_key_hash": sha256_text(rec.message_key)[:16], "lifecycle_kind": rec.lifecycle_kind, "lifecycle_status": rec.lifecycle_status, "evidence": rec.evidence, "requires_local_validation": rec.requires_local_validation})
            if corr: candidate_events.append(corr)
    not_observed=[]
    for key, old in previous.records.items():
        if key not in current:
            rec = LifecycleRecord(**old.public_dict())
            rec.lifecycle_status = LifecycleStatus.NOT_OBSERVED.value
            rec.actionable = False; rec.ignore_reason = "not_observed_not_withdrawn"; rec.last_seen_at = now
            not_observed.append(rec.public_dict()); current[key] = rec
    previous.records = current
    return {"added": added, "updated": updated, "reobserved": reobserved, "not_observed": not_observed, "unchanged": unchanged, "candidate_events": candidate_events}


def load_messages(path: str) -> List[Dict[str, Any]]:
    with open(path, "r", encoding="utf-8") as f:
        data = json.load(f)
    if isinstance(data, list): return data
    for key in ("messages", "items", "results"):
        if isinstance(data.get(key), list): return data[key]
    raise ValueError("snapshot does not contain a message list")


def redacted_evidence_bundle(before_path: str, after_path: str, scenario: str = "") -> Dict[str, Any]:
    before = LifecycleState(); compare_snapshot(before, load_messages(before_path))
    changes = compare_snapshot(before, load_messages(after_path))
    return {"schema_version": EVIDENCE_SCHEMA_VERSION, "scenario": scenario, "before": {"path_hash": sha256_text(os.path.abspath(before_path))[:16]}, "after": {"path_hash": sha256_text(os.path.abspath(after_path))[:16]}, "changes": {"added": changes["added"], "updated": changes["updated"], "reobserved": changes["reobserved"], "not_observed": changes["not_observed"], "unchanged_count": len(changes["unchanged"])}, "candidate_events": changes["candidate_events"], "safety": {"read_only": True, "raw_text_printed": False, "raw_sender_printed": False}, "requires_local_validation": True}
