#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Read-only geometry diagnostics for one matched Qianniu message node.

The script never writes, clicks, scrolls, sends keys, or changes conversations.
It does not print raw chat text, sender names, or raw AutomationIds. Names and
AutomationIds are represented only by hashes, lengths, and match flags.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from typing import Any

import uiautomation as auto

from qn_uia_extract_messages import (
    MESSAGE_LIST_ID,
    control_automation_id,
    find_message_nodes,
    iter_tree,
    rect_tuple,
    safe_attr,
    safe_text,
)
from qn_uia_send_probe import (
    DOC_NAME,
    WINDOW_CLASS,
    WINDOW_NAME,
    configure_console,
    fail,
    require,
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Inspect redacted UIA geometry for an expected chat message",
    )
    parser.add_argument(
        "--expect-message",
        required=True,
        help="Exact message text to locate; never printed",
    )
    parser.add_argument(
        "--max-depth",
        type=int,
        default=12,
        help="Maximum traversal depth inside the matched message; default 12",
    )
    parser.add_argument(
        "--max-nodes",
        type=int,
        default=500,
        help="Maximum controls reported inside the matched message; default 500",
    )
    return parser.parse_args()


def digest(value: str) -> str:
    if not value:
        return ""
    return hashlib.sha256(value.encode("utf-8", errors="replace")).hexdigest()[:12]


def rectangle_payload(control: auto.Control) -> list[int] | None:
    rect = rect_tuple(control)
    return list(rect) if rect is not None else None


def describe_control(
    control: auto.Control,
    *,
    depth: int,
    expected: str,
) -> dict[str, Any]:
    name = safe_text(safe_attr(control, "Name", ""))
    automation_id = control_automation_id(control)
    rect = rect_tuple(control)
    width = max(0, rect[2] - rect[0]) if rect is not None else None
    height = max(0, rect[3] - rect[1]) if rect is not None else None

    return {
        "depth": depth,
        "control_type": safe_text(safe_attr(control, "ControlTypeName", "")),
        "class_name": safe_text(safe_attr(control, "ClassName", "")),
        "rectangle": list(rect) if rect is not None else None,
        "width": width,
        "height": height,
        "is_offscreen": bool(safe_attr(control, "IsOffscreen", False)),
        "name_present": bool(name),
        "name_chars": len(name),
        "name_sha256": digest(name),
        "contains_expected": bool(expected and expected in name),
        "automation_id_present": bool(automation_id),
        "automation_id_chars": len(automation_id),
        "automation_id_sha256": digest(automation_id),
        "is_pnm_node": automation_id.endswith(".PNM"),
    }


def node_contains_expected(node: auto.Control, expected: str) -> bool:
    for control, _depth in iter_tree(
        node,
        max_depth=12,
        max_nodes=500,
        include_root=True,
    ):
        name = safe_text(safe_attr(control, "Name", ""))
        if expected in name:
            return True
    return False


def main() -> int:
    configure_console()
    args = parse_args()

    if not args.expect_message.strip():
        fail("expect-message is empty", 1)
    if args.max_depth <= 0:
        fail("max-depth must be positive", 1)
    if args.max_nodes <= 0:
        fail("max-nodes must be positive", 1)

    auto.SetGlobalSearchTimeout(8)
    window = require(
        auto.WindowControl(
            searchDepth=1,
            Name=WINDOW_NAME,
            ClassName=WINDOW_CLASS,
        ),
        "Qianniu reception window",
    )
    document = require(
        window.DocumentControl(searchDepth=40, Name=DOC_NAME),
        "chat document",
    )
    message_list = require(
        auto.Control(
            searchFromControl=document,
            searchDepth=40,
            AutomationId=MESSAGE_LIST_ID,
        ),
        "message list J_msg_list",
    )

    nodes, used_fallback = find_message_nodes(
        message_list,
        max_depth=30,
        max_nodes=8000,
    )
    matched_nodes = [
        node for node in nodes if node_contains_expected(node, args.expect_message)
    ]

    reports: list[dict[str, Any]] = []
    for node in matched_nodes:
        controls: list[dict[str, Any]] = []
        for control, depth in iter_tree(
            node,
            max_depth=args.max_depth,
            max_nodes=args.max_nodes,
            include_root=True,
        ):
            item = describe_control(
                control,
                depth=depth,
                expected=args.expect_message,
            )
            if (
                item["rectangle"] is not None
                or item["name_present"]
                or item["automation_id_present"]
            ):
                controls.append(item)

        reports.append(
            {
                "node_rectangle": rectangle_payload(node),
                "node_is_offscreen": bool(safe_attr(node, "IsOffscreen", False)),
                "controls": controls,
            }
        )

    payload = {
        "schema_version": "qn_uia_message_geometry.v1",
        "safety": {
            "read_only": True,
            "raw_chat_text_printed": False,
            "raw_sender_printed": False,
            "raw_automation_ids_printed": False,
            "input_written": False,
            "control_invoked": False,
            "keys_sent": False,
            "mouse_clicked": False,
            "scrolled": False,
            "conversation_changed": False,
        },
        "expected_sha256": digest(args.expect_message),
        "message_list_rectangle": rectangle_payload(message_list),
        "loaded_message_node_count": len(nodes),
        "matched_node_count": len(matched_nodes),
        "used_direct_child_fallback": used_fallback,
        "matched_nodes": reports,
    }
    print(json.dumps(payload, ensure_ascii=False, indent=2))
    return 0 if matched_nodes else 5


if __name__ == "__main__":
    raise SystemExit(main())
