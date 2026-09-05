from pathlib import Path


def replace(path, old, new, count=1):
    p = Path(path)
    text = p.read_text(encoding="utf-8-sig")
    found = text.count(old)
    if found != count:
        raise SystemExit(f"{path}: expected {count} occurrences, found {found}: {old[:120]!r}")
    p.write_text(text.replace(old, new, count), encoding="utf-8")


replace("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs",
'''            if (!IsCurrent) return false;
            MarkReady("send_barrier_stable");
            return true;
''',
'''            if (!IsCurrent) return false;
            return MarkReady("send_barrier_stable");
''')

replace("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs",
'''            var conversationCtl = ResponseProgressTracker.BeginAnswer(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                detectedAt);
            var aiStartedAt = DateTime.Now;
''',
'''            var conversationCtl = ResponseProgressTracker.BeginAnswer(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                detectedAt);
            if (!lease.MarkGenerating("streaming_answer_started"))
            {
                ResponseProgressTracker.Cancel(burst.SellerNick, burst.BuyerNick,
                    "generation已失效，未进入答案生成");
                return;
            }
            var aiStartedAt = DateTime.Now;
''')
replace("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs",
'''                    ResponseProgressTracker.Fail(burst.SellerNick, burst.BuyerNick, timeout);
                    Log.Info("文本AI流总预算超时: buyer=" + burst.BuyerNick
''',
'''                    lease.MarkFailed("streaming_ai_budget_exhausted");
                    ResponseProgressTracker.Fail(burst.SellerNick, burst.BuyerNick, timeout);
                    Log.Info("文本AI流总预算超时: buyer=" + burst.BuyerNick
''')
replace("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs",
'''                ResponseProgressTracker.Fail(
                    burst.SellerNick,
                    burst.BuyerNick,
                    failure);
''',
'''                lease.MarkFailed("streaming_answer_invalid");
                ResponseProgressTracker.Fail(
                    burst.SellerNick,
                    burst.BuyerNick,
                    failure);
''')
replace("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs",
'''                var suppressed = "并发旧答案已抑制：" + relevanceReason;
                if (conversationCtl != null) conversationCtl.SetStatus(suppressed, false);
                ResponseProgressTracker.Cancel(burst.SellerNick, burst.BuyerNick, suppressed);
''',
'''                var suppressed = "并发旧答案已抑制：" + relevanceReason;
                lease.MarkCompleted("streaming_relevance_suppressed");
                if (conversationCtl != null) conversationCtl.SetStatus(suppressed, false);
                ResponseProgressTracker.Cancel(burst.SellerNick, burst.BuyerNick, suppressed);
''')
replace("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs",
'''            if (!autoSend)
            {
                if (conversationCtl != null) conversationCtl.SetStatus("仅生成答案", true);
                ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick);
                return;
            }

            if (!lease.IsCurrent)
            {
                const string invalidBeforeSend = "未发送：任务已因显式硬失效而取消";
                if (conversationCtl != null) conversationCtl.SetSendResult(false, invalidBeforeSend);
                ResponseProgressTracker.Cancel(burst.SellerNick, burst.BuyerNick, invalidBeforeSend);
                return;
            }

            var sendOk = await qn.SendTextWithRetryAsync(burst.BuyerNick, answer, 1);
''',
'''            if (!autoSend)
            {
                lease.MarkCompleted("streaming_answer_generated_only");
                if (conversationCtl != null) conversationCtl.SetStatus("仅生成答案", true);
                ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick);
                return;
            }

            if (!lease.MarkSending("streaming_send_started"))
            {
                const string invalidBeforeSend = "未发送：generation在发送前已失效";
                if (conversationCtl != null) conversationCtl.SetSendResult(false, invalidBeforeSend);
                ResponseProgressTracker.Cancel(burst.SellerNick, burst.BuyerNick, invalidBeforeSend);
                return;
            }

            var sendOk = await qn.SendTextWithRetryAsync(burst.BuyerNick, answer, 1);
''')
replace("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs",
'''            if (conversationCtl != null)
            {
                conversationCtl.SetSendResult(
''',
'''            if (sendOk)
                lease.MarkCompleted("streaming_send_completed");
            else
                lease.MarkFailed("streaming_send_failed");
            if (conversationCtl != null)
            {
                conversationCtl.SetSendResult(
''')

