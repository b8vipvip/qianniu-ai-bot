from pathlib import Path

path = Path("src/Bot/ChromeNs/QNRpa.cs")
source = path.read_text(encoding="utf-8-sig")

replacements = [
    (
        "        private const int UiActionTimeoutMs = 1800;\n",
        "        private const int UiActionTimeoutMs = 1800;\n"
        "        private const int UiMutationTimeoutMs = 4500;\n",
    ),
    (
        "        private static readonly TimeSpan OwnedDraftRetention = TimeSpan.FromMinutes(30);\n\n"
        "        private string _lastOwnedDraftBuyer = string.Empty;\n",
        "        private static readonly TimeSpan OwnedDraftRetention = TimeSpan.FromMinutes(30);\n\n"
        "        // A timed-out UI mutation must never be followed by a second concurrent mutation.\n"
        "        // Keep the original worker leased until it actually exits; retries fail fast meanwhile.\n"
        "        private readonly object _uiMutationLock = new object();\n"
        "        private Task<bool> _activeUiMutationTask;\n\n"
        "        private string _lastOwnedDraftBuyer = string.Empty;\n",
    ),
    (
'''        private async Task<bool> RunUiMutationAsync(Func<bool> action, string stage)
        {
            if (action == null) return false;
            try
            {
                // Side-effecting UI work must never be abandoned after a timeout. A timed-out
                // Task.Run can still press Ctrl+A/Backspace later and erase a newer draft.
                return await Task.Run(action).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SetSendFailure(stage, ex.Message);
                Log.Info(stage + "失败: " + ex.Message);
                return false;
            }
        }
''',
'''        private async Task<bool> RunUiMutationAsync(Func<bool> action, string stage)
        {
            if (action == null) return false;

            Task<bool> worker;
            lock (_uiMutationLock)
            {
                if (_activeUiMutationTask != null && !_activeUiMutationTask.IsCompleted)
                {
                    SetSendFailure(stage, "上一条UI草稿修改仍在安全退出，禁止并发启动新的草稿修改");
                    Log.Info(stage + "已快速失败：上一条UI草稿修改仍在后台退出，避免两个任务并发清空/覆盖输入框。seller="
                        + SellerNick);
                    return false;
                }

                try
                {
                    worker = Task.Run(action);
                    _activeUiMutationTask = worker;
                }
                catch (Exception ex)
                {
                    SetSendFailure(stage, ex.Message);
                    return false;
                }
            }

            var winner = await Task.WhenAny(worker, Task.Delay(UiMutationTimeoutMs)).ConfigureAwait(false);
            if (winner != worker)
            {
                // COM/UIA cannot be safely aborted. Release the send wait after a bounded interval,
                // but retain the single mutation lease until the original worker actually exits.
                SetSendFailure(stage, "UI草稿修改超过" + UiMutationTimeoutMs
                    + "ms，已停止等待；原任务保持独占租约直到安全退出");
                Log.Info(stage + "等待超时，发送链路已释放等待但保留单一UI修改租约: seller="
                    + SellerNick + ", timeoutMs=" + UiMutationTimeoutMs);
                worker.ContinueWith(completed =>
                {
                    lock (_uiMutationLock)
                    {
                        if (ReferenceEquals(_activeUiMutationTask, worker)) _activeUiMutationTask = null;
                    }
                    Log.Info(stage + "超时后的UI草稿修改任务已退出，可接受后续草稿任务: seller=" + SellerNick);
                }, TaskScheduler.Default);
                return false;
            }

            try
            {
                return await worker.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SetSendFailure(stage, ex.Message);
                Log.Info(stage + "失败: " + ex.Message);
                return false;
            }
            finally
            {
                lock (_uiMutationLock)
                {
                    if (ReferenceEquals(_activeUiMutationTask, worker)) _activeUiMutationTask = null;
                }
            }
        }
''',
    ),
    (
'''                    if (!TryGetEditorText(out latestText)
                        || !EditorMatchesExpectedText(latestText, ownedText)
                        || !IsOwnedDraftForBuyer(buyer, latestText)
                        || !FocusEditor())
                    {
                        return false;
                    }
                    PressCtrlA();
                    PressBackspace();
''',
'''                    if (!TryGetEditorText(out latestText)
                        || !EditorMatchesExpectedText(latestText, ownedText)
                        || !IsOwnedDraftForBuyer(buyer, latestText)
                        || !FocusEditor())
                    {
                        return false;
                    }

                    // Focus/UIA may have been delayed. Revalidate the exact Bot-owned text
                    // immediately before the destructive key sequence so a late worker cannot
                    // erase a newer Bot draft or a human-authored draft.
                    string postFocusText;
                    if (!TryGetEditorText(out postFocusText)
                        || !EditorMatchesExpectedText(postFocusText, ownedText)
                        || !IsOwnedDraftForBuyer(buyer, postFocusText))
                    {
                        Log.Info("Bot历史残留草稿清理在聚焦后检测到内容已变化，已取消清空: buyer=" + buyer);
                        return false;
                    }
                    PressCtrlA();
                    PressBackspace();
''',
    ),
]

for old, new in replacements:
    count = source.count(old)
    if count != 1:
        raise SystemExit(f"patch anchor count={count}, expected=1: {old[:80]!r}")
    source = source.replace(old, new, 1)

path.write_text(source, encoding="utf-8")
print("patched", path)
