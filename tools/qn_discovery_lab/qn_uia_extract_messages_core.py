#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Read loaded Qianniu chat messages through Windows UI Automation.

The extractor is read-only. It never writes to the chat input, invokes a button,
sends keys, clicks, scrolls, or changes the selected conversation.

Privacy is the default: message node IDs, sender names, and message bodies are
hashed/redacted. Raw private values require both --include-sensitive and the
exact confirmation token SHOW_PRIVATE_CHAT_TEXT. Never commit real output.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable

import uiautomation as auto

from qn_uia_send_probe import (
    DOC_NAME,
    WINDOW_CLASS,
    WINDOW_NAME,
    configure_console,
    fail,
    require,
)

MESSAGE_LIST_ID = "J_msg_list"
PRIVATE_OUTPUT_TOKEN = "SHOW_PRIVATE_CHAT_TEXT"
SCHEMA_VERSION = "qn_uia_messages.v2"

MESSAGE_NODE_RE = re.compile(r"(?:^|[._-])[A-Za-z0-9_-]+\.PNM$|\.PNM(?:$|[._-])")
URL_RE = re.compile(r"https?://\S+", re.IGNORECASE)
PRICE_RE = re.compile(r"(?:¥|￥|RMB\s*)\s*\d+(?:\.\d{1,2})?", re.IGNORECASE)
TIMESTAMP_RES = (
    re.compile(r"\b20\d{2}[-/.]\d{1,2}[-/.]\d{1,2}\s+\d{1,2}:\d{2}(?::\d{2})?\b"),
    re.compile(r"(?:今天|昨天|前天)\s*\d{1,2}:\d{2}(?::\d{2})?"),
    re.compile(r"\b\d{1,2}:\d{2}(?::\d{2})?\b"),
)

GENERIC_LABELS = {
    "千牛消息聊天",
    "消息",
    "聊天",
    "发送",
    "关闭",
    "已读",
    "未读",
    "更多",
    "复制",
}
SYSTEM_MARKERS = (
    "系统消息",
    "服务提醒",
    "风险提示",
    "撤回了一条消息",
    "以上为历史消息",
    "消息已撤回",
)
PRODUCT_MARKERS = ("商品", "宝贝", "SKU", "价格", "立即购买", "查看详情")
IMAGE_MARKERS = ("[图片]", "图片消息", "查看图片")

SEMANTIC_RULES = {
    "withdrawal.full_phrase": ("撤回了一条消息", "消息已撤回", "撤回一条消息"),
    "risk.block": ("风险提示", "存在风险", "安全风险", "已被拦截"),
    "send.failure": ("发送失败", "消息发送失败", "未发送成功"),
    "history.marker": ("以上为历史消息", "以下为新消息", "历史消息"),
    "order.notice": ("订单", "退款", "售后", "交易关闭"),
    "system.tip": ("系统消息", "服务提醒", "系统提示"),
}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Read loaded Qianniu messages as redacted structured JSON",
    )
    parser.add_argument(
        "--max-messages",
        type=int,
        default=100,
        help="Maximum number of loaded message nodes to output; default 100",
    )
    parser.add_argument(
        "--max-depth",
        type=int,
        default=30,
        help="Maximum UIA traversal depth below J_msg_list; default 30",
    )
    parser.add_argument(
        "--max-nodes",
        type=int,
        default=8000,
        help="Maximum UIA nodes visited below J_msg_list; default 8000",
    )
    parser.add_argument(
        "--expect-message",
        default="",
        help="Check whether exact text exists without printing that text",
    )
    parser.add_argument(
        "--include-sensitive",
        action="store_true",
        help="Output raw node IDs, senders, and bodies; unsafe for shared logs",
    )
    parser.add_argument(
        "--confirm-private-output",
        default="",
        help=f"Required token for raw output: {PRIVATE_OUTPUT_TOKEN}",
    )
    parser.add_argument(
        "--output",
        default="-",
        help="Output file path, or - for stdout; never commit real output",
    )
    return parser.parse_args()


def safe_attr(obj: Any, name: str, default: Any = None) -> Any:
    try:
        value = getattr(obj, name)
    except Exception:
        return default
    return default if value is None else value


def safe_text(value: Any) -> str:
    if value is None:
        return ""
    try:
        return str(value)
    except Exception:
        return ""


def split_fragments(value: str) -> list[str]:
    fragments: list[str] = []
    for item in re.split(r"[\r\n]+", value or ""):
        normalized = re.sub(r"\s+", " ", item).strip()
        if normalized:
            fragments.append(normalized)
    return fragments


def unique_strings(values: Iterable[str]) -> list[str]:
    result: list[str] = []
    seen: set[str] = set()
    for value in values:
        if not value or value in seen:
            continue
        seen.add(value)
        result.append(value)
    return result


