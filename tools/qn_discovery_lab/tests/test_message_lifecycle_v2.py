from __future__ import annotations

import argparse
import contextlib
import io
import json
import sys
import tempfile
import unittest
from unittest import mock
from pathlib import Path

LAB = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(LAB))

import qn_uia_extract_messages as extractor
import qn_uia_extract_messages_core as core
import qn_uia_lifecycle_probe as probe
import qn_uia_withdraw_exact_probe as withdraw_probe
from qn_uia_message_lifecycle import compare_snapshots, load_state, observation_key


def message(key="A", direction="incoming", content="x", *, flags=None, visible=True,
            node="node-a", message_type="text", observed=None):
    item = {
        "message_key": key, "node_identity_hash": node, "direction": direction,
        "observed_direction_guess": observed or direction, "type": message_type,
        "original_type": message_type, "timestamp": "2026-07-16 10:00",
        "content_hash": content, "semantic_flags": flags or [], "visible": visible,
    }
    item["observation_key"] = observation_key(item)
    return item


def snapshot(*items):
    return {"schema_version": "qn_uia_messages.v2", "messages": list(items)}


def capture_args(**overrides):
    values = {
        "output": "C:/tmp/a.json",
        "include_sensitive": False,
        "confirm_local_sensitive": "",
        "max_messages": 100,
        "max_depth": 30,
        "max_nodes": 8000,
    }
    values.update(overrides)
    return argparse.Namespace(**values)


