from __future__ import annotations

import argparse
import json
import sys
import tempfile
import unittest
from pathlib import Path

LAB = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(LAB))

import qn_uia_extract_messages as extractor
import qn_uia_extract_messages_core as core
import qn_uia_lifecycle_probe as probe
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
        args = argparse.Namespace(output="C:/tmp/a.json", include_sensitive=False, confirm_local_sensitive="")
        self.assertNotIn("--redact", probe.capture_command(args))

    def test_09_sensitive_capture_forwards_both_confirmations(self):
        args = argparse.Namespace(output="C:/qianniu-evidence/private.json", include_sensitive=True, confirm_local_sensitive=probe.SENSITIVE_TOKEN)
        command = probe.capture_command(args)
        self.assertIn("--include-sensitive", command)
        self.assertIn(probe.PRIVATE_TOKEN, command)

    def test_10_sensitive_repo_path_rejected(self):
        args = argparse.Namespace(output=str(LAB / "private.json"), include_sensitive=True, confirm_local_sensitive=probe.SENSITIVE_TOKEN)
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


if __name__ == "__main__":
    unittest.main()