def rect_tuple(control: auto.Control) -> tuple[int, int, int, int] | None:
    rect = safe_attr(control, "BoundingRectangle")
    if rect is None:
        return None

    for names in (("left", "top", "right", "bottom"), ("Left", "Top", "Right", "Bottom")):
        try:
            values = tuple(int(getattr(rect, name)) for name in names)
        except Exception:
            continue
        return values  # type: ignore[return-value]

    match = re.search(
        r"\((-?\d+)\s*,\s*(-?\d+)\s*,\s*(-?\d+)\s*,\s*(-?\d+)\)",
        safe_text(rect),
    )
    if not match:
        return None
    return tuple(int(group) for group in match.groups())  # type: ignore[return-value]


def iter_tree(
    root: auto.Control,
    *,
    max_depth: int,
    max_nodes: int,
    include_root: bool = True,
) -> Iterable[tuple[auto.Control, int]]:
    stack: list[tuple[auto.Control, int]] = [(root, 0)]
    visited = 0

    while stack and visited < max_nodes:
        control, depth = stack.pop()
        visited += 1

        if include_root or depth > 0:
            yield control, depth

        if depth >= max_depth:
            continue

        try:
            children = control.GetChildren()
        except Exception:
            children = []

        for child in reversed(children):
            stack.append((child, depth + 1))


def control_automation_id(control: auto.Control) -> str:
    return safe_text(safe_attr(control, "AutomationId", "")).strip()


def is_message_node(control: auto.Control) -> bool:
    automation_id = control_automation_id(control)
    return bool(automation_id and MESSAGE_NODE_RE.search(automation_id))


def find_message_nodes(
    message_list: auto.Control,
    *,
    max_depth: int,
    max_nodes: int,
) -> tuple[list[auto.Control], bool]:
    result: list[auto.Control] = []
    stack: list[tuple[auto.Control, int]] = []

    try:
        children = message_list.GetChildren()
    except Exception:
        children = []

    for child in reversed(children):
        stack.append((child, 1))

    visited = 0
    while stack and visited < max_nodes:
        control, depth = stack.pop()
        visited += 1

        if is_message_node(control):
            result.append(control)
            # A PNM node is the message boundary. Do not collect nested PNM nodes twice.
            continue

        if depth >= max_depth:
            continue

        try:
            descendants = control.GetChildren()
        except Exception:
            descendants = []

        for child in reversed(descendants):
            stack.append((child, depth + 1))

    if result:
        return result, False

    # Conservative fallback for UI variants: direct J_msg_list children only.
    fallback = [child for child in children if safe_text(safe_attr(child, "Name", "")).strip()]
    return fallback, True


def collect_fragment_details(control: auto.Control) -> tuple[list[str], list[str], dict[str, Any]]:
    root_name = safe_text(safe_attr(control, "Name", ""))
    descendant_fragments: list[str] = []
    control_types: list[str] = []
    text_fragments: list[str] = []
    hyperlink_count = 0

    for descendant, depth in iter_tree(
        control,
        max_depth=12,
        max_nodes=400,
        include_root=True,
    ):
        control_type = safe_text(safe_attr(descendant, "ControlTypeName", "")).strip()
        if control_type:
            control_types.append(control_type)

        if "Hyperlink" in control_type:
            hyperlink_count += 1

        if depth == 0:
            continue
        name = safe_text(safe_attr(descendant, "Name", ""))
        parts = split_fragments(name)
        descendant_fragments.extend(parts)
        if "Text" in control_type:
            text_fragments.extend(parts)

    fragments = unique_strings(descendant_fragments)
    if not fragments:
        fragments = unique_strings(split_fragments(root_name))

    details = {
        "text_fragments": text_fragments,
        "text_control_count": sum(1 for item in control_types if "Text" in item),
        "hyperlink_count": hyperlink_count,
    }
    return fragments, unique_strings(control_types), details


def collect_fragments(control: auto.Control) -> tuple[list[str], list[str]]:
    fragments, control_types, _details = collect_fragment_details(control)
    return fragments, control_types


def normalize_content(value: str) -> str:
    return re.sub(r"\s+", " ", value or "").strip()


def sha256_text(value: str) -> str:
    return hashlib.sha256(value.encode("utf-8", errors="replace")).hexdigest()


