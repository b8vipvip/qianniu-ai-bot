"""Read-only lifecycle evidence CLI for local Qianniu validation."""
from __future__ import annotations
import argparse, json, os, subprocess, sys
from pathlib import Path
from qn_uia_message_lifecycle import LifecycleState, LifecycleStateError, load_messages, compare_snapshot, redacted_evidence_bundle

SCENARIOS = {"buyer_withdrawal","seller_withdrawal","system_tip","risk_or_block","outgoing_send_failure","virtualized_list","history_reload"}
CONFIRM = "STORE_LOCAL_SENSITIVE"

def repo_root() -> Path:
    return Path(__file__).resolve().parents[2]

def in_repo(path: Path) -> bool:
    try: path.resolve().relative_to(repo_root().resolve()); return True
    except ValueError: return False

def write_json(path: Path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2, sort_keys=True); f.write("\n")

def cmd_capture(args):
    out = Path(args.output).resolve()
    if args.include_sensitive:
        if args.confirm_local_sensitive != CONFIRM: raise SystemExit("sensitive mode requires --confirm-local-sensitive STORE_LOCAL_SENSITIVE")
        if in_repo(out): raise SystemExit("refusing to write sensitive snapshot inside the Git repository")
    extractor = Path(__file__).with_name("qn_uia_extract_messages.py")
    if not extractor.exists(): raise SystemExit("qn_uia_extract_messages.py is required for live capture on the local Qianniu machine")
    cmd = [sys.executable, str(extractor), "--output", str(out)]
    if not args.include_sensitive: cmd.append("--redact")
    subprocess.run(cmd, check=True)
    print(json.dumps({"capture":"ok","output":str(out),"read_only":True,"sensitive":bool(args.include_sensitive)}, ensure_ascii=False))

def cmd_compare(args):
    if args.scenario and args.scenario not in SCENARIOS: raise SystemExit("unsupported scenario label")
    bundle = redacted_evidence_bundle(args.before, args.after, args.scenario or "")
    if args.output: write_json(Path(args.output), bundle)
    print(json.dumps(bundle, ensure_ascii=False, indent=2, sort_keys=True))

def cmd_inspect_state(args):
    try: st = LifecycleState.load(args.state)
    except LifecycleStateError as exc: raise SystemExit(str(exc))
    print(json.dumps({"schema_version":"qn_uia_lifecycle_state_inspect.v1","record_count":len(st.records),"processed_count":len(st.processed_message_keys),"raw_text_printed":False}, ensure_ascii=False, indent=2))

def main(argv=None):
    p=argparse.ArgumentParser(description="Read-only, redacted Qianniu UIA lifecycle evidence helper")
    sub=p.add_subparsers(required=True)
    c=sub.add_parser("capture", help="capture a redacted local snapshot via existing extractor")
    c.add_argument("--output", required=True); c.add_argument("--include-sensitive", action="store_true"); c.add_argument("--confirm-local-sensitive", default=""); c.set_defaults(func=cmd_capture)
    cm=sub.add_parser("compare", help="compare two snapshots and print a redacted evidence bundle")
    cm.add_argument("--before", required=True); cm.add_argument("--after", required=True); cm.add_argument("--scenario", choices=sorted(SCENARIOS), default=""); cm.add_argument("--output"); cm.set_defaults(func=cmd_compare)
    i=sub.add_parser("inspect-state", help="inspect lifecycle state without raw text")
    i.add_argument("--state", required=True); i.set_defaults(func=cmd_inspect_state)
    args=p.parse_args(argv); args.func(args)
if __name__ == "__main__": main()
