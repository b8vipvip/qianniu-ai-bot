#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Withdraw exactly one current-test Qianniu message through live UIA controls.

The probe is dry-run by default. A real withdrawal requires the exact message
substring, the previously captured privacy-safe message key and node identity,
``--withdraw``, and the exact confirmation token. It opens the context menu on
the live body anchor of that one node and invokes one withdrawal menu item once.
It never retries and never falls back to a row, adjacent message, fixed screen
coordinate, or message text count.
"""
from __future__ import annotations

import argparse
import sys
import time
from typing import Any

import uiautomation as auto

import qn_uia_extract_messages as extractor
import qn_uia_extract_messages_core as core

CONFIRM_TOKEN = "WITHDRAW_EXACT_CURRENT_TEST_MESSAGE"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--expect-message", required=True)
    parser.add_argument("--expected-message-key", required=True)
    parser.add_argument("--expected-node-identity-hash", required=True)
    parser.add_argument("--withdraw", action="store_true")
    parser.add_argument("--confirm", default="")
    parser.add_argument("--countdown", type=int, default=3)
    parser.add_argument("--verify-seconds", type=float, default=8.0)
    return parser.parse_args()


def safe_identity(node: auto.Control) -> tuple[str, str, str]:
    node_id = core.control_automation_id(node)
    serialized = core.redact(node_id, "node_id")
    key, _source = extractor._stable_message_key({"node_id": serialized})
    return key, core.sha256_text(node_id) if node_id else "", node_id


def matches_expectation(node: auto.Control, expected: str) -> bool:
    fragments, _types = core.collect_fragments(node)
    return bool(expected) and any(expected in fragment for fragment in fragments)


def safe_target_metadata(
    node: auto.Control,
    message_list: auto.Control,
) -> dict[str, Any]:
    message, _matched = core.extract_message(
        node,
        message_list,
        include_sensitive=False,
        expect_message="",
    )
    key, node_hash, _node_id = safe_identity(node)
    return {
        "message_key": key,
        "node_identity_hash": node_hash,
        "content_hash": message.get("content_hash", ""),
        "direction": message.get("direction", ""),
        "observed_direction_guess": message.get("observed_direction_guess", ""),
        "type": message.get("type", ""),
        "original_type": message.get("original_type", ""),
        "semantic_flags": list(message.get("semantic_flags", [])),
        "visible": bool(message.get("visible", False)),
    }


def validate_expected_identity(
    metadata: dict[str, Any],
    expected_key: str,
    expected_node_hash: str,
) -> None:
    if metadata["message_key"] != expected_key:
        raise ValueError("target message_key differs from the captured expectation match")
    if metadata["node_identity_hash"] != expected_node_hash:
        raise ValueError("target node identity differs from the captured expectation match")
    if not metadata["visible"]:
        raise ValueError("target is offscreen; refusing to open a context menu")
    if metadata["type"] != "text" or metadata["original_type"] != "text":
        raise ValueError("target is not an ordinary text message before withdrawal")
    if "withdrawal_notice" in metadata["semantic_flags"]:
        raise ValueError("target is already a withdrawal notice")


def find_withdraw_item(process_ids: set[int]) -> auto.Control | None:
    root = auto.GetRootControl()
    for top_level in root.GetChildren():
        if int(core.safe_attr(top_level, "ProcessId", 0) or 0) not in process_ids:
            continue
        for control, _depth in core.iter_tree(
            top_level, max_depth=10, max_nodes=1200, include_root=True
        ):
            if core.safe_attr(control, "ControlType", 0) != auto.ControlType.MenuItemControl:
                continue
            name = core.safe_text(core.safe_attr(control, "Name", "")).strip()
            if name not in {"撤回", "撤回消息"}:
                continue
            if bool(core.safe_attr(control, "IsOffscreen", True)):
                continue
            return control
    return None


def main() -> int:
    core.configure_console()
    core.classify_direction = extractor.classify_direction
    core.classify_direction_details = extractor.classify_direction_details
    args = parse_args()
    if args.countdown < 0 or args.verify_seconds < 0:
        print("[BLOCKED] countdown and verify-seconds must be non-negative")
        return 1

    auto.SetGlobalSearchTimeout(8)
    window = core.require(
        auto.WindowControl(searchDepth=1, Name=core.WINDOW_NAME, ClassName=core.WINDOW_CLASS),
        "Qianniu reception window",
    )
    document = core.require(
        window.DocumentControl(searchDepth=40, Name=core.DOC_NAME), "chat document"
    )
    message_list = core.require(
        auto.Control(
            searchFromControl=document,
            searchDepth=40,
            AutomationId=core.MESSAGE_LIST_ID,
        ),
        "message list J_msg_list",
    )
    nodes, _fallback = core.find_message_nodes(message_list, max_depth=30, max_nodes=8000)
    matched = [node for node in nodes if matches_expectation(node, args.expect_message)]
    if len(matched) != 1:
        print(f"[BLOCKED] expected exactly one live matching node; found {len(matched)}")
        return 2

    node = matched[0]
    metadata = safe_target_metadata(node, message_list)
    try:
        validate_expected_identity(
            metadata, args.expected_message_key, args.expected_node_identity_hash
        )
    except ValueError as exc:
        print(f"[BLOCKED] {exc}")
        return 3

    print("[PASS] unique live node matches captured message key and node identity")
    print(
        "[SAFE] direction={direction} type={type} visible={visible}".format(**metadata)
    )
    if not args.withdraw:
        print("[DRY RUN] context menu was not opened and nothing was withdrawn")
        return 0
    if args.confirm != CONFIRM_TOKEN:
        print(f"[BLOCKED] real withdrawal requires --confirm {CONFIRM_TOKEN}")
        return 4

    fragments, _control_types = core.collect_fragments(node)
    timestamp, timestamp_index = core.find_timestamp(fragments)
    sender = core.choose_sender(fragments, timestamp, timestamp_index)
    body = core.choose_body(fragments, sender, timestamp, timestamp_index)
    body_rect = extractor._find_body_anchor(node, message_list, body)
    if not extractor._valid_rect(body_rect):
        print("[BLOCKED] no live body anchor; refusing row or fixed-coordinate fallback")
        return 5

    for remaining in range(args.countdown, 0, -1):
        print(f"[WITHDRAW IN] {remaining}")
        time.sleep(1)

    x = int((body_rect[0] + body_rect[2]) / 2)
    y = int((body_rect[1] + body_rect[3]) / 2)
    node.RightClick(x=x, y=y, simulateMove=False, waitTime=0.5)
    process_ids = {
        int(core.safe_attr(window, "ProcessId", 0) or 0),
        int(core.safe_attr(node, "ProcessId", 0) or 0),
    }
    withdraw_item = find_withdraw_item(process_ids)
    if withdraw_item is None:
        print("[BLOCKED] target context menu has no visible withdrawal item")
        auto.SendKeys("{Esc}", waitTime=0.1)
        return 6

    invoke = withdraw_item.GetInvokePattern()
    if invoke is None:
        print("[BLOCKED] withdrawal item has no InvokePattern")
        auto.SendKeys("{Esc}", waitTime=0.1)
        return 7
    invoke.Invoke()
    print("[ACTION] withdrawal menu item invoked exactly once; no retry")

    deadline = time.monotonic() + args.verify_seconds
    while time.monotonic() <= deadline:
        current_nodes, _fallback = core.find_message_nodes(
            message_list, max_depth=30, max_nodes=8000
        )
        same_node = [item for item in current_nodes if safe_identity(item)[1] == metadata["node_identity_hash"]]
        if len(same_node) == 1:
            after = safe_target_metadata(same_node[0], message_list)
            if (
                after["message_key"] == metadata["message_key"]
                and after["content_hash"] != metadata["content_hash"]
                and "withdrawal_notice" in after["semantic_flags"]
            ):
                print("[PASS] same node changed in place to a withdrawal notice")
                return 0
        time.sleep(0.5)

    print("[FAIL] withdrawal could not be verified on the same node; no retry performed")
    return 8


if __name__ == "__main__":
    raise SystemExit(main())
