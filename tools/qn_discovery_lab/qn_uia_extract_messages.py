#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Read loaded Qianniu messages with verified direction and stable keys.

This public entry point reuses the original read-only extractor core and replaces
only direction detection and final JSON normalization. Direction is inferred from
a shallow avatar control at the left or right edge of a PNM row, then from body
geometry, and finally from the row container.

Every message receives a privacy-safe stable key. The preferred source is the
message-node AutomationId; a content/time fingerprint is used only when that ID
is absent. Duplicate keys inside one snapshot are emitted once. CLI arguments,
privacy defaults, and read-only safety behavior remain unchanged.
"""
from __future__ import annotations

import hashlib
from typing import Any, Iterable

import uiautomation as auto

import qn_uia_extract_messages_core as core
from qn_uia_extract_messages_core import *  # noqa: F401,F403

SCHEMA_VERSION = "qn_uia_messages.v2"
_ORIGINAL_WRITE_JSON = core.write_json


def _valid_rect(rect: tuple[int, int, int, int] | None) -> bool:
    return bool(rect and rect[2] > rect[0] and rect[3] > rect[1])


def _is_timestamp_only(value: str) -> bool:
    fragments = core.split_fragments(value)
    if not fragments:
        return False
    timestamp, _index = core.find_timestamp(fragments)
    return bool(timestamp and len(fragments) == 1 and timestamp == fragments[0])


def _body_fragments(body: str) -> list[str]:
    return [
        fragment
        for fragment in core.split_fragments(body)
        if fragment and not core.is_metadata_fragment(fragment, "")
    ]


def _content_match_score(name: str, body: str, fragments: Iterable[str]) -> int:
    value = " ".join(name.split()).strip()
    if not value:
        return 0

    normalized_body = " ".join(body.split()).strip()
    if normalized_body and value == normalized_body:
        return 500

    best = 0
    for fragment in fragments:
        normalized_fragment = " ".join(fragment.split()).strip()
        if not normalized_fragment:
            continue
        if value == normalized_fragment:
            best = max(best, 400)
        elif normalized_fragment in value:
            best = max(best, 300)
        elif value in normalized_fragment and len(value) >= 2:
            best = max(best, 200)
    return best


def _find_avatar_side(
    node: auto.Control,
    message_list: auto.Control,
) -> str | None:
    """Infer direction from a shallow square control touching a list edge."""
    list_rect = core.rect_tuple(message_list)
    node_rect = core.rect_tuple(node)
    if not _valid_rect(list_rect) or not _valid_rect(node_rect):
        return None

    list_width = list_rect[2] - list_rect[0]
    edge_tolerance = max(10.0, list_width * 0.035)
    candidates: list[tuple[float, str]] = []

    for control, depth in core.iter_tree(
        node,
        max_depth=4,
        max_nodes=120,
        include_root=False,
    ):
        rect = core.rect_tuple(control)
        if not _valid_rect(rect):
            continue

        width = rect[2] - rect[0]
        height = rect[3] - rect[1]
        if not (24 <= width <= 72 and 24 <= height <= 72):
            continue

        aspect = max(width / height, height / width)
        if aspect > 1.75:
            continue

        if rect[3] < node_rect[1] - 4 or rect[1] > node_rect[3] + 4:
            continue

        left_gap = abs(rect[0] - list_rect[0])
        right_gap = abs(list_rect[2] - rect[2])
        nearest_gap = min(left_gap, right_gap)
        if nearest_gap > edge_tolerance:
            continue

        side = "incoming" if left_gap < right_gap else "outgoing"
        square_penalty = abs(width - height)
        size_penalty = abs(((width + height) / 2.0) - 36.0)
        depth_penalty = max(0, depth - 1) * 3.0
        score = 1000.0 - nearest_gap * 20.0 - square_penalty * 2.0
        score -= size_penalty + depth_penalty
        candidates.append((score, side))

    if not candidates:
        return None

    candidates.sort(key=lambda item: item[0], reverse=True)
    best_score, best_side = candidates[0]
    if len(candidates) > 1:
        second_score, second_side = candidates[1]
        if second_side != best_side and abs(best_score - second_score) < 25.0:
            return None
    return best_side


def _find_body_anchor(
    node: auto.Control,
    message_list: auto.Control,
    body: str,
) -> tuple[int, int, int, int] | None:
    list_rect = core.rect_tuple(message_list)
    if not _valid_rect(list_rect):
        return None

    list_width = list_rect[2] - list_rect[0]
    fragments = _body_fragments(body)
    candidates: list[tuple[tuple[int, int, int, int], tuple[int, int, int, int]]] = []

    for control, _depth in core.iter_tree(
        node,
        max_depth=12,
        max_nodes=500,
        include_root=False,
    ):
        rect = core.rect_tuple(control)
        if not _valid_rect(rect):
            continue

        width = rect[2] - rect[0]
        height = rect[3] - rect[1]
        if width > list_width * 0.92:
            continue

        name = core.safe_text(core.safe_attr(control, "Name", "")).strip()
        if not name or _is_timestamp_only(name):
            continue

        name_parts = core.split_fragments(name)
        if name_parts and all(core.is_metadata_fragment(part, "") for part in name_parts):
            continue

        match_score = _content_match_score(name, body, fragments)
        if match_score <= 0:
            continue

        node_rect = core.rect_tuple(node)
        if _valid_rect(node_rect):
            node_mid_y = (node_rect[1] + node_rect[3]) / 2.0
            control_mid_y = (rect[1] + rect[3]) / 2.0
            vertical_score = max(0, 1000 - int(abs(control_mid_y - node_mid_y) * 10))
        else:
            vertical_score = 0
        rank = (match_score, vertical_score, min(width, 1200), min(height, 600))
        candidates.append((rank, rect))

    if not candidates:
        return None

    candidates.sort(key=lambda item: item[0], reverse=True)
    return candidates[0][1]


def _direction_from_rect(
    anchor_rect: tuple[int, int, int, int] | None,
    list_rect: tuple[int, int, int, int] | None,
) -> str:
    if not _valid_rect(anchor_rect) or not _valid_rect(list_rect):
        return "unknown"

    anchor_center = (anchor_rect[0] + anchor_rect[2]) / 2.0
    list_center = (list_rect[0] + list_rect[2]) / 2.0
    threshold = max(12.0, (list_rect[2] - list_rect[0]) * 0.05)

    if anchor_center > list_center + threshold:
        return "outgoing"
    if anchor_center < list_center - threshold:
        return "incoming"
    return "unknown"


def classify_direction(
    node: auto.Control,
    message_list: auto.Control,
    message_type: str,
) -> str:
    if message_type == "system":
        return "unknown"

    avatar_side = _find_avatar_side(node, message_list)
    if avatar_side is not None:
        return avatar_side

    fragments, _control_types = core.collect_fragments(node)
    timestamp, timestamp_index = core.find_timestamp(fragments)
    sender = core.choose_sender(fragments, timestamp, timestamp_index)
    body = core.choose_body(fragments, sender, timestamp, timestamp_index)

    list_rect = core.rect_tuple(message_list)
    body_rect = _find_body_anchor(node, message_list, body)
    direction = _direction_from_rect(body_rect, list_rect)
    if direction != "unknown":
        return direction

    return _direction_from_rect(core.rect_tuple(node), list_rect)


def _digest(parts: Iterable[str]) -> str:
    material = "\x1f".join(str(part) for part in parts)
    return hashlib.sha256(material.encode("utf-8", errors="replace")).hexdigest()[:24]


def _stable_message_key(message: dict[str, Any]) -> tuple[str, str]:
    node_identity_hash = str(message.get("node_identity_hash", "")).strip()
    if node_identity_hash:
        return f"uia:{node_identity_hash[:24]}", "node_identity_hash"

    fallback_parts = [
        str(message.get("direction", "")),
        str(message.get("type", "")),
        str(message.get("sender", "")),
        str(message.get("timestamp", "")),
        str(message.get("content_hash", "")),
    ]
    return f"fallback:{_digest(fallback_parts)}", "content_time_fallback"


def _observation_key(message: dict[str, Any]) -> str:
    digest = _digest([
        str(message.get('message_key', '')),
        str(message.get('content_hash', '')),
        str(message.get('direction', '')),
        str(message.get('original_type', message.get('type', ''))),
        str(message.get('timestamp', '')),
        '|'.join(sorted(str(item) for item in message.get('semantic_flags', []))),
        str(message.get('lifecycle_kind', '')),
        str(message.get('lifecycle_status', '')),
    ])
    return f"obs:{digest}"


def _normalize_and_deduplicate(payload: dict[str, Any]) -> dict[str, Any]:
    raw_messages = payload.get("messages", [])
    if not isinstance(raw_messages, list):
        raw_messages = []

    seen: set[str] = set()
    messages: list[dict[str, Any]] = []
    duplicate_count = 0
    fallback_key_count = 0

    for raw_message in raw_messages:
        if not isinstance(raw_message, dict):
            continue
        message = dict(raw_message)
        key, key_source = _stable_message_key(message)
        message["message_key"] = key
        message["key_source"] = key_source
        message["observation_key"] = _observation_key(message)
        if key_source == "content_time_fallback":
            fallback_key_count += 1
        if key in seen:
            duplicate_count += 1
            continue
        seen.add(key)
        messages.append(message)

    payload["schema_version"] = SCHEMA_VERSION
    payload["messages"] = messages

    meta = payload.get("meta")
    if not isinstance(meta, dict):
        meta = {}
        payload["meta"] = meta
    meta["raw_message_count"] = len(raw_messages)
    meta["message_count"] = len(messages)
    meta["duplicate_count"] = duplicate_count
    meta["fallback_key_count"] = fallback_key_count
    meta["deduplication"] = {
        "enabled": True,
        "primary": "node_identity_hash",
        "fallback": "direction_type_sender_timestamp_content_hash",
        "excludes": ["visible", "bounds", "offscreen", "screen_coordinates"],
    }
    return payload


def write_json(payload: dict[str, Any], output: str) -> None:
    _ORIGINAL_WRITE_JSON(_normalize_and_deduplicate(payload), output)


def main() -> int:
    core.classify_direction = classify_direction
    core.write_json = write_json
    return core.main()


if __name__ == "__main__":
    raise SystemExit(main())
