import json, os, tempfile, unittest
from tools.qn_discovery_lab.qn_uia_message_lifecycle import *


def msg(k, direction="incoming", text="hello", typ="text", **kw):
    d={"message_key":k,"direction":direction,"text":text,"type":typ,"timestamp":"fictional"}
    d.update(kw); return d

class LifecycleTests(unittest.TestCase):
    def test_added_not_actionable_first(self):
        st=LifecycleState(); ch=compare_snapshot(st,[msg("m1")])
        self.assertEqual(len(ch["added"]),1); self.assertFalse(ch["added"][0]["actionable"])
    def test_second_observation_stable_actionable(self):
        st=LifecycleState(); compare_snapshot(st,[msg("m1")]); ch=compare_snapshot(st,[msg("m1")])
        self.assertEqual(ch["unchanged"][0]["lifecycle_status"],"stable"); self.assertTrue(ch["unchanged"][0]["actionable"])
    def test_repeat_no_added_key_same(self):
        st=LifecycleState(); compare_snapshot(st,[msg("m1")]); ch=compare_snapshot(st,[msg("m1")])
        self.assertEqual(ch["added"],[]); self.assertEqual(ch["unchanged"][0]["message_key"],"m1")
    def test_system_tip_candidate(self):
        st=LifecycleState(); ch=compare_snapshot(st,[msg("s1", text="温馨提示：虚构")])
        r=ch["added"][0]; self.assertEqual(r["lifecycle_kind"],"system_candidate"); self.assertFalse(r["actionable"]); self.assertTrue(r["requires_local_validation"])
    def test_buyer_withdrawal_candidate_only(self):
        st=LifecycleState(); compare_snapshot(st,[msg("m1")]); ch=compare_snapshot(st,[msg("m1"), msg("w1", text="对方撤回了一条消息")])
        r=ch["added"][0]; self.assertFalse(r["actionable"]); self.assertEqual(r["lifecycle_status"],"candidate_withdrawn"); self.assertTrue(any(e.get("type")=="correlation_candidate" for e in ch["candidate_events"])); self.assertNotIn("confirmed", r["lifecycle_status"])
    def test_seller_withdrawal_no_incoming_actionable(self):
        st=LifecycleState(); ch=compare_snapshot(st,[msg("w1", direction="outgoing", text="你撤回了一条消息")])
        self.assertFalse(ch["added"][0]["actionable"])
    def test_risk_candidate_no_retry(self):
        st=LifecycleState(); ch=compare_snapshot(st,[msg("r1", text="内容已被屏蔽")])
        r=ch["added"][0]; self.assertFalse(r["actionable"]); self.assertEqual(r["lifecycle_kind"],"risk_candidate")
    def test_send_failure_candidate_no_confirm_no_retry(self):
        st=LifecycleState(); ch=compare_snapshot(st,[msg("f1", direction="outgoing", text="消息发送失败")])
        r=ch["added"][0]; self.assertEqual(r["lifecycle_status"],"candidate_send_failed"); self.assertNotIn("confirmed", r["lifecycle_status"]); self.assertFalse(r["actionable"])
    def test_disappear_not_withdrawn(self):
        st=LifecycleState(); compare_snapshot(st,[msg("m1")]); ch=compare_snapshot(st,[])
        self.assertEqual(ch["not_observed"][0]["lifecycle_status"],"not_observed"); self.assertNotIn("withdrawn", ch["not_observed"][0]["lifecycle_status"])
    def test_reobserved_not_reactionable(self):
        st=LifecycleState(); compare_snapshot(st,[msg("m1")]); compare_snapshot(st,[msg("m1")]); compare_snapshot(st,[]); ch=compare_snapshot(st,[msg("m1")])
        self.assertEqual(len(ch["reobserved"]),1); self.assertFalse(ch["reobserved"][0]["actionable"])
    def test_history_seen_not_new(self):
        st=LifecycleState(processed=["m1"]); ch=compare_snapshot(st,[msg("m1", history_initial=True)])
        self.assertFalse(ch["added"][0]["actionable"])
    def test_automation_id_changed_candidate_no_merge_silently(self):
        st=LifecycleState(); compare_snapshot(st,[msg("m1", automation_id="a")]); ch=compare_snapshot(st,[msg("m1", automation_id="b")])
        self.assertTrue(any(e.get("type")=="identity_change_candidate" for e in ch["candidate_events"]))

    def test_in_place_buyer_withdrawal_confirmed_same_message_key(self):
        st=LifecycleState()
        before=msg("uia:fictional-withdrawn-node", direction="incoming", text="fictional original buyer text", typ="text", timestamp="21:24:56", automation_id="same-node")
        compare_snapshot(st,[before, msg("offscreen-withdrawal-history", direction="unknown", text="撤回了一条消息", typ="system", timestamp="", automation_id="history-node")])
        after=msg("uia:fictional-withdrawn-node", direction="unknown", text="撤回了一条消息", typ="system", timestamp="", automation_id="same-node")
        ch=compare_snapshot(st,[after, msg("offscreen-withdrawal-history", direction="unknown", text="撤回了一条消息", typ="system", timestamp="", automation_id="history-node")])
        self.assertEqual(len(ch["updated"]),1)
        self.assertEqual(ch["added"],[])
        self.assertEqual(ch["not_observed"],[])
        r=ch["updated"][0]
        self.assertEqual(r["message_key"],"uia:fictional-withdrawn-node")
        self.assertEqual(r["lifecycle_status"],"confirmed_withdrawn")
        self.assertFalse(r["actionable"])
        self.assertEqual(r["ignore_reason"],"buyer_withdrawn")
        self.assertFalse(r["requires_local_validation"])
        self.assertTrue(any(e.get("evidence_source")=="in_place_same_message_key_update" for e in r["evidence"]))

    def test_corrupt_state_fail_closed(self):
        fd,path=tempfile.mkstemp(); os.write(fd,b"not json"); os.close(fd)
        try:
            with self.assertRaises(LifecycleStateError): LifecycleState.load(path)
        finally: os.unlink(path)

if __name__ == "__main__": unittest.main()
