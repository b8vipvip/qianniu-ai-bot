#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Validate Qianniu message direction using body-control geometry.

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

        # Prefer an exact body-bearing descendant. For ties, prefer deeper and
        # more substantial controls while avoiding giant row-wide containers.
        rank = (match_score, depth, min(width, 1200), min(height, 600))
        candidates.append((rank, rect))

    if not candidates:
        return None

    candidates.sort(key=lambda item: item[0], reverse=True)
    return candidates[0][1]


def classify_direction(
    node: auto.Control,
    message_list: auto.Control,
    message_type: str,
) -> str:
    if message_type == "system":
        return "unknown"

    list_rect = base.rect_tuple(message_list)
    if not _valid_rect(list_rect):
        return "unknown"

    fragments, _control_types = base.collect_fragments(node)
    timestamp, timestamp_index = base.find_timestamp(fragments)
    sender = base.choose_sender(fragments, timestamp, timestamp_index)
    body = base.choose_body(fragments, sender, timestamp, timestamp_index)

    anchor_rect = _find_body_anchor(node, message_list, body)
    if not _valid_rect(anchor_rect):
        anchor_rect = base.rect_tuple(node)
    if not _valid_rect(anchor_rect):
        return "unknown"

    anchor_center = (anchor_rect[0] + anchor_rect[2]) / 2.0
    list_center = (list_rect[0] + list_rect[2]) / 2.0
    threshold = max(12.0, (list_rect[2] - list_rect[0]) * 0.05)

    if anchor_center > list_center + threshold:
        return "outgoing"
    if anchor_center < list_center - threshold:
        return "incoming"
    return "unknown"


def main() -> int:
    base.classify_direction = classify_direction
    return base.main()


if __name__ == "__main__":
    raise SystemExit(main())
