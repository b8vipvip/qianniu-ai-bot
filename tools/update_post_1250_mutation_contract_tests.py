from pathlib import Path

replacements = {
    "tests/test_1196_followup_runtime_hardening_static.py": [
        (
'''def test_unknown_composer_text_is_never_deleted_and_mutation_has_no_abandoned_timeout():
    q = read("src/Bot/ChromeNs/QNRpa.cs")
    method = q[q.index("ClearStaleComposerBeforeNewDraftAsync"):q.index("TrySetPlainTextByCdpAsync")]
    assert "IsOwnedDraftForBuyer(buyer, observedText)" in method
    assert "输入框存在所有权无法证明的内容，已保留" in method
    assert "RunUiMutationAsync" in method
    helper = q[q.index("private async Task<bool> RunUiMutationAsync"):q.index("private async Task<bool> HasExpectedDraftFastAsync")]
    assert "Task.WhenAny" not in helper
    assert "Task.Delay" not in helper
''',
'''def test_unknown_composer_text_is_never_deleted_and_timed_out_mutation_retains_exclusive_lease():
    q = read("src/Bot/ChromeNs/QNRpa.cs")
    method = q[q.index("ClearStaleComposerBeforeNewDraftAsync"):q.index("TrySetPlainTextByCdpAsync")]
    assert "IsOwnedDraftForBuyer(buyer, observedText)" in method
    assert "输入框存在所有权无法证明的内容，已保留" in method
    assert "RunUiMutationAsync" in method
    helper = q[q.index("private async Task<bool> RunUiMutationAsync"):q.index("private async Task<bool> HasExpectedDraftFastAsync")]
    assert "Task.WhenAny" in helper
    assert "Task.Delay(UiMutationTimeoutMs)" in helper
    assert "_activeUiMutationTask != null && !_activeUiMutationTask.IsCompleted" in helper
    assert "原任务保持独占租约直到安全退出" in helper
    assert "Thread.Abort" not in helper
'''
        )
    ],
    "tests/test_pr15_review_fixes_static.py": [
        (
'''    # A stale composer may only be mutated after target-buyer proof and exact Bot ownership proof.
    # Unknown/manual content is preserved fail-closed. The mutation itself is never abandoned on a
    # timeout, so Ctrl+A/Backspace cannot run later against a newer draft.
''',
'''    # A stale composer may only be mutated after target-buyer proof and exact Bot ownership proof.
    # Unknown/manual content is preserved fail-closed. Caller wait is bounded, while a timed-out
    # COM/UIA worker retains an exclusive lease until it exits so a second mutation cannot race it.
'''
        ),
        (
'''    mutation_helper = qnrpa[qnrpa.index("private async Task<bool> RunUiMutationAsync"):qnrpa.index("private async Task<bool> HasExpectedDraftFastAsync")]
    assert "Task.WhenAny" not in mutation_helper
    assert "Task.Delay" not in mutation_helper
''',
'''    mutation_helper = qnrpa[qnrpa.index("private async Task<bool> RunUiMutationAsync"):qnrpa.index("private async Task<bool> HasExpectedDraftFastAsync")]
    assert "Task.WhenAny" in mutation_helper
    assert "Task.Delay(UiMutationTimeoutMs)" in mutation_helper
    assert "_activeUiMutationTask != null && !_activeUiMutationTask.IsCompleted" in mutation_helper
    assert "原任务保持独占租约直到安全退出" in mutation_helper
    assert "Thread.Abort" not in mutation_helper
'''
        )
    ],
    "tests/test_stale_composer_cleanup_and_log_retention_static.py": [
        (
'''def test_exact_current_task_draft_is_adopted_and_side_effect_mutations_are_never_timed_out():
''',
'''def test_exact_current_task_draft_is_adopted_and_side_effect_mutations_use_bounded_exclusive_lease():
'''
        ),
        (
'''    mutation = text.index("private async Task<bool> RunUiMutationAsync")
    mutation_end = text.index("private async Task<bool> HasExpectedDraftFastAsync", mutation)
    assert "Task.WhenAny" not in text[mutation:mutation_end]
    assert "Task.Delay" not in text[mutation:mutation_end]
    assert "return await Task.Run(action).ConfigureAwait(false);" in text[mutation:mutation_end]
    assert "OwnedDraftRetention = TimeSpan.FromMinutes(30)" in text
''',
'''    mutation = text.index("private async Task<bool> RunUiMutationAsync")
    mutation_end = text.index("private async Task<bool> HasExpectedDraftFastAsync", mutation)
    helper = text[mutation:mutation_end]
    assert "Task.WhenAny" in helper
    assert "Task.Delay(UiMutationTimeoutMs)" in helper
    assert "_activeUiMutationTask != null && !_activeUiMutationTask.IsCompleted" in helper
    assert "原任务保持独占租约直到安全退出" in helper
    assert "Thread.Abort" not in helper
    assert "OwnedDraftRetention = TimeSpan.FromMinutes(30)" in text
'''
        )
    ],
}

for filename, pairs in replacements.items():
    path = Path(filename)
    text = path.read_text(encoding="utf-8-sig")
    for old, new in pairs:
        count = text.count(old)
        if count != 1:
            raise SystemExit(f"{filename}: expected exactly one stale contract anchor, found {count}")
        text = text.replace(old, new, 1)
    path.write_text(text, encoding="utf-8")
    print("updated", filename)