replace("src/Bot/ChromeNs/ResponseProgressTracker.cs",
'''    internal static class ResponseProgressTracker
    {
        private sealed class Entry
''',
'''    /// <summary>
    /// UI/metrics observer only. BuyerSessionAgent is the sole business lifecycle authority.
    /// A terminal/stale observation may remove or update an existing turn, but must never recreate
    /// a turn or fall through onto another generation.
    /// </summary>
    internal static class ResponseProgressTracker
    {
        private sealed class Entry
''')
replace("src/Bot/ChromeNs/ResponseProgressTracker.cs",
'''            var turnKey = ResolveOperationOrDetectedTurnKey(seller, buyer, detected);
            if (!Entries.ContainsKey(turnKey))
            {
                ObserveQuestion(seller, buyer, question, detected);
                turnKey = TurnKey(seller, buyer, detected);
            }
            OperationTurnKey.Value = turnKey;
            var control = SetExactQuestionByTurn(turnKey, question, detected);
            var answerStartedAt = detected;
            Entry entry;
            if (Entries.TryGetValue(turnKey, out entry) && entry != null)
            {
                lock (entry.Sync)
                {
                    entry.AnswerReadyAt = answerReadyAt;
                    entry.Answer = answer ?? string.Empty;
                    answerStartedAt = entry.AnswerStartedAt == DateTime.MinValue ? detected : entry.AnswerStartedAt;
                    control = entry.Control ?? control;
                }
            }
''',
'''            var turnKey = ResolveOperationOrDetectedTurnKey(seller, buyer, detected);
            Entry entry;
            if (string.IsNullOrWhiteSpace(turnKey)
                || !Entries.TryGetValue(turnKey, out entry)
                || entry == null)
            {
                Log.Info("已丢弃失效turn的迟到答案就绪观察，不重建回复进度: seller=" + seller
                    + ", buyer=" + buyer + ", turn=" + (turnKey ?? string.Empty));
                return null;
            }
            OperationTurnKey.Value = turnKey;
            var control = SetExactQuestionByTurn(turnKey, question, detected);
            var answerStartedAt = detected;
            lock (entry.Sync)
            {
                entry.AnswerReadyAt = answerReadyAt;
                entry.Answer = answer ?? string.Empty;
                answerStartedAt = entry.AnswerStartedAt == DateTime.MinValue ? detected : entry.AnswerStartedAt;
                control = entry.Control ?? control;
            }
''')
replace("src/Bot/ChromeNs/ResponseProgressTracker.cs",
'''        public static void Fail(string seller, string buyer, string detail)
        {
            MessageProcessingTraceService.RecordFailure(seller, buyer, detail);
            var turnKey = ResolveTerminalTurnKey(seller, buyer);
            Entry entry;
            if (!TryRemoveTurn(turnKey, out entry) || entry == null) return;
''',
'''        public static void Fail(string seller, string buyer, string detail)
        {
            var turnKey = ResolveTerminalTurnKey(seller, buyer);
            Entry entry;
            if (!TryRemoveTurn(turnKey, out entry) || entry == null) return;
            MessageProcessingTraceService.RecordFailure(seller, buyer, detail);
''')
replace("src/Bot/ChromeNs/ResponseProgressTracker.cs",
'''        public static void Cancel(string seller, string buyer, string detail)
        {
            MessageProcessingTraceService.RecordCancelled(seller, buyer, detail);
            var turnKey = ResolveTerminalTurnKey(seller, buyer);
            Entry entry;
            if (!TryRemoveTurn(turnKey, out entry) || entry == null) return;
''',
'''        public static void Cancel(string seller, string buyer, string detail)
        {
            var turnKey = ResolveTerminalTurnKey(seller, buyer);
            Entry entry;
            if (!TryRemoveTurn(turnKey, out entry) || entry == null) return;
            MessageProcessingTraceService.RecordCancelled(seller, buyer, detail);
''')
replace("src/Bot/ChromeNs/ResponseProgressTracker.cs",
'''            var operationKey = OperationTurnKey.Value;
            Entry operationEntry;
            if (!string.IsNullOrWhiteSpace(operationKey)
                && Entries.TryGetValue(operationKey, out operationEntry)
                && operationEntry != null
                && string.Equals(operationEntry.ConversationKey, conversationKey, StringComparison.Ordinal))
            {
                return operationKey;
            }
            return TurnKey(seller, buyer, NormalizeDetectedAt(detectedAt));
''',
'''            var operationKey = OperationTurnKey.Value;
            if (!string.IsNullOrWhiteSpace(operationKey)) return operationKey;
            return TurnKey(seller, buyer, NormalizeDetectedAt(detectedAt));
''')
replace("src/Bot/ChromeNs/ResponseProgressTracker.cs",
'''            var operationKey = OperationTurnKey.Value;
            Entry operationEntry;
            if (!string.IsNullOrWhiteSpace(operationKey)
                && Entries.TryGetValue(operationKey, out operationEntry)
                && operationEntry != null
                && string.Equals(operationEntry.ConversationKey, conversationKey, StringComparison.Ordinal))
            {
                return operationKey;
            }
            string currentKey;
''',
'''            var operationKey = OperationTurnKey.Value;
            if (!string.IsNullOrWhiteSpace(operationKey)) return operationKey;
            string currentKey;
''')

