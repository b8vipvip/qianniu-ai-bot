#!/usr/bin/env python3
"""Analyze Qianniu Bot logs for IMSDK direct-send evidence.

This tool is intentionally passive: it parses existing logs/discovery snapshots and
never connects to Qianniu or invokes any API.
"""
from __future__ import annotations

import argparse
import json
import re
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any, Dict, Iterable, List, Tuple

TRACE_MARKERS = ("imsdkInvokeTrace", "IMSDK调用跟踪", "IMSDK invoke")
DISCOVERY_MARKERS = ("imsdkSendDiscoveryV2", "imsdkApiScan")
SEND_WORDS = ("sendmsg", "sendmessage", "send", "reply", "message", "msg", "chat", "wangwang", "publish", "smarttip")


def _read_text(path: Path) -> str:
    for encoding in ("utf-8-sig", "utf-8", "gb18030"):
        try:
            return path.read_text(encoding=encoding, errors="strict")
        except UnicodeError:
            pass
    return path.read_text(encoding="utf-8", errors="replace")


def _balanced_json_fragments(line: str) -> Iterable[str]:
    """Yield balanced JSON object fragments from a noisy log line."""
    starts = [i for i, ch in enumerate(line) if ch == "{"]
    for start in starts:
        depth = 0
        in_string = False
        escape = False
        for pos in range(start, len(line)):
            ch = line[pos]
            if in_string:
                if escape:
                    escape = False
                elif ch == "\\":
                    escape = True
                elif ch == '"':
                    in_string = False
                continue
            if ch == '"':
                in_string = True
            elif ch == "{":
                depth += 1
            elif ch == "}":
                depth -= 1
                if depth == 0:
                    yield line[start : pos + 1]
                    break


def _walk(value: Any) -> Iterable[Dict[str, Any]]:
    if isinstance(value, dict):
        yield value
        for child in value.values():
            yield from _walk(child)
    elif isinstance(value, list):
        for child in value:
            yield from _walk(child)


def _json_objects(text: str) -> Iterable[Dict[str, Any]]:
    seen = set()
    for line in text.splitlines():
        if not any(marker in line for marker in TRACE_MARKERS + DISCOVERY_MARKERS) and "SendSmartTipMsg" not in line:
            continue
        for fragment in _balanced_json_fragments(line):
            if fragment in seen:
                continue
            seen.add(fragment)
            try:
                parsed = json.loads(fragment)
            except Exception:
                continue
            yield from _walk(parsed)


def _method_from_obj(obj: Dict[str, Any]) -> str:
    for key in ("method", "apiName", "api", "name"):
        value = obj.get(key)
        if isinstance(value, str) and value.strip():
            return value.strip()
    return ""


def _score_method(method: str) -> int:
    lower = method.lower()
    score = 0
    if "sendmsg" in lower or "sendmessage" in lower:
        score += 160
    if ".send" in lower or lower.startswith("send"):
        score += 100
    for word in SEND_WORDS:
        if word in lower:
            score += 10
    if "wangwang" in lower or "singlemsg" in lower:
        score += 35
    if "intelligentservice.sendsmarttipmsg" in lower:
        # Observed to result in real seller echoes, but its smart-tip namespace means
        # we do not classify it as the canonical normal-chat API without more evidence.
        score += 80
    return score


def analyze_text(text: str) -> Dict[str, Any]:
    method_counts: Counter[str] = Counter()
    param_keys: Dict[str, Counter[str]] = defaultdict(Counter)
    discovery_paths: Counter[str] = Counter()

    for obj in _json_objects(text):
        method = _method_from_obj(obj)
        if method and any(word in method.lower() for word in SEND_WORDS):
            method_counts[method] += 1
            params = obj.get("param") or obj.get("params") or obj.get("arguments")
            if isinstance(params, dict):
                for key in params:
                    param_keys[method][str(key)] += 1

        path = obj.get("path")
        if isinstance(path, str) and any(word in path.lower() for word in SEND_WORDS):
            discovery_paths[path] += 1

    # Regex fallback catches escaped JSON/log text that could not be decoded cleanly.
    for match in re.finditer(r'(?:"method"\s*:\s*"|method[=:]\s*)([A-Za-z0-9_.-]*(?:send|Send|msg|Msg|message|Message|chat|Chat)[A-Za-z0-9_.-]*)', text):
        method_counts[match.group(1)] += 1
    for match in re.finditer(r'intelligentservice\.SendSmartTipMsg', text):
        method_counts["intelligentservice.SendSmartTipMsg"] += 1

    ranked = []
    for method, count in method_counts.items():
        canonical = "unknown"
        note = "needs live passive trace + controlled validation"
        if method.lower() == "intelligentservice.sendsmarttipmsg":
            canonical = "not-confirmed"
            note = "observed text-capable; smart-tip namespace, keep out of new-version production routing"
        ranked.append(
            {
                "method": method,
                "count": count,
                "score": _score_method(method),
                "param_keys": sorted(param_keys[method]),
                "canonical_normal_chat": canonical,
                "note": note,
            }
        )
    ranked.sort(key=lambda row: (-row["score"], -row["count"], row["method"]))

    paths = [
        {"path": path, "count": count, "score": _score_method(path)}
        for path, count in discovery_paths.items()
    ]
    paths.sort(key=lambda row: (-row["score"], -row["count"], row["path"]))

    return {
        "passive_analysis": True,
        "candidate_invocation": False,
        "methods": ranked,
        "discovery_paths": paths,
        "send_smart_tip_observed": any(row["method"].lower() == "intelligentservice.sendsmarttipmsg" for row in ranked),
        "canonical_direct_send_confirmed": any(row["canonical_normal_chat"] == "confirmed" for row in ranked),
    }


def analyze_files(paths: Iterable[Path]) -> Dict[str, Any]:
    combined = "\n".join(_read_text(path) for path in paths)
    return analyze_text(combined)


def main() -> int:
    parser = argparse.ArgumentParser(description="Analyze passive Qianniu IMSDK send discovery logs")
    parser.add_argument("logs", nargs="+", type=Path, help="Bot log / exported discovery files")
    parser.add_argument("--output", "-o", type=Path, help="write JSON report")
    args = parser.parse_args()

    report = analyze_files(args.logs)
    rendered = json.dumps(report, ensure_ascii=False, indent=2)
    if args.output:
        args.output.write_text(rendered + "\n", encoding="utf-8")
    print(rendered)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
