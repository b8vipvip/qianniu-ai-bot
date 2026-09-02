from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def write(path, text):
    (ROOT / path).write_text(text, encoding="utf-8")


def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected one anchor, found {count}")
    return text.replace(old, new, 1)


path = "src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs"
s = read(path)
s = replace_once(
    s,
    '''        // DeterministicAutoReplyService already owns the single per-buyer serialization gate.\n        // A second outer gate can strand every later generation behind one unhealthy fixed send.\n        private const int PreMergeRuleExecutionDeadlineMilliseconds = 20000;\n        private const int SemanticContinuationWindowSeconds = 180;''',
    '''        // DeterministicAutoReplyService owns the single per-buyer serialization gate.\n        // Do not add a second outer gate or race a still-running fixed-send task against AI.\n        private const int SemanticContinuationWindowSeconds = 180;''',
    "deadline constant")

start = s.index('            var allowLocalShortReply = !HasPendingBuyerMessages(item.SellerNick, item.BuyerNick);')
end = s.index('        private void AttachSemanticContinuation(BuyerMessageBurstItem item)', start)
new_block = '''            var allowLocalShortReply = !HasPendingBuyerMessages(item.SellerNick, item.BuyerNick);\n            Task.Run(async () =>\n            {\n                var continueToMerge = true;\n                try\n                {\n                    // The deterministic service owns the only same-buyer gate. Its bounded 1.8s\n                    // acquisition fails open for later generations, so an unhealthy fixed send can\n                    // no longer strand the whole buyer in Coalescing. We deliberately await the\n                    // selected rule task here: starting AI while that task can still send would\n                    // create a duplicate/out-of-order side-effect race.\n                    continueToMerge = await DeterministicAutoReplyService.HandleBeforeMergeAsync(\n                        item,\n                        allowLocalShortReply);\n                }\n                catch (OperationCanceledException)\n                {\n                    if (observation.CancellationToken.IsCancellationRequested)\n                    {\n                        Log.Info("消息合并前固定规则已因generation显式失效取消: seller="\n                            + item.SellerNick + ", buyer=" + item.BuyerNick\n                            + ", generation=" + item.SessionGeneration);\n                        return;\n                    }\n                    Log.ErrorWithMaxCount(\n                        "消息合并前固定规则发生非会话取消，已fail-open继续普通合并链路: seller="\n                        + item.SellerNick + ", buyer=" + item.BuyerNick\n                        + ", generation=" + item.SessionGeneration,\n                        20);\n                    continueToMerge = true;\n                }\n                catch (Exception ex)\n                {\n                    Log.ErrorWithMaxCount(\n                        "消息合并前固定规则处理失败，继续普通合并链路: seller=" + item.SellerNick\n                        + ", buyer=" + item.BuyerNick + ", error=" + Safe(ex.Message, 220),\n                        20);\n                    continueToMerge = true;\n                }\n\n                if (observation.CancellationToken.IsCancellationRequested\n                    || !_sessionAgent.IsCurrent(item.SellerNick, item.BuyerNick, item.SessionGeneration))\n                {\n                    return;\n                }\n\n                if (continueToMerge)\n                {\n                    try\n                    {\n                        EnqueueForMerge(item);\n                    }\n                    catch (Exception ex)\n                    {\n                        _sessionAgent.TryTransition(\n                            item.SellerNick,\n                            item.BuyerNick,\n                            item.SessionGeneration,\n                            BuyerSessionAgentState.Failed,\n                            "pre_merge_enqueue_exception");\n                        Log.ErrorWithMaxCount(\n                            "消息进入合并队列异常，已结束Coalescing避免永久等待: seller="\n                            + item.SellerNick + ", buyer=" + item.BuyerNick\n                            + ", generation=" + item.SessionGeneration\n                            + ", error=" + Safe(ex.Message, 220),\n                            50);\n                    }\n                }\n                else\n                {\n                    BuyerSessionAgentState deterministicState;\n                    if (_sessionAgent.TryGetGenerationState(\n                        item.SellerNick,\n                        item.BuyerNick,\n                        item.SessionGeneration,\n                        out deterministicState)\n                        && deterministicState == BuyerSessionAgentState.Failed)\n                    {\n                        Log.Info("固定规则发送失败后保留Failed终态，禁止升级Completed: seller="\n                            + item.SellerNick + ", buyer=" + item.BuyerNick\n                            + ", generation=" + item.SessionGeneration);\n                    }\n                    else\n                    {\n                        _sessionAgent.TryTransition(\n                            item.SellerNick,\n                            item.BuyerNick,\n                            item.SessionGeneration,\n                            BuyerSessionAgentState.Completed,\n                            "deterministic_rule_consumed");\n                    }\n                }\n            });\n        }\n\n'''
s = s[:start] + new_block + s[end:]
write(path, s)

path = "src/Bot/ChromeNs/DeterministicAutoReplyService.cs"
s = read(path)
s = replace_once(
    s,
    '''                // Never let a deterministic rule lock strand the buyer generation in Coalescing.\n                // The outer coordinator already serializes the same buyer; this inner guard is only\n                // a compatibility barrier and must fail open when an earlier send is unhealthy.''',
    '''                // This is the single authoritative deterministic-rule serialization gate.\n                // It must fail open after a bounded wait so one unhealthy fixed send never strands\n                // later generations in Coalescing. The coordinator intentionally has no outer gate.''',
    "deterministic gate comment")
write(path, s)

path = "tests/test_contextual_followup_coalescing_static.py"
s = read(path)
s = replace_once(
    s,
    '''def test_premerge_has_one_authoritative_gate_and_hard_liveness_boundary():\n    coordinator = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")\n    deterministic = read("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")\n    assert "_preMergeRuleGates" not in coordinator\n    assert "PreMergeRuleExecutionDeadlineMilliseconds = 20000" in coordinator\n    assert "Task.WhenAny(rulesTask, deadlineTask)" in coordinator\n    assert "已fail-open继续普通合并链路" in coordinator\n    assert "pre_merge_enqueue_exception" in coordinator\n    assert "gate.WaitAsync(1800)" in deterministic\n''',
    '''def test_premerge_has_one_authoritative_gate_and_no_late_send_ai_race():\n    coordinator = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")\n    deterministic = read("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")\n    assert "_preMergeRuleGates" not in coordinator\n    assert "PreMergeRuleExecutionDeadlineMilliseconds" not in coordinator\n    assert "Task.WhenAny(rulesTask, deadlineTask)" not in coordinator\n    assert "await DeterministicAutoReplyService.HandleBeforeMergeAsync(" in coordinator\n    assert "pre_merge_enqueue_exception" in coordinator\n    assert "gate.WaitAsync(1800)" in deterministic\n    assert "single authoritative deterministic-rule serialization gate" in deterministic\n''',
    "premerge test")
write(path, s)

print("safe premerge refinement applied")
