#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Compare two read-only Qianniu UIA snapshots by stable message key.

The probe launches qn_uia_extract_messages.py twice without clicking, scrolling,
writing, sending keys, or changing conversations. It prints only aggregate counts
and pass/fail state; chat text, sender names, node IDs, and message keys are not
printed.
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time
from pathlib import Path
from typing import Any

EXTRACTOR = Path(__file__).with_name("qn_uia_extract_messages.py")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Verify stable message keys across two read-only UIA snapshots",
    )
    parser.add_argument("--max-messages", type=int, default=100)
    parser.add_argument("--expect-message", default="")
    parser.add_argument("--delay-seconds", type=float, default=1.0)
    return parser.parse_args()


def fail(message: str, code: int = 1) -> None:
    print(json.dumps({"error": message}, ensure_ascii=False, indent=2))
    raise SystemExit(code)


def run_snapshot(args: argparse.Namespace) -> dict[str, Any]:
    command = [
        sys.executable,
        str(EXTRACTOR),
        "--max-messages",
        str(args.max_messages),
    ]
    if args.expect_message:
        command.extend(["--expect-message", args.expect_message])

    environment = dict(os.environ)
    environment["PYTHONUTF8"] = "1"
    completed = subprocess.run(
        command,
        cwd=str(EXTRACTOR.parent),
        env=environment,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=False,
    )
    if completed.returncode != 0:
        fail(
            f"extractor exited with {completed.returncode}; stderr chars={len(completed.stderr)}",
            4,
        )
    try:
        payload = json.loads(completed.stdout)
    except json.JSONDecodeError as exc:
        fail(f"extractor output is not JSON: {exc}", 4)
    if not isinstance(payload, dict):
        fail("extractor JSON root is not an object", 4)
    return payload


def message_keys(payload: dict[str, Any]) -> list[str]:
    messages = payload.get("messages", [])
    if not isinstance(messages, list):
        return []
    result: list[str] = []
    for message in messages:
        if not isinstance(message, dict):
            continue
        key = str(message.get("message_key", "")).strip()
        if key:
            result.append(key)
    return result


def snapshot_summary(payload: dict[str, Any]) -> dict[str, Any]:
    meta = payload.get("meta", {})
    if not isinstance(meta, dict):
        meta = {}
    expectation = payload.get("expectation", {})
    if not isinstance(expectation, dict):
        expectation = {}
    keys = message_keys(payload)
    return {
        "schema_version": payload.get("schema_version", ""),
        "message_count": len(keys),
        "unique_key_count": len(set(keys)),
        "raw_message_count": meta.get("raw_message_count", 0),
        "duplicate_count": meta.get("duplicate_count", 0),
        "fallback_key_count": meta.get("fallback_key_count", 0),
        "expectation_matched": expectation.get("matched", None),
        "expectation_match_count": expectation.get("match_count", None),
    }


def main() -> int:
    args = parse_args()
    if args.max_messages <= 0:
        fail("max-messages must be positive")
    if args.delay_seconds < 0 or args.delay_seconds > 30:
        fail("delay-seconds must be between 0 and 30")

    first = run_snapshot(args)
    if args.delay_seconds:
        time.sleep(args.delay_seconds)
    second = run_snapshot(args)

    first_keys = message_keys(first)
    second_keys = message_keys(second)
    first_set = set(first_keys)
    second_set = set(second_keys)

    schema_ok = (
        first.get("schema_version") == "qn_uia_messages.v2"
        and second.get("schema_version") == "qn_uia_messages.v2"
    )
    unique_ok = len(first_keys) == len(first_set) and len(second_keys) == len(second_set)
    stable_ok = first_keys == second_keys

    expectation_ok = True
    if args.expect_message:
        first_expectation = first.get("expectation", {})
        second_expectation = second.get("expectation", {})
        expectation_ok = bool(
            isinstance(first_expectation, dict)
            and isinstance(second_expectation, dict)
            and first_expectation.get("matched") is True
            and second_expectation.get("matched") is True
            and first_expectation.get("match_count") == 1
            and second_expectation.get("match_count") == 1
        )

    passed = schema_ok and unique_ok and stable_ok and expectation_ok
    output = {
        "schema_version": "qn_uia_dedup_probe.v1",
        "safety": {
            "read_only": True,
            "raw_chat_text_printed": False,
            "raw_sender_printed": False,
            "raw_node_ids_printed": False,
            "message_keys_printed": False,
            "input_written": False,
            "control_invoked": False,
            "keys_sent": False,
            "mouse_clicked": False,
            "scrolled": False,
            "conversation_changed": False,
        },
        "first": snapshot_summary(first),
        "second": snapshot_summary(second),
        "comparison": {
            "common_count": len(first_set & second_set),
            "added_count": len(second_set - first_set),
            "removed_count": len(first_set - second_set),
            "same_order": first_keys == second_keys,
            "schema_ok": schema_ok,
            "unique_keys_ok": unique_ok,
            "expectation_ok": expectation_ok,
        },
        "result": "PASS" if passed else "FAIL",
    }
    print(json.dumps(output, ensure_ascii=False, indent=2))
    return 0 if passed else 5


if __name__ == "__main__":
    raise SystemExit(main())
