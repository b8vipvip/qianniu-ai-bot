from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(relative: str) -> str:
    return (ROOT / relative).read_text(encoding="utf-8-sig")


def test_conversation_context_menu_can_open_exact_knowledge_entry():
    text = read("src/Bot/AssistWindow/Widget/Robot/CtlConversation.xaml.cs")
    assert 'Header = "查看"' in text
    assert "KnowledgeCenterWindow.ShowManagerAndLocate" in text
    assert "_seller" in text and "_buyer" in text
    assert "_question" in text and "_answer" in text


def test_knowledge_manager_locates_and_scrolls_to_entry():
    window = read("src/Bot/Knowledge/KnowledgeCenterWindow.cs")
    manager = read("src/Bot/Knowledge/KnowledgeManagerControl.cs")
    assert "NavigateToManager" in window
    assert "LocateEntry" in manager
    assert "TryFindLocalAnswer" in manager
    assert "ScrollIntoView" in manager
    assert "SelectedItem = selected" in manager


def test_incoming_timeline_is_recorded_before_burst_generation():
    text = read("src/Bot/ChromeNs/QN.cs")
    refresh = text.index("ConversationContextStore.RefreshAndRecord(message, messageText);")
    enqueue = text.index("_buyerMessageBurstCoordinator.Enqueue", refresh)
    answer = text.index("var answer = await Task.Run(() => MyOpenAI.GetAnswer", enqueue)
    assert refresh < enqueue < answer


def test_every_text_burst_is_checked_before_display_and_send():
    text = read("src/Bot/ChromeNs/QN.cs")
    generated = text.index("var answer = await Task.Run(() => MyOpenAI.GetAnswer")
    stale_check = text.index("if (!lease.IsCurrent)", generated)
    checked = text.index("ReplyDeduplicationService.EnsureDistinct", stale_check)
    stable = text.index("ConfirmStableAsync(450)", checked)
    displayed = text.index("Desk.Inst.AddConversation", stable)
    sent = text.index("SendTextWithRetryAsync(burst.BuyerNick, answer, 1)", displayed)
    remembered = text.index("ReplyDeduplicationService.RememberDelivered", sent)
    assert generated < stale_check < checked < stable < displayed < sent < remembered


def test_duplicate_service_regenerates_and_rejects_same_result():
    text = read("src/Bot/ChromeNs/ReplyDeduplicationService.cs")
    assert "SameAnswer(previousAnswer, result.Answer)" in text
    assert "MyOpenAI.CallStructuredChat" in text
    assert "SameAnswer(previousAnswer, regenerated)" in text
    assert "BuildSafeFallback" in text
    assert "本地知识库重答" in text


def test_contextual_matching_can_use_last_delivered_answer_without_remote_echo():
    text = read("src/Bot/ChromeNs/KnowledgeContextualReplyService.cs")
    assert "ReplyDeduplicationService.TryGetLastDelivered" in text
    assert "local-delivered-answer" in text
    assert "previousWasKnowledgeAnswer" in text
    assert "standaloneQuestion" in text


def test_new_services_are_included_in_windows_and_wpf_temp_builds():
    text = read("src/Directory.Build.targets")
    assert "ReplyDeduplicationService.cs" in text
    assert "BuyerMessageBurstCoordinator.cs" in text
    assert '<Compile Include="$(MSBuildProjectDirectory)\ChromeNs\ReplyDeduplicationService.cs" />' in text
    assert '<Compile Include="$(MSBuildProjectDirectory)\ChromeNs\BuyerMessageBurstCoordinator.cs" />' in text
