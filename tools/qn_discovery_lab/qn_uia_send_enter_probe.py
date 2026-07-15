#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Guarded Qianniu UIA send probe using one Enter keystroke.

Dry-run is the default. A real send requires all of:

    --send
    --confirm SEND_TO_CURRENT_CHAT
    --confirm-enter-mode ENTER_SEND_IS_SELECTED

The probe never invokes the split send button. It focuses the known chat input,
sends exactly one Enter keystroke, verifies the UIA tree, and never retries.
"""
from __future__ import annotations

import argparse
import sys
import time

import uiautomation as auto

from qn_uia_send_probe import (
    CONFIRM_TOKEN,
    DOC_NAME,
    INPUT_ID,
    SEND_ID,
    WINDOW_CLASS,
    WINDOW_NAME,
    configure_console,
    contains_message,
    fail,
    require,
)

ENTER_CONFIRM_TOKEN = "ENTER_SEND_IS_SELECTED"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Guarded Qianniu UIA Enter-key send probe",
    )
    parser.add_argument("--message", required=True, help="Text to place in the current chat")
    parser.add_argument(
        "--send",
        action="store_true",
        help="Actually send one Enter keystroke; omitted means safe dry-run",
    )
    parser.add_argument(
        "--confirm",
        default="",
        help=f"Required real-send token: {CONFIRM_TOKEN}",
    )
    parser.add_argument(
        "--confirm-enter-mode",
        default="",
        help=f"Confirm Qianniu is configured for Enter-to-send: {ENTER_CONFIRM_TOKEN}",
    )
    parser.add_argument(
        "--countdown",
        type=int,
        default=3,
        help="Seconds before the single Enter keystroke",
    )
    parser.add_argument(
        "--verify-seconds",
        type=float,
        default=10.0,
        help="How long to look for the sent text in the UIA tree",
    )
    return parser.parse_args()


def restore_if_unchanged(edit: auto.Control, message: str, old_value: str) -> None:
    try:
        pattern = edit.GetValuePattern()
        current = pattern.Value or ""
        if current == message:
            pattern.SetValue(old_value)
    except Exception:
        pass


def main() -> int:
    configure_console()
    args = parse_args()

    message = args.message
    if not message.strip():
        fail("message is empty", 1)
    if len(message) > 1000:
        fail(f"message is too long: {len(message)} characters", 1)
    if args.countdown < 0:
        fail("countdown cannot be negative", 1)
    if args.verify_seconds < 0:
        fail("verify-seconds cannot be negative", 1)

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
    edit = require(
        window.EditControl(searchDepth=40, AutomationId=INPUT_ID),
        "chat input",
    )
    send_button = require(
        window.ButtonControl(searchDepth=40, AutomationId=SEND_ID),
        "send button",
    )

    print("[FOUND] window:", window.Name)
    print("[FOUND] input :", edit.BoundingRectangle)
    print("[FOUND] send  :", send_button.BoundingRectangle, "name=", send_button.Name)
    print("[INFO] method : one Enter keystroke; split send button is not invoked")

    value_pattern = edit.GetValuePattern()
    if value_pattern is None:
        fail("chat input does not expose ValuePattern", 3)

    old_value = value_pattern.Value or ""
    print("[INFO] old input:", repr(old_value))

    if args.send and old_value:
        fail("real send requires the chat input to be empty before the probe", 4)

    try:
        value_pattern.SetValue(message)
        time.sleep(0.7)
        readback = edit.GetValuePattern().Value or ""
        print("[INFO] readback :", repr(readback))

        if readback != message:
            fail("input write/read mismatch", 3)

        if not args.send:
            value_pattern.SetValue(old_value)
            restored = edit.GetValuePattern().Value or ""
            print("[DRY RUN] message restored; no key sent and nothing sent")
            print("[INFO] restored :", repr(restored))
            return 0

        if args.confirm != CONFIRM_TOKEN:
            value_pattern.SetValue(old_value)
            print(f"[BLOCKED] real send requires --confirm {CONFIRM_TOKEN}")
            print("[SAFE] original input restored; nothing sent")
            return 4

        if args.confirm_enter_mode != ENTER_CONFIRM_TOKEN:
            value_pattern.SetValue(old_value)
            print(
                "[BLOCKED] real send requires --confirm-enter-mode "
                f"{ENTER_CONFIRM_TOKEN}"
            )
            print("[SAFE] original input restored; nothing sent")
            return 4

        print("[ARMED] sending once to the CURRENTLY SELECTED chat")
        print("[ARMED] user confirmed Qianniu is configured for Enter-to-send")
        for remaining in range(args.countdown, 0, -1):
            print("[SEND IN]", remaining)
            time.sleep(1)

        window.SetActive()
        edit.SetFocus()
        time.sleep(0.3)

        focused = bool(edit.HasKeyboardFocus)
        print("[INFO] input has keyboard focus:", focused)
        if not focused:
            value_pattern.SetValue(old_value)
            fail("chat input did not obtain keyboard focus", 4)

        final_readback = edit.GetValuePattern().Value or ""
        if final_readback != message:
            value_pattern.SetValue(old_value)
            fail("input changed before Enter send", 4)

    except SystemExit:
        restore_if_unchanged(edit, message, old_value)
        raise
    except Exception as exc:
        restore_if_unchanged(edit, message, old_value)
        fail(f"unexpected UIA error before sending: {exc!r}", 7)

    # From this point onward, a send attempt is considered made. Never restore or retry.
    try:
        auto.SendKeys("{Enter}", waitTime=0.05)
        print("[ACTION] Enter key sent exactly once")
    except Exception as exc:
        print(f"[FAIL] Enter send attempt raised: {exc!r}")
        print("[SAFE] no retry was attempted")
        return 7

    deadline = time.monotonic() + args.verify_seconds
    verified = False
    while time.monotonic() <= deadline:
        if contains_message(document, message):
            verified = True
            break
        time.sleep(0.5)

    try:
        current_value = edit.GetValuePattern().Value or ""
    except Exception:
        current_value = "<unavailable>"

    print("[INFO] input after send:", repr(current_value))

    if verified:
        print("[PASS] sent text found in the chat UIA tree")
        return 0

    if current_value == "":
        print("[WARN] input cleared, but sent text was not found before timeout")
        print("[SAFE] no retry was attempted")
        return 5

    print("[FAIL] Enter send could not be verified")
    print("[SAFE] no retry was attempted")
    return 6


if __name__ == "__main__":
    raise SystemExit(main())
