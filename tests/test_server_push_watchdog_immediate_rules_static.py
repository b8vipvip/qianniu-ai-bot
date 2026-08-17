from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_fixed_rules_run_before_any_burst_quiet_delay_or_context_merge():
    coordinator = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    deterministic = read("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")

    enqueue = coordinator.index("public void Enqueue(BuyerMessageBurstItem item)")
    before_merge = coordinator.index("DeterministicAutoReplyService.HandleBeforeMergeAsync(item)", enqueue)
    enqueue_merge = coordinator.index("EnqueueForMerge(item)", before_merge)
    quiet_delay = coordinator.index("QuietDelayMilliseconds", enqueue_merge)
    assert enqueue < before_merge < enqueue_merge < quiet_delay

    # The post-merge dispatcher must not re-run deterministic rules.
    dispatch = coordinator.index("private async Task DispatchScopedAsync")
    assert "DeterministicAutoReplyService" not in coordinator[dispatch:]

    first = deterministic.index("FirstInquiryFixedReplyService.TryResolve(")
    off_hours = deterministic.index("TryResolveOffHours(out offHoursReply)")
    assert first < off_hours
    assert "SendTextWithRetryAsync(item.BuyerNick, answer, 3)" in deterministic
    assert "Do not let an AI/context reply overtake a failed mandatory greeting" in deterministic


def test_order_auto_reply_still_precedes_burst_merge_path():
    qn = read("src/Bot/ChromeNs/QN.cs")
    order = qn.index("OrderPlacedAutoReplyService.TryCreatePlan(")
    merge = qn.index("_buyerMessageBurstCoordinator.Enqueue(", order)
    assert order < merge


def test_server_push_replaces_client_periodic_version_polling():
    core = read("src/Bot/Update/BotUpdateService.Core.Fast.cs")
    state = read("src/Bot/Update/BotUpdateService.State.Fast.cs")
    client = read("src/Bot/Update/BotUpdateService.ServerPush.Fast.cs")
    server = read("services/api-control-plane/bot_update_push.py")
    bootstrap = read("services/api-control-plane/bootstrap.py")

    assert "RestartServerPushListener();" in core
    assert "clientAutoCheck=False" in core
    assert "CheckNowAsync(false)" not in state
    assert "new Timer(" not in state
    assert "text/event-stream" in client
    assert "ResponseHeadersRead" in client
    assert "StreamingResponse" in server
    assert "/api/public/v1/bot-update/events" in server
    assert "bot_update_push.router" in bootstrap


def test_download_progress_identifies_server_or_github_channel():
    download = read("src/Bot/Update/BotUpdateService.Download.Fast.cs")
    auto_window = read("src/Bot/Update/BotUpdateAutoProgressWindow.Fast.cs")
    prompt = read("src/Bot/Update/BotUpdatePromptWindow.Fast.cs")

    assert 'AddDownloadSource(sources, "服务器", release.MirrorUrl)' in download
    assert 'AddDownloadSource(sources, "GitHub", release.PackageUrl)' in download
    assert "CurrentDownloadPercent" in download
    assert "DownloadedBytes" in download
    assert "正在下载更新｜通道：" in download
    assert "下载通道：" in auto_window
    assert "下载通道：" in prompt


def test_external_watchdog_restarts_unexpected_process_exit_only():
    watchdog = read("src/Bot/Update/BotUpdateProcessWatchdog.Fast.cs")

    assert "Wait-Process -Id $CurrentPid" in watchdog
    assert "ExpectedExitMarker" in watchdog
    assert "MarkExpectedExit(\"normal-app-exit\")" in watchdog
    assert "Start-Process -FilePath $ExePath" in watchdog
    assert "5 unexpected exits in 10 minutes" in watchdog
    assert "Bot外部进程守护已启动" in watchdog


def test_handoff_strategy_is_built_directly_not_only_by_loaded_runtime_patch():
    wnd = read("src/Bot/Options/WndOption.xaml.cs")
    feature = read("src/Bot/Options/FeatureSettingsOptionsControl.cs")
    legacy_bridge = read("src/Bot/Update/BotUpdateHandoffSettingsUi.Fast.cs")

    # Regression from 1.1.756/1.1.758: WndOption itself still used the old visible title,
    # so opening settings on another page showed “消息通知” until the feature control Loaded.
    assert 'AddFeaturePage("回复与通知", "转人工策略"' in wnd
    assert 'OptionEnum.Notifications, "转人工策略")' in wnd
    assert 'AddFeaturePage("回复与通知", "消息通知"' not in wnd

    # The real underlying tab and controls must be reorganized during constructor execution.
    constructor = feature.index("public FeatureSettingsOptionsControl(string seller)")
    direct_migration = feature.index("OrganizeHandoffStrategyPage();", constructor)
    hide_tabs = feature.index("HideLegacyTabHeaders();", direct_migration)
    assert constructor < direct_migration < hide_tabs
    assert 'notificationTab.Header = "转人工策略"' in feature
    assert 'pageTitle = "转人工策略"' in feature
    assert '"_rulesEnabled"' in feature
    assert '"_manualKeywords"' in feature
    assert '"_noAutoKeywords"' in feature
    assert '"_handoffText"' in feature
    assert "在构造阶段将“启用转人工规则”及关键词/话术移动到“转人工策略”" in feature

    # Keep the older Loaded bridge only as compatibility fallback; correctness can no longer
    # depend on it executing after the user opens a feature page.
    assert "HandoffSettingsUiBridge" in legacy_bridge
