#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Validate Qianniu message direction using avatar and body geometry.

This is a read-only compatibility wrapper around qn_uia_extract_messages.py.
It preserves the extractor's arguments, privacy defaults, and JSON schema, but
replaces only the direction classifier at runtime. The original extractor is
left unchanged until this algorithm is verified against real loaded messages.
"""
from __future__ import annotations

from typing import Iterable

import uiautomation as auto

import qn_uia_extract_messages as base


def _valid_rect(rect: tuple[int, int, int, int] | None) -> bool:
    return bool(rect and rect[2] > rect[0] and rect[3] > rect[1])


def _is_timestamp_only(value: str) -> bool:
    fragments = base.split_fragments(value)
    if not fragments:
        return False
    timestamp, _index = base.find_timestamp(fragments)
    return bool(timestamp and len(fragments) == 1 and timestamp == fragments[0])


def _body_fragments(body: str) -> list[str]:
    return [
        fragment
        for fragment in base.split_fragments(body)
        if fragment and not base.is_metadata_fragment(fragment, "")
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
    """Infer direction from a shallow square control touching a list edge.

    Qianniu's PNM node spans the full message row, but the avatar container is a
    shallow approximately square descendant aligned to the left or right edge.
    Small status/menu controls are excluded by the minimum size.
    """
    list_rect = base.rect_tuple(message_list)
    node_rect = base.rect_tuple(node)
    if not _valid_rect(list_rect) or not _valid_rect(node_rect):
        return None

    list_width = list_rect[2] - list_rect[0]
    edge_tolerance = max(10.0, list_width * 0.035)
    candidates: list[tuple[float, str]] = []

    for control, depth in base.iter_tree(
        node,
        max_depth=4,
        max_nodes=120,
        include_root=False,
    ):
        rect = base.rect_tuple(control)
        if not _valid_rect(rect):
            continue

        width = rect[2] - rect[0]
        height = rect[3] - rect[1]
        if not (24 <= width <= 72 and 24 <= height <= 72):
            continue

        aspect = max(width / height, height / width)
        if aspect > 1.75:
            continue

        # Ignore controls far outside the PNM row's vertical span.
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
    list_rect = base.rect_tuple(message_list)
    if not _valid_rect(list_rect):
        return None

    list_width = list_rect[2] - list_rect[0]
    fragments = _body_fragments(body)
    candidates: list[tuple[tuple[int, int, int, int], tuple[int, int, int, int]]] = []

    for control, depth in base.iter_tree(
        node,
        max_depth=12,
        max_nodes=500,
        include_root=False,
    ):
        rect = base.rect_tuple(control)
        if not _valid_rect(rect):
            continue

        width = rect[2] - rect[0]
        height = rect[3] - rect[1]
        if width > list_width * 0.92:
            continue

        name = base.safe_text(base.safe_attr(control, "Name", "")).strip()
        if not name or _is_timestamp_only(name):
            continue

        name_parts = base.split_fragments(name)
        if name_parts and all(base.is_metadata_fragment(part, "") for part in name_parts):
            continue

        match_score = _content_match_score(name, body, fragments)
        if match_score <= 0:
            continue

        # Exact body matches win. For ties, prefer a substantial control closer
        # to the row's vertical middle, not merely the deepest metadata child.
        node_rect = base.rect_tuple(node)
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

    fragments, _control_types = base.collect_fragments(node)
    timestamp, timestamp_index = base.find_timestamp(fragments)
    sender = base.choose_sender(fragments, timestamp, timestamp_index)
    body = base.choose_body(fragments, sender, timestamp, timestamp_index)

    list_rect = base.rect_tuple(message_list)
    body_rect = _find_body_anchor(node, message_list, body)
    direction = _direction_from_rect(body_rect, list_rect)
    if direction != "unknown":
        return direction

    return _direction_from_rect(base.rect_tuple(node), list_rect)


def main() -> int:
    base.classify_direction = classify_direction
    return base.main()


if __name__ == "__main__":
    raise SystemExit(main())
