#!/usr/bin/env python3
"""CLI for privacy-safe capture, lifecycle comparison, and state inspection."""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path

from qn_uia_message_lifecycle import compare_snapshots, load_state

ROOT = Path(__file__).resolve().parent
EXTRACTOR = ROOT / "qn_uia_extract_messages.py"
SENSITIVE_TOKEN = "STORE_LOCAL_SENSITIVE"
PRIVATE_TOKEN = "SHOW_PRIVATE_CHAT_TEXT"
SCENARIOS = (
    "buyer_withdrawal",
    "seller_withdrawal",
    "system_tip",
    "risk_or_block",
    "outgoing_send_failure",
    "virtualized_list",
    "history_reload",
)


def _inside_repo(path: Path) -> bool:
    resolved = path.resolve()
    for parent in (ROOT.parents[1], Path(r"C:\qianniu-ai-bot")):
        try:
            resolved.relative_to(parent.resolve())
            return True
        except ValueError:
            pass
    return False


def capture_command(args: argparse.Namespace) -> list[str]:
    output = Path(args.output)
    command = [
        sys.executable,
        str(EXTRACTOR),
        "--max-messages",
        str(args.max_messages),
        "--max-depth",
        str(args.max_depth),
        "--max-nodes",
        str(args.max_nodes),
        "--output",
        str(output),
    ]
    if args.include_sensitive:
        if args.confirm_local_sensitive != SENSITIVE_TOKEN:
            raise ValueError(f"sensitive capture requires --confirm-local-sensitive {SENSITIVE_TOKEN}")
        if _inside_repo(output):
            raise ValueError("sensitive output must be outside every Git repository")
        command += ["--include-sensitive", "--confirm-private-output", PRIVATE_TOKEN]
    return command


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)
    capture = sub.add_parser("capture")
    capture.add_argument("--output", required=True)
    capture.add_argument("--max-messages", type=int, default=100)
    capture.add_argument("--max-depth", type=int, default=30)
    capture.add_argument("--max-nodes", type=int, default=8000)
    capture.add_argument("--include-sensitive", action="store_true")
    capture.add_argument("--confirm-local-sensitive", default="")
    compare = sub.add_parser("compare")
    compare.add_argument("before_pos", nargs="?")
    compare.add_argument("after_pos", nargs="?")
    compare.add_argument("--before")
    compare.add_argument("--after")
    compare.add_argument("--scenario", required=True, choices=SCENARIOS)
    compare.add_argument("--output", default="-")
    inspect = sub.add_parser("inspect-state")
    inspect.add_argument("state")
    return parser.parse_args()


def _read(path: str) -> dict:
    value = json.loads(Path(path).read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError("JSON root must be an object")
    return value


def compare_paths(args: argparse.Namespace) -> tuple[str, str]:
    before = args.before or args.before_pos
    after = args.after or args.after_pos
    if not before or not after:
        raise ValueError("compare requires before and after snapshots")
    if args.before and args.before_pos:
        raise ValueError("use either --before/--after or positional paths, not both")
    if args.after and args.after_pos:
        raise ValueError("use either --before/--after or positional paths, not both")
    return before, after


def main() -> int:
    args = parse_args()
    try:
        if args.command == "capture":
            if args.max_messages <= 0 or args.max_depth <= 0 or args.max_nodes <= 0:
                raise ValueError("capture limits must be positive")
            return subprocess.run(capture_command(args), cwd=str(ROOT), check=False).returncode
        if args.command == "compare":
            before, after = compare_paths(args)
            result = compare_snapshots(_read(before), _read(after))
            result["scenario"] = args.scenario
            rendered = json.dumps(result, ensure_ascii=False, indent=2) + "\n"
            if args.output == "-":
                sys.stdout.write(rendered)
            else:
                Path(args.output).write_text(rendered, encoding="utf-8")
            return 0
        state = load_state(Path(args.state))
        summary = {"record_count": len(state.get("records", {})), "valid": True}
        print(json.dumps(summary, ensure_ascii=False, indent=2))
        return 0
    except (OSError, ValueError, json.JSONDecodeError) as exc:
        print(json.dumps({"error": str(exc)}, ensure_ascii=False), file=sys.stderr)
        return 4


if __name__ == "__main__":
    raise SystemExit(main())