def semantic_metadata(
    fragments: list[str],
    *,
    text_fragments: list[str],
    hyperlink_count: int,
) -> tuple[list[str], list[str]]:
    candidates = [normalize_content(item) for item in fragments + text_fragments if item]
    joined = normalize_content(" ".join(candidates))
    compact = re.sub(r"\s+", "", joined)
    matched: list[str] = []

    for rule_id, phrases in SEMANTIC_RULES.items():
        if any(normalize_content(phrase) in joined or phrase in compact for phrase in phrases):
            matched.append(rule_id)

    withdrawal_parts = any("撤回" in item for item in candidates) and any(
        marker in joined or marker in compact for marker in ("消息", "一条", "重新编辑")
    )
    if withdrawal_parts and "withdrawal.full_phrase" not in matched:
        matched.append("withdrawal.fragmented_row")
    if withdrawal_parts and hyperlink_count:
        matched.append("withdrawal.with_edit_link")

    flags: list[str] = []
    if any(item.startswith("withdrawal.") for item in matched):
        flags.append("withdrawal_notice")
    if "risk.block" in matched:
        flags.append("risk_notice")
    if "send.failure" in matched:
        flags.append("send_failure_notice")
    if "order.notice" in matched:
        flags.append("order_notice")
    if "history.marker" in matched:
        flags.append("history_marker")
    if "system.tip" in matched or flags:
        flags.append("system_tip")
    return unique_strings(flags), unique_strings(matched)


def find_timestamp(fragments: list[str]) -> tuple[str, int]:
    for index, fragment in enumerate(fragments):
        for pattern in TIMESTAMP_RES:
            match = pattern.search(fragment)
            if match:
                return match.group(0), index
    return "", -1


def is_metadata_fragment(fragment: str, timestamp: str) -> bool:
    value = fragment.strip()
    if not value or value in GENERIC_LABELS:
        return True
    if timestamp and value == timestamp:
        return True
    if re.fullmatch(r"已读|未读|送达|发送中|发送失败", value):
        return True
    return False


def choose_sender(fragments: list[str], timestamp: str, timestamp_index: int) -> str:
    candidates = fragments[:timestamp_index] if timestamp_index > 0 else []
    for fragment in reversed(candidates):
        if is_metadata_fragment(fragment, timestamp):
            continue
        if URL_RE.search(fragment) or PRICE_RE.search(fragment):
            continue
        if len(fragment) > 80:
            continue
        return fragment
    return ""


def choose_body(
    fragments: list[str],
    sender: str,
    timestamp: str,
    timestamp_index: int,
) -> str:
    candidates = fragments[timestamp_index + 1 :] if timestamp_index >= 0 else list(fragments)
    body: list[str] = []

    for fragment in candidates:
        if fragment == sender or is_metadata_fragment(fragment, timestamp):
            continue
        body.append(fragment)

    if not body:
        for fragment in fragments:
            if fragment == sender or is_metadata_fragment(fragment, timestamp):
                continue
            body.append(fragment)

    return "\n".join(unique_strings(body)).strip()


def classify_type(body: str, fragments: list[str], control_types: list[str]) -> str:
    joined = "\n".join(fragments)
    if any("Image" in control_type for control_type in control_types):
        return "image"
    if any(marker in joined for marker in IMAGE_MARKERS):
        return "image"
    if URL_RE.search(joined) or PRICE_RE.search(joined) or any(
        marker in joined for marker in PRODUCT_MARKERS
    ):
        return "product"
    if any(marker in joined for marker in SYSTEM_MARKERS):
        return "system"
    if body:
        return "text"
    return "unknown"


def classify_direction(
    node: auto.Control,
    message_list: auto.Control,
    message_type: str,
) -> str:
    if message_type == "system":
        return "unknown"

    node_rect = rect_tuple(node)
    list_rect = rect_tuple(message_list)
    if node_rect is None or list_rect is None:
        return "unknown"

    node_center = (node_rect[0] + node_rect[2]) / 2.0
    list_center = (list_rect[0] + list_rect[2]) / 2.0
    threshold = max(12.0, (list_rect[2] - list_rect[0]) * 0.07)

    if node_center > list_center + threshold:
        return "outgoing"
    if node_center < list_center - threshold:
        return "incoming"
    return "unknown"


def redact(value: str, label: str) -> str:
    if not value:
        return ""
    digest = hashlib.sha256(value.encode("utf-8", errors="replace")).hexdigest()[:12]
    return f"<redacted:{label}:sha256={digest}:chars={len(value)}>"


def serialize_sensitive(value: str, label: str, include_sensitive: bool) -> str:
    return value if include_sensitive else redact(value, label)


def message_sort_key(control: auto.Control) -> tuple[int, int, str]:
    rect = rect_tuple(control)
    if rect is None:
        return (sys.maxsize, sys.maxsize, control_automation_id(control))
    return (rect[1], rect[0], control_automation_id(control))