class LifecycleTests(unittest.TestCase):
    def confirmed(self, before, after):
        result = compare_snapshots(snapshot(*before), snapshot(*after))
        return result, result["confirmed_events"]

    def test_01_redacted_buyer_withdrawal(self):
        result, events = self.confirmed([message()], [message(content="y", flags=["withdrawal_notice"], message_type="system")])
        self.assertEqual("confirmed_buyer_withdrawn", events[0]["lifecycle_status"])
        self.assertFalse(events[0]["actionable"])

    def test_02_redacted_seller_withdrawal_uses_prior_direction(self):
        old = message(direction="outgoing")
        new = message(direction="unknown", observed="incoming", content="y", flags=["withdrawal_notice"], message_type="system")
        _result, events = self.confirmed([old], [new])
        self.assertEqual("confirmed_seller_withdrawn", events[0]["lifecycle_status"])
        self.assertEqual("outgoing", events[0]["prior_direction"])

    def test_03_duplicate_text_only_second_withdrawn(self):
        a = message("A", direction="outgoing", content="same", node="node-a")
        b = message("B", direction="outgoing", content="same", node="node-b")
        b2 = message("B", direction="unknown", content="notice", node="node-b", flags=["withdrawal_notice"], message_type="system", observed="incoming")
        result, events = self.confirmed([a, b], [a, b2])
        self.assertEqual(["A"], [x["message_key"] for x in result["changes"]["unchanged"]])
        self.assertEqual(["B"], [x["message_key"] for x in result["changes"]["updated"]])
        self.assertEqual(1, len(events))

    def test_04_historical_offscreen_withdrawals_not_associated(self):
        candidate = message("H", content="notice", flags=["withdrawal_notice"], visible=False, message_type="system")
        result = compare_snapshots(snapshot(), snapshot(candidate), history_initial=True)
        self.assertEqual(1, len(result["candidate_events"]))
        self.assertEqual([], result["confirmed_events"])

    def test_05_semantics_survive_redaction(self):
        flags, rules = core.semantic_metadata(["某用户撤回了一条消息"], text_fragments=[], hyperlink_count=0)
        self.assertIn("withdrawal_notice", flags)
        self.assertIn("withdrawal.full_phrase", rules)

    def test_06_fragmented_withdrawal(self):
        flags, rules = core.semantic_metadata(["撤回", "了一条消息"], text_fragments=["撤回", "了一条消息"], hyperlink_count=0)
        self.assertIn("withdrawal_notice", flags)
        self.assertTrue(any(x.startswith("withdrawal.") for x in rules))

    def test_07_withdrawal_with_hyperlink(self):
        flags, rules = core.semantic_metadata(["撤回", "了一条消息", "重新编辑"], text_fragments=[], hyperlink_count=1)
        self.assertIn("withdrawal.with_edit_link", rules)

    def test_08_default_capture_has_no_redact_argument(self):
        args = capture_args()
        self.assertNotIn("--redact", probe.capture_command(args))

    def test_09_sensitive_capture_forwards_both_confirmations(self):
        args = capture_args(output="C:/qianniu-evidence/private.json", include_sensitive=True,
                            confirm_local_sensitive=probe.SENSITIVE_TOKEN)
        command = probe.capture_command(args)
        self.assertIn("--include-sensitive", command)
        self.assertIn(probe.PRIVATE_TOKEN, command)

    def test_10_sensitive_repo_path_rejected(self):
        args = capture_args(output=str(LAB / "private.json"), include_sensitive=True,
                            confirm_local_sensitive=probe.SENSITIVE_TOKEN)
        with self.assertRaises(ValueError):
            probe.capture_command(args)

    def test_11_different_ids_different_hashes(self):
        self.assertNotEqual(core.sha256_text("A.PNM"), core.sha256_text("B.PNM"))

    def test_12_bounds_do_not_change_identity(self):
        self.assertEqual(core.sha256_text("A.PNM"), core.sha256_text("A.PNM"))

    def test_13_visible_does_not_change_observation_key(self):
        self.assertEqual(observation_key(message(visible=True)), observation_key(message(visible=False)))

    def test_14_disappearance_is_not_observed(self):
        result = compare_snapshots(snapshot(message()), snapshot())
        self.assertEqual(1, len(result["changes"]["not_observed"]))
        self.assertEqual([], result["confirmed_events"])

    def test_15_reappearance_is_reobserved(self):
        result = compare_snapshots(snapshot(), snapshot(message()), previously_not_observed={"A"})
        self.assertEqual(1, len(result["changes"]["reobserved"]))

    def test_16_reobserved_not_actionable(self):
        result = compare_snapshots(snapshot(), snapshot(message()), previously_not_observed={"A"})
        self.assertFalse(result["changes"]["reobserved"][0]["actionable"])

    def test_17_history_initial_not_new_actionable(self):
        result = compare_snapshots(snapshot(), snapshot(message()), history_initial=True)
        self.assertTrue(result["changes"]["added"][0]["history_initial"])
        self.assertFalse(result["changes"]["added"][0]["actionable"])

    def test_18_corrupt_state_fails_closed(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "state.json"
            path.write_text("{broken", encoding="utf-8")
            with self.assertRaises(ValueError):
                load_state(path)

    def test_19_standalone_withdrawal_candidate_only(self):
        item = message(flags=["withdrawal_notice"], message_type="system")
        result = compare_snapshots(snapshot(), snapshot(item))
        self.assertEqual(1, len(result["candidate_events"]))
        self.assertEqual(0, len(result["confirmed_events"]))

    def test_20_normal_direction_is_preserved(self):
        incoming = message(direction="incoming")
        outgoing = message("B", direction="outgoing", node="node-b")
        self.assertEqual("incoming", incoming["direction"])
        self.assertEqual("outgoing", outgoing["direction"])

    def test_21_stable_key_remains_compatible_with_baseline(self):
        raw_id = "fixture-message-42.PNM"
        redacted_id = core.redact(raw_id, "node_id")
        legacy_key = "uia:" + extractor._digest([redacted_id])
        new_message = {
            "node_id": redacted_id,
            "node_identity_hash": core.sha256_text(raw_id),
        }
        key, source = extractor._stable_message_key(new_message)
        self.assertEqual(legacy_key, key)
        self.assertEqual("automation_id", source)

    def test_22_new_identity_metadata_does_not_create_added(self):
        raw_id = "fixture-message-42.PNM"
        node_id = core.redact(raw_id, "node_id")
        old = {"node_id": node_id, "direction": "incoming", "type": "text",
               "sender": "sender", "timestamp": "10:00", "text": "text"}
        new = dict(old, node_identity_hash=core.sha256_text(raw_id), content_hash="hash")
        self.assertEqual(extractor._stable_message_key(old), extractor._stable_message_key(new))

    def test_23_message_list_metadata_keeps_legacy_and_hash_fields(self):
        meta = core.build_meta(message_count=0, used_fallback=False, warnings=[])
        self.assertEqual("J_msg_list", meta["message_list_automation_id"])
        self.assertEqual(core.sha256_text("J_msg_list"), meta["message_list_identity_hash"])

    def test_24_order_refund_question_remains_user_text(self):
        flags, _rules = core.semantic_metadata(
            ["我的订单怎么申请退款"], text_fragments=["我的订单怎么申请退款"], hyperlink_count=0,
        )
        item = message(direction="incoming", flags=flags, message_type="text")
        result = compare_snapshots(snapshot(), snapshot(item))
        event = result["changes"]["added"][0]
        self.assertEqual("user_text", event["lifecycle_kind"])
        self.assertTrue(event["actionable_eligible"])
        self.assertNotIn("order_notice", flags)
        self.assertEqual([], result["confirmed_events"])

    def test_25_capture_forwards_limits(self):
        command = probe.capture_command(capture_args(max_messages=80, max_depth=20, max_nodes=4000))
        self.assertEqual("80", command[command.index("--max-messages") + 1])
        self.assertEqual("20", command[command.index("--max-depth") + 1])
        self.assertEqual("4000", command[command.index("--max-nodes") + 1])
        self.assertNotIn("--redact", command)

    def test_26_compare_outputs_scenario_with_named_paths(self):
        with tempfile.TemporaryDirectory() as directory:
            before = Path(directory) / "before.json"
            after = Path(directory) / "after.json"
            before.write_text(json.dumps(snapshot(message())), encoding="utf-8")
            after.write_text(json.dumps(snapshot(message())), encoding="utf-8")
            output = io.StringIO()
            argv = ["probe", "compare", "--before", str(before), "--after", str(after),
                    "--scenario", "history_reload"]
            with mock.patch.object(sys, "argv", argv), contextlib.redirect_stdout(output):
                self.assertEqual(0, probe.main())
            self.assertEqual("history_reload", json.loads(output.getvalue())["scenario"])

    def test_27_compare_accepts_positional_paths(self):
        args = argparse.Namespace(before=None, after=None, before_pos="A", after_pos="B")
        self.assertEqual(("A", "B"), probe.compare_paths(args))

    def test_28_invalid_scenario_is_rejected(self):
        argv = ["probe", "compare", "A", "B", "--scenario", "invalid"]
        with mock.patch.object(sys, "argv", argv), contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            probe.parse_args()

    def _expectation_payload(self, items):
        return {
            "schema_version": "qn_uia_messages.v2",
            "redacted": True,
            "expectation": {"provided": True, "matched": True, "match_count": len(items)},
            "messages": items,
        }

    def _raw_expectation_message(self, key, *, text="<redacted:text>", match=True):
        return {
            "node_id": core.redact(key + ".PNM", "node_id"),
            "node_identity_hash": core.sha256_text(key + ".PNM"),
            "content_hash": core.sha256_text(text),
            "direction": "incoming", "observed_direction_guess": "incoming",
            "type": "text", "original_type": "text", "semantic_flags": [],
            "timestamp": "10:00", "text": core.redact(text, "text"),
            "sender": core.redact("fixture", "sender"), "_expectation_match": match,
            "visible": True,
            "control_flags": {"is_pnm_node": True, "text_control_count": 1},
            "direction_diagnostics": {
                "direction_source": "body_geometry", "avatar_candidate_count": 0,
                "avatar_side": "unknown", "body_anchor_found": True,
            },
        }

    def test_29_expectation_matches_body_equal_to_token(self):
        result = extractor._normalize_and_deduplicate(self._expectation_payload([
            self._raw_expectation_message("A", text="fixture-token"),
        ]))
        self.assertEqual(1, result["expectation"]["match_count"])
        self.assertEqual("uia:" + extractor._digest([result["messages"][0]["node_id"]]), result["expectation"]["matches"][0]["message_key"])

    def test_30_expectation_fragment_match_does_not_require_body_hash(self):
        result = extractor._normalize_and_deduplicate(self._expectation_payload([
            self._raw_expectation_message("A", text="body with other content"),
        ]))
        self.assertEqual(result["messages"][0]["content_hash"], result["expectation"]["matches"][0]["content_hash"])

    def test_31_single_match_is_reported(self):
        result = extractor._normalize_and_deduplicate(self._expectation_payload([
            self._raw_expectation_message("A", match=True), self._raw_expectation_message("B", match=False),
        ]))
        self.assertEqual(1, result["expectation"]["match_count"])

    def test_32_two_substring_matches_are_counted(self):
        result = extractor._normalize_and_deduplicate(self._expectation_payload([
            self._raw_expectation_message("A"), self._raw_expectation_message("B"),
        ]))
        self.assertEqual(2, result["expectation"]["match_count"])

    def test_33_wrapper_dedup_matches_final_messages(self):
        duplicate = self._raw_expectation_message("A")
        result = extractor._normalize_and_deduplicate(self._expectation_payload([duplicate, dict(duplicate)]))
        self.assertEqual(1, len(result["messages"]))
        self.assertEqual(1, result["expectation"]["match_count"])
        self.assertEqual(result["messages"][0]["message_key"], result["expectation"]["matches"][0]["message_key"])

    def test_34_expectation_output_is_redacted(self):
        result = extractor._normalize_and_deduplicate(self._expectation_payload([
            self._raw_expectation_message("A", text="secret fixture token"),
        ]))
        rendered = json.dumps(result, ensure_ascii=False)
        self.assertNotIn("secret fixture token", rendered)

    def test_35_expectation_matches_has_no_raw_automation_id(self):
        result = extractor._normalize_and_deduplicate(self._expectation_payload([
            self._raw_expectation_message("A"),
        ]))
        self.assertNotIn("node_id", result["expectation"]["matches"][0])

    def test_36_expectation_keeps_legacy_key_algorithm(self):
        result = extractor._normalize_and_deduplicate(self._expectation_payload([
            self._raw_expectation_message("fixture-message-42"),
        ]))
        message = result["messages"][0]
        expected = "uia:" + extractor._digest([message["node_id"]])
        self.assertEqual(expected, result["expectation"]["matches"][0]["message_key"])

    def test_37_duplicate_late_match_is_preserved(self):
        first = self._raw_expectation_message("A", match=False)
        second = dict(first, _expectation_match=True)
        result = extractor._normalize_and_deduplicate(self._expectation_payload([first, second]))
        self.assertEqual(1, result["expectation"]["match_count"])
        self.assertEqual(result["messages"][0]["message_key"], result["expectation"]["matches"][0]["message_key"])

    def test_38_direction_diagnostics_are_privacy_safe(self):
        result = extractor._normalize_and_deduplicate(self._expectation_payload([
            self._raw_expectation_message("A"),
        ]))
        match = result["expectation"]["matches"][0]
        self.assertEqual("body_geometry", match["direction_diagnostics"]["direction_source"])
        rendered = json.dumps(match, ensure_ascii=False)
        for forbidden in ("rectangle", "bounds", "coordinate", '"node_id":', '"automation_id":'):
            self.assertNotIn(forbidden, rendered.lower())

    def test_39_exact_withdraw_identity_accepts_captured_target(self):
        metadata = {
            "message_key": "uia:key", "node_identity_hash": "node-hash",
            "visible": True, "type": "text", "original_type": "text",
            "semantic_flags": [],
        }
        withdraw_probe.validate_expected_identity(metadata, "uia:key", "node-hash")

    def test_40_exact_withdraw_rejects_wrong_key_or_node(self):
        metadata = {
            "message_key": "uia:key", "node_identity_hash": "node-hash",
            "visible": True, "type": "text", "original_type": "text",
            "semantic_flags": [],
        }
        with self.assertRaises(ValueError):
            withdraw_probe.validate_expected_identity(metadata, "uia:other", "node-hash")
        with self.assertRaises(ValueError):
            withdraw_probe.validate_expected_identity(metadata, "uia:key", "other-node")

    def test_41_exact_withdraw_rejects_offscreen_or_non_text(self):
        base = {
            "message_key": "uia:key", "node_identity_hash": "node-hash",
            "visible": True, "type": "text", "original_type": "text",
            "semantic_flags": [],
        }
        with self.assertRaises(ValueError):
            withdraw_probe.validate_expected_identity(dict(base, visible=False), "uia:key", "node-hash")
        with self.assertRaises(ValueError):
            withdraw_probe.validate_expected_identity(dict(base, type="system"), "uia:key", "node-hash")


if __name__ == "__main__":
    unittest.main()
