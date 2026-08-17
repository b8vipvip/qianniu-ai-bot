from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_background_order_notification_gets_bounded_delayed_panel_recovery():
    source = read("src/Bot/ChromeNs/FirstInquiryDeliveryBridge.cs")

    assert "BackgroundOrderPanelRecoveryBridge" in source
    assert "EvShopRobotReceriveNewMessage += Qn_EvShopRobotReceriveNewMessage" in source
    assert "500, 1500, 3200, 6000, 10000, 16000, 24000, 36000" in source
    assert "TryRecoverVisibleOrderPanelForBackgroundProbeAsync" in source
    assert "BotActivityCoordinator.IsSafeToAutoFocus" in source
    assert "OpenChat(openNick)" in source
    assert "BuyerIdentityAliasService.AreEquivalent" in source
    assert "probeStartedAt == DateTime.MinValue ? now : probeStartedAt" in source
    assert "AddSeconds(-20)" in source
    assert 'Source = "千牛右侧订单面板后台延迟兜底"' in source
    assert "OrderEventHub.Publish(snapshot)" in source


def test_background_order_probe_never_publishes_unverified_or_historical_panel_rows():
    source = read("src/Bot/ChromeNs/FirstInquiryDeliveryBridge.cs")
    method = source[source.index("TryRecoverVisibleOrderPanelForBackgroundProbeAsync"):]

    assert "GetCurrentConversationID" in method
    assert "ExtractVisibleOrderPanelText" in method
    assert "ParseVisibleOrderPanelCandidates" in method
    assert "eventTime.Value > now.AddMinutes(2)" in method
    assert "eventTime.Value < freshFloor" in method
    assert "VisiblePanelUnsupportedStatuses" in method
    assert "currentBuyer" in method


def test_feature_settings_load_and_save_off_hours_inside_seller_shop_scope():
    source = read("src/Bot/Options/FeatureSettingsOptionsControl.cs")

    assert "using Bot.ShopScope;" in source
    assert "ResolveShopContext(Seller)" in source
    assert "ShopContextLocator.ResolveRuntimeBySellerNick" in source
    assert "ShopContextLocator.ResolveBySellerNick" in source
    assert "using (ShopSettingsScope.Enter(initialShop))" in source
    assert "using (ShopSettingsScope.Enter(shop))" in source
    assert "SyncOffHoursToLegacyControls();" in source
    assert "_saveAllMethod.Invoke(_legacyWindow, null);" in source
    assert "自动回复规则已按当前店铺作用域保存" in source


def test_off_hours_runtime_still_precedes_merge_and_ai():
    service = read("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")
    coordinator = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")

    first = service.index("FirstInquiryFixedReplyService.TryResolve")
    off_hours = service.index("TryResolveOffHours", first)
    assert first < off_hours
    assert '"下班自动回复"' in service[off_hours:]
    assert "return false;" in service[off_hours:]
    assert "DeterministicAutoReplyService.HandleBeforeMergeAsync(item)" in coordinator
    assert coordinator.index("HandleBeforeMergeAsync(item)") < coordinator.index("EnqueueForMerge(item)")