def extract_message(
    node: auto.Control,
    message_list: auto.Control,
    *,
    include_sensitive: bool,
    expect_message: str,
) -> tuple[dict[str, Any], bool]:
    fragments, control_types, details = collect_fragment_details(node)
    timestamp, timestamp_index = find_timestamp(fragments)
    sender = choose_sender(fragments, timestamp, timestamp_index)
    body = choose_body(fragments, sender, timestamp, timestamp_index)
    original_type = classify_type(body, fragments, control_types)
    observed_direction = classify_direction(node, message_list, original_type)
    node_id = control_automation_id(node)
    semantic_flags, matched_rule_ids = semantic_metadata(
        fragments,
        text_fragments=details["text_fragments"],
        hyperlink_count=details["hyperlink_count"],
    )
    message_type = "system" if "withdrawal_notice" in semantic_flags else original_type
    direction = "unknown" if "withdrawal_notice" in semantic_flags else observed_direction
    normalized_body = normalize_content(body)
    matched = bool(expect_message) and any(expect_message in fragment for fragment in fragments)

    is_offscreen = bool(safe_attr(node, "IsOffscreen", False))
    return (
        {
            "node_id": serialize_sensitive(node_id, "node_id", include_sensitive),
            "sender": serialize_sensitive(sender, "sender", include_sensitive),
            "timestamp": timestamp,
            "direction": direction,
            "type": message_type,
            "original_type": original_type,
            "text": serialize_sensitive(body, "text", include_sensitive),
            "visible": not is_offscreen,
            "content_hash": sha256_text(normalized_body),
            "content_chars": len(normalized_body),
            "node_identity_hash": sha256_text(node_id) if node_id else "",
            "semantic_flags": semantic_flags,
            "matched_rule_ids": matched_rule_ids,
            "control_flags": {
                "is_pnm_node": bool(node_id and MESSAGE_NODE_RE.search(node_id)),
                "text_control_count": details["text_control_count"],
                "hyperlink_count": details["hyperlink_count"],
                "has_hyperlink": details["hyperlink_count"] > 0,
            },
            "observed_direction_guess": observed_direction,
        },
        matched,
    )


def write_json(payload: dict[str, Any], output: str) -> None:
    rendered = json.dumps(payload, ensure_ascii=False, indent=2) + "\n"
    if output == "-":
        sys.stdout.write(rendered)
        return

    path = Path(output)
    path.write_text(rendered, encoding="utf-8")
    print(f"[WROTE] {path}", file=sys.stderr)
    print("[PRIVATE] Do not commit real chat extraction output.", file=sys.stderr)


def main() -> int:
    configure_console()
    args = parse_args()

    if args.max_messages <= 0:
        fail("max-messages must be positive", 1)
    if args.max_depth <= 0:
        fail("max-depth must be positive", 1)
    if args.max_nodes <= 0:
        fail("max-nodes must be positive", 1)
    if args.include_sensitive and args.confirm_private_output != PRIVATE_OUTPUT_TOKEN:
        fail(
            f"raw output requires --confirm-private-output {PRIVATE_OUTPUT_TOKEN}",
            4,
        )

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
        max_depth=args.max_depth,
        max_nodes=args.max_nodes,
    )
    nodes.sort(key=message_sort_key)
    if len(nodes) > args.max_messages:
        nodes = nodes[-args.max_messages :]

    messages: list[dict[str, Any]] = []
    match_count = 0
    for node in nodes:
        message, matched = extract_message(
            node,
            message_list,
            include_sensitive=args.include_sensitive,
            expect_message=args.expect_message,
        )
        messages.append(message)
        if matched:
            match_count += 1

    warnings: list[str] = []
    if used_fallback:
        warnings.append(
            "No *.PNM message nodes were found; output uses direct J_msg_list children."
        )
    if not messages:
        warnings.append("No loaded message nodes were found in J_msg_list.")
    warnings.append(
        "Current-conversation identity is intentionally not inferred in this P2 extractor."
    )

    payload: dict[str, Any] = {
        "schema_version": SCHEMA_VERSION,
        "redacted": not args.include_sensitive,
        "conversation": {
            "detected": False,
            "display_name": "",
            "conversation_id": "",
            "source": "not_implemented_until_P4",
        },
        "messages": messages,
        "meta": {
            "generated_at_utc": datetime.now(timezone.utc).isoformat(),
            "window": WINDOW_NAME,
            "document": DOC_NAME,
            "message_list_identity_hash": sha256_text(MESSAGE_LIST_ID),
            "message_count": len(messages),
            "used_direct_child_fallback": used_fallback,
            "read_only": True,
            "warnings": warnings,
        },
    }

    if args.expect_message:
        payload["expectation"] = {
            "provided": True,
            "expected_sha256": hashlib.sha256(
                args.expect_message.encode("utf-8", errors="replace")
            ).hexdigest()[:12],
            "matched": match_count > 0,
            "match_count": match_count,
        }

    write_json(payload, args.output)
    return 0 if messages else 5


if __name__ == "__main__":
    raise SystemExit(main())
