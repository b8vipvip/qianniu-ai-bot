#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Guarded Qianniu UI Automation send probe.

Dry-run is the default. A real send requires both:

    --send --confirm SEND_TO_CURRENT_CHAT

The probe invokes the structured UIA send button exactly once and never retries.
"""

from __future__ import annotations

import argparse
import sys
import time
from typing import Iterable

import uiautomation as auto

WINDOW_NAME = "千牛接待台"
WINDOW_CLASS = "MutilChatView"
DOC_NAME = "千牛消息聊天"
CONFIRM_TOKEN = "SEND_TO_CURRENT_CHAT"

INPUT_ID = (
    "UIWindow.mutilcentralwidget.stackedWidget.SingleChatView.centralwidget."
    "stackedWidget.SubChatView.ChatDisplayWidget.ChatContentView.splitter."
    "sendMsgWidget.chatInputArea.plainTextEdit"
)
SEND_ID = (
    "UIWindow.mutilcentralwidget.stackedWidget.SingleChatView.centralwidget."
    "stackedWidget.SubChatView.ChatDisplayWidget.ChatContentView.splitter."
    "sendMsgWidget.enterAreaKeyWidget.sendMsg"
)


def configure_console() -> None:
    """Avoid crashes or lost output when control names contain unusual glyphs."""
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure:
            reconfigure(encoding="utf-8", errors="replace")


def fail(message: str, code: int) -> "NoReturn":
    print(f"[FAIL] {message}")
    raise SystemExit(code)


def require(control: auto.Control, label: str) -> auto.Control:
    if not control.Exists(8, 0.5):
        fail(f"{label} not found", 2)
    return control


def walk_names(
    root: auto.Control,
    *,
    max_depth: int = 30,
    max_nodes: int = 6000,
) -> Iterable[str]:
    """Yield UIA names without dumping the entire private conversation."""
    stack: list[tuple[auto.Control, int]] = [(root, 0)]
    visited = 0

    while stack and visited < max_nodes:
        control, depth = stack.pop()
        visited += 1

        try:
            name = control.Name
        except Exception:
            name = ""

        if name:
            yield name

        if depth >= max_depth:
            continue

        try:
            children = control.GetChildren()
        except Exception:
            children = []

        for child in reversed(children):
            stack.append((child, depth + 1))


def contains_message(document: auto.Control, message: str) -> bool:
    return any(message in name for name in walk_names(document))


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Guarded Qianniu UIA send probe",
    )
    parser.add_argument("--message", required=True, help="Text to place in the current chat")
    parser.add_argument(
        "--send",
        action="store_true",
        help="Actually invoke the send button; omitted means safe dry-run",
    )
    parser.add_argument(
        "--confirm",
        default="",
        help=f"Required real-send token: {CONFIRM_TOKEN}",
    )
    parser.add_argument(
        "--countdown",
        type=int,
        default=3,
        help="Seconds before the single send invocation",
    )
    parser.add_argument(
        "--verify-seconds",
        type=float,
        default=10.0,
        help="How long to look for the sent text in the UIA tree",
    )
    parser.add_argument(
        "--preview",
        type=int,
        default=0,
        help="Print only the last N UIA names before writing; default 0",
    )
    return parser.parse_args()


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

    if args.preview > 0:
        names = list(walk_names(document))
        print(f"[CHAT PREVIEW] last {min(args.preview, len(names))} UIA names")
        for name in names[-args.preview :]:
            print(" ", repr(name[:300]))

    value_pattern = edit.GetValuePattern()
    if value_pattern is None:
        fail("chat input does not expose ValuePattern", 3)

    old_value = value_pattern.Value or ""
    print("[INFO] old input:", repr(old_value))

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
            print("[DRY RUN] message restored; nothing sent")
            print("[INFO] restored :", repr(restored))
            return 0

        if args.confirm != CONFIRM_TOKEN:
            value_pattern.SetValue(old_value)
            print(f"[BLOCKED] real send requires --confirm {CONFIRM_TOKEN}")
            print("[SAFE] original input restored; nothing sent")
            return 4

        print("[ARMED] sending once to the CURRENTLY SELECTED chat")
        for remaining in range(args.countdown, 0, -1):
            print("[SEND IN]", remaining)
            time.sleep(1)

        window.SetActive()
        edit.SetFocus()
        time.sleep(0.2)

        invoke_pattern = send_button.GetInvokePattern()
        if invoke_pattern is None:
            value_pattern.SetValue(old_value)
            fail("send button does not expose InvokePattern", 4)

        invoke_pattern.Invoke()
        print("[ACTION] send button invoked exactly once")

    except SystemExit:
        try:
            current = edit.GetValuePattern().Value or ""
            if current == message:
                edit.GetValuePattern().SetValue(old_value)
        except Exception:
            pass
        raise
    except Exception as exc:
        try:
            current = edit.GetValuePattern().Value or ""
            if current == message:
                edit.GetValuePattern().SetValue(old_value)
        except Exception:
            pass
        fail(f"unexpected UIA error before/while sending: {exc!r}", 7)

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

    print("[FAIL] send could not be verified")
    print("[SAFE] no retry was attempted")
    return 6


if __name__ == "__main__":
    raise SystemExit(main())