test = Path("tests/test_1272_knowledge_authority_and_turn_lifecycle_static.py")
test.write_text(r'''from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_human_edit_is_authoritative_provenance_not_ai_classification():
    service = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs")
    bridge = read("src/Bot/ChromeNs/KnowledgeEngineV2LearningBridge.cs")
    ui = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.xaml.cs")
    assert "internal static class KnowledgeV2AuthorityPolicy" in bridge
    assert 'source.IndexOf("人工修改"' in bridge
    assert "ApplyHumanConfirmed(record)" in bridge
    assert 'record.Status = "active"' in bridge
    assert 'record.Type = "business_fact"' in bridge
    assert "ApplyImportedLegacyProvenance(record, entry)" in bridge
    bridge_importer = bridge.split("namespace Bot.Knowledge")[0]
    assert 'record.Type = "learning_candidate";' not in bridge_importer
    assert 'record.Status = "candidate";' not in bridge_importer
    assert 'LearnAsync(e.Question, wnd.EditedAnswer, "人工修改"' in ui
    assert "KnowledgeEngineV2LearningBridge.SynchronizeSeller(e.Seller)" in ui
    assert "KnowledgeV2AuthorityPolicy.IsProductionApproved" in service


def test_runtime_and_test_console_share_v2_authority_policy():
    service = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs")
    assert "KnowledgeV2AuthorityPolicy.IsProductionApproved" in service
    assert ".Where(KnowledgeV2AuthorityPolicy.IsProductionApproved)" in service
    assert ".Select(KnowledgeV2AuthorityPolicy.NormalizeForRead)" in service
    assert "LearningCandidates = all.Count(KnowledgeV2AuthorityPolicy.IsCandidate)" in service


def test_reply_progress_cannot_resurrect_a_cancelled_turn():
    tracker = read("src/Bot/ChromeNs/ResponseProgressTracker.cs")
    assert "UI/metrics observer only. BuyerSessionAgent is the sole business lifecycle authority." in tracker
    ready = tracker[tracker.index("public static CtlConversation SetAnswerReady"):tracker.index("public static void MarkDeliveryConfirmed")]
    assert "ObserveQuestion(seller, buyer, question, detected);" not in ready
    assert "已丢弃失效turn的迟到答案就绪观察" in ready
    terminal = tracker[tracker.index("private static string ResolveTerminalTurnKey"):tracker.index("private static bool TryRemoveTurn")]
    assert "if (!string.IsNullOrWhiteSpace(operationKey)) return operationKey;" in terminal


def test_streaming_pipeline_uses_session_agent_as_terminal_owner():
    pipeline = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    coordinator = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    assert 'lease.MarkGenerating("streaming_answer_started")' in pipeline
    assert 'lease.MarkSending("streaming_send_started")' in pipeline
    assert 'lease.MarkCompleted("streaming_send_completed")' in pipeline
    assert 'lease.MarkFailed("streaming_send_failed")' in pipeline
    assert 'lease.MarkCompleted("streaming_answer_generated_only")' in pipeline
    assert 'return MarkReady("send_barrier_stable");' in coordinator
''', encoding="utf-8")
