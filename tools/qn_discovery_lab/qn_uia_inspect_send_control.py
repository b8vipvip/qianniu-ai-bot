#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Read-only diagnostics for the Qianniu send control.

This script never writes to the chat input, invokes a control, sends keys, or clicks.
It only reports UIA metadata, supported patterns, direct children, and siblings around
the known send control.
"""
from __future__ import annotations

import json
from typing import Any, Iterable

import uiautomation as auto

from qn_uia_send_probe import (
    SEND_ID,
    WINDOW_CLASS,
    WINDOW_NAME,
    configure_console,
    require,
)


def safe_attr(obj: Any, name: str, default: Any = None) -> Any:
    try:
        value = getattr(obj, name)
    except Exception as exc:
        return f"<error:{type(exc).__name__}>"

    if value is None or isinstance(value, (str, int, float, bool)):
        return value
    return str(value)


def describe_control(control: auto.Control) -> dict[str, Any]:
    return {
        "control_type": safe_attr(control, "ControlTypeName", ""),
        "name": safe_attr(control, "Name", ""),
        "automation_id": safe_attr(control, "AutomationId", ""),
        "class_name": safe_attr(control, "ClassName", ""),
        "rectangle": safe_attr(control, "BoundingRectangle", ""),
        "is_enabled": safe_attr(control, "IsEnabled"),
        "is_offscreen": safe_attr(control, "IsOffscreen"),
        "is_keyboard_focusable": safe_attr(control, "IsKeyboardFocusable"),
        "has_keyboard_focus": safe_attr(control, "HasKeyboardFocus"),
    }


def describe_controls(controls: Iterable[auto.Control]) -> list[dict[str, Any]]:
    result: list[dict[str, Any]] = []
    for control in controls:
        try:
            result.append(describe_control(control))
        except Exception as exc:
            result.append({"error": repr(exc)})
    return result


def inspect_pattern(
    control: auto.Control,
    getter_name: str,
    property_names: tuple[str, ...],
) -> dict[str, Any]:
    getter = getattr(control, getter_name, None)
    if getter is None:
        return {"getter": getter_name, "available": False, "supported": False}

    try:
        pattern = getter()
    except Exception as exc:
        return {
            "getter": getter_name,
            "available": True,
            "supported": False,
            "error": repr(exc),
        }

    if pattern is None:
        return {"getter": getter_name, "available": True, "supported": False}

    details: dict[str, Any] = {
        "getter": getter_name,
        "available": True,
        "supported": True,
        "pattern_type": type(pattern).__name__,
    }
    for property_name in property_names:
        details[property_name] = safe_attr(pattern, property_name)
    return details


def main() -> int:
    configure_console()
    auto.SetGlobalSearchTimeout(8)

    window = require(
        auto.WindowControl(
            searchDepth=1,
            Name=WINDOW_NAME,
            ClassName=WINDOW_CLASS,
        ),
        "Qianniu reception window",
    )
    send_button = require(
        window.ButtonControl(searchDepth=40, AutomationId=SEND_ID),
        "send button",
    )

    try:
        parent = send_button.GetParentControl()
    except Exception:
        parent = None

    try:
        children = send_button.GetChildren()
    except Exception:
        children = []

    if parent is not None:
        try:
            siblings = parent.GetChildren()
        except Exception:
            siblings = []
    else:
        siblings = []

    report = {
        "safety": {
            "read_only": True,
            "input_written": False,
            "control_invoked": False,
            "keys_sent": False,
            "mouse_clicked": False,
        },
        "send_control": describe_control(send_button),
        "patterns": [
            inspect_pattern(send_button, "GetInvokePattern", ()),
            inspect_pattern(
                send_button,
                "GetExpandCollapsePattern",
                ("ExpandCollapseState",),
            ),
            inspect_pattern(send_button, "GetTogglePattern", ("ToggleState",)),
            inspect_pattern(
                send_button,
                "GetLegacyIAccessiblePattern",
                ("DefaultAction", "State"),
            ),
            inspect_pattern(
                send_button,
                "GetSelectionItemPattern",
                ("IsSelected",),
            ),
        ],
        "direct_children": describe_controls(children),
        "parent": describe_control(parent) if parent is not None else None,
        "parent_children": describe_controls(siblings),
    }

    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
