from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
QNRPA = ROOT / "src" / "Bot" / "ChromeNs" / "QNRpa.cs"
APP_LIFE = ROOT / "src" / "Bot" / "StartUp" / "AppLife.cs"
LOG_WRITER = ROOT / "src" / "BotLib" / "LogWriter.cs"


def _read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def test_stale_desktop_composer_is_cleared_only_after_target_buyer_is_proven():
    text = _read(QNRPA)
    method = text.index("ClearStaleComposerBeforeNewDraftAsync")
    target_read = text.index("var currentBuyer = await ReadCurrentBuyerNickAsync()", method)
    target_guard = text.index("if (!IsExpectedBuyer(buyer, currentBuyer))", target_read)
    clear_log = text.index("检测到电脑千牛输入框残留草稿", target_guard)
    ctrl_a = text.index("PressCtrlA();", clear_log)
    backspace = text.index("PressBackspace();", ctrl_a)
    reread = text.index("TryGetEditorText(out afterClear)", backspace)
    second_target = text.index("var buyerAfterClear = await ReadCurrentBuyerNickAsync()", reread)
    cdp_probe = text.index("残留草稿清理后确认", second_target)

    assert method < target_read < target_guard < clear_log < ctrl_a < backspace < reread < second_target < cdp_probe
    assert "输入框残留内容清空失败，已阻止追加写入" in text
    assert "清空后二次确认仍检测到非本次内容，已阻止覆盖/追加发送" in text


def test_exact_current_task_draft_is_never_deleted_or_duplicated():
    text = _read(QNRPA)
    method = text.index("ClearStaleComposerBeforeNewDraftAsync")
    exact = text.index("EditorMatchesExpectedText(currentText, expected)", method)
    clear_log = text.index("检测到电脑千牛输入框残留草稿", exact)
    concurrent_exact = text.index("残留草稿清理后并发草稿确认", clear_log)
    adopt = text.index("直接接管发送", concurrent_exact)

    assert method < exact < clear_log < concurrent_exact < adopt
    assert "输入框已有非本次Bot草稿，已阻止覆盖/追加发送" not in text


def test_text_send_clears_stale_buffer_before_new_cdp_insert_and_image_path_does_too():
    text = _read(QNRPA)
    setter = text.index("TrySetPlainTextByCdpAsync")
    cleanup = text.index("ClearStaleComposerBeforeNewDraftAsync(buyer, text)", setter)
    recheck = text.index("新任务写入前清空确认", cleanup)
    insert = text.index("InsertText2Inputbox(buyer, text)", recheck)
    assert setter < cleanup < recheck < insert

    image_method = text.index("OpenAndSendImageAsync")
    image_cleanup = text.index("ClearStaleComposerBeforeNewDraftAsync(buyer, string.Empty)", image_method)
    set_image = text.index("SetImage(image)", image_cleanup)
    assert image_method < image_cleanup < set_image


def test_runtime_logs_live_in_persistent_user_data_and_use_exact_1mib_segments():
    app = _read(APP_LIFE)
    log = _read(LOG_WRITER)

    assert 'Path.Combine(PathEx.UserDataRoot, "logs")' in app
    assert 'Path.Combine(logDir, "运行日志.txt")' in app
    assert "RuntimeLogSegmentBytes = 1024 * 1024" in app

    assert "DefaultSegmentBytes = 1024 * 1024" in log
    assert "TimeSpan.FromHours(24)" in log
    assert "RotateCurrentFile()" in log
    assert "DeleteExpiredSegments" in log
    assert "File.Move(FileName, destination)" in log
    assert "ClearFileIfNeed" not in log


def test_rotation_happens_before_a_normal_entry_would_cross_segment_cap():
    log = _read(LOG_WRITER)
    size_guard = log.index("currentBytes + entryBytes > _limitFileSize")
    flush = log.index("WriteBatch(batch);", size_guard)
    rotate = log.index("RotateCurrentFile();", flush)
    add = log.index("batch.Add(part);", rotate)
    assert size_guard < flush < rotate < add
