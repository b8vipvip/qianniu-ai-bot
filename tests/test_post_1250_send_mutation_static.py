from pathlib import Path


# Production regression contract for the 1.1.1250 residual-composer stall.
ROOT = Path(__file__).resolve().parents[1]
QNRPA = ROOT / "src" / "Bot" / "ChromeNs" / "QNRpa.cs"


def _source() -> str:
    return QNRPA.read_text(encoding="utf-8-sig")


def test_side_effecting_ui_mutation_has_bounded_wait_and_single_active_lease():
    source = _source()
    assert "private const int UiMutationTimeoutMs = 4500;" in source
    assert "private readonly object _uiMutationLock = new object();" in source
    assert "private Task<bool> _activeUiMutationTask;" in source
    method = source[source.index("private async Task<bool> RunUiMutationAsync"):source.index("private async Task<bool> HasExpectedDraftFastAsync")]
    assert "Task.WhenAny(" in method
    assert "Task.Delay(UiMutationTimeoutMs)" in method
    assert "_activeUiMutationTask != null && !_activeUiMutationTask.IsCompleted" in method
    assert "禁止并发启动新的草稿修改" in method
    assert "ReferenceEquals(_activeUiMutationTask, worker)" in method
    assert "Thread.Abort" not in method
    assert "worker.Wait(" not in method


def test_late_residual_draft_worker_revalidates_exact_owned_text_after_focus():
    source = _source()
    start = source.index("检测到同一买家的Bot历史残留草稿，准备安全清空")
    end = source.index("同一买家的Bot历史残留草稿已清空并二次确认为空", start)
    block = source[start:end]
    focus = block.index("!FocusEditor()")
    post_focus = block.index("string postFocusText;", focus)
    destructive = block.index("PressCtrlA();", post_focus)
    assert focus < post_focus < destructive
    between = block[post_focus:destructive]
    assert "TryGetEditorText(out postFocusText)" in between
    assert "EditorMatchesExpectedText(postFocusText, ownedText)" in between
    assert "IsOwnedDraftForBuyer(buyer, postFocusText)" in between
    assert "聚焦后检测到内容已变化，已取消清空" in between