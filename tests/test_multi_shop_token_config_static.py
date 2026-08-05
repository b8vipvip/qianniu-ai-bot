from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_shop_tokens_are_dpapi_current_user_protected_and_shop_bound():
    source = read("src/Bot/ShopScope/ShopTokenStore.cs")
    assert "ProtectedData.Protect" in source
    assert "ProtectedData.Unprotect" in source
    assert "DataProtectionScope.CurrentUser" in source
    assert '"qianniu-ai-bot|control-plane-token|" + _shop.ShopKey' in source
    assert 'JsonProperty("protected_token")' in source
    assert 'JsonProperty("fingerprint")' in source
    assert "Array.Clear" in source
    assert 'GetConfigPath(_shop, "control-plane-token.json")' in source
    assert 'JsonProperty("token")' not in source


def test_clearing_shop_token_removes_current_backup_and_temp_copies():
    source = read("src/Bot/ShopScope/ShopTokenStore.cs")
    assert "DeleteIfExists(_path);" in source
    assert 'DeleteIfExists(_path + ".bak")' in source
    assert 'Path.GetFileName(_path) + ".tmp-*"' in source
    assert "Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly)" in source


def test_legacy_global_token_requires_explicit_ui_import():
    connection = read("src/Bot/ShopScope/ShopControlPlaneConnectionStore.cs")
    ui = read("src/Bot/Options/ShopBindingOptionsControl.cs")
    assert "GetLegacyGlobalToken" in connection
    assert 'Content = "导入旧全局令牌"' not in ui
    assert 'MakeButton("导入旧全局令牌"' in ui
    assert "ImportLegacy_Click" in ui
    assert "点击窗口底部“保存”后才会写入本店 DPAPI 令牌文件" in ui
    assert "_connection.SaveToken(candidate)" in ui


def test_shop_settings_file_is_schema_checked_atomic_and_shop_key_bound():
    source = read("src/Bot/ShopScope/ShopScopedSettingsStore.cs")
    assert 'Schema = "qianniu-ai-bot.shop-settings"' in source
    assert 'GetConfigPath(_shop, "settings.json")' in source
    assert "document.ShopKey, _shop.ShopKey" in source
    assert "File.Replace" in source
    assert "File.Copy(temp, path, true)" in source
    assert "ConcurrentDictionary<string, object>" in source


def test_persistent_params_routes_scoped_reads_and_writes_before_global_db():
    source = read("src/BotLib/Db/Sqlite/PersistentParams.cs")
    router = read("src/BotLib/Db/Sqlite/ScopedParamRouter.cs")
    assert "ScopedParamRouter.TryWrite(masterKey, subKey" in source
    assert "ScopedParamRouter.TryRead(masterKey, subKey" in source
    assert "if (ScopedParamRouter.TryWrite(masterKey, subKey, value)) return;" in source
    assert "public delegate bool TryReadHandler" in router
    assert "public delegate bool TryWriteHandler" in router


def test_only_explicit_ai_scope_is_shop_routed_in_this_pr():
    source = read("src/Bot/ShopScope/ShopScopedParamBridge.cs")
    assert '"ai"' in source
    assert '"feature"' not in source
    assert '"ai-control-plane"' not in source
    assert "ShopSettingsScope.Current" in source
    assert "store.TryGetString(masterKey" in source
    assert "store.SetString(masterKey" in source


def test_settings_window_scopes_ai_load_and_save_without_process_global_shop_state():
    source = read("src/Bot/Options/WndOption.xaml.cs")
    scope = read("src/Bot/ShopScope/ShopSettingsScope.cs")
    assert 'CreateOpTab("店铺绑定", new ShopBindingOptionsControl(Seller)' in source
    assert 'RunInShopScope(() => CreateOpTab("AI大模型设置"' in source
    assert "RunInShopScope(() => TraversalOpsAndDoAction" in source
    assert "using (ShopSettingsScope.Enter(_shop))" in source
    assert "AsyncLocal<ShopContext>" in scope
    assert "static ShopContext CurrentShop" not in scope


def test_buyer_reply_runtime_enters_shop_scope_at_the_single_handler_call():
    source = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    locator = read("src/Bot/ShopScope/ShopContextLocator.cs")
    assert "DispatchScopedAsync(burst, lease)" in source
    assert "ShopContextLocator.ResolveRuntimeBySellerNick" in source
    assert "using (ShopSettingsScope.Enter(shop))" in source
    assert "LegacyAiConfigurationGate.WaitAsync" in source
    assert "LegacyAiConfigurationGate.Release" in source
    assert "await _handler(lease)" in source
    assert "ResolveRuntimeBySellerNick" in locator
    assert "Profiles.GetOrCreate(ResolveBySellerNickCore" in locator


def test_runtime_scope_does_not_add_a_second_reflection_handler_wrapper():
    props = read("src/Bot/Directory.Build.props")
    coordinator = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    assert "ShopScopedBuyerRuntimeService.cs" not in props
    assert "BindingFlags" not in coordinator
    assert "ShopScopedBuyerRuntimeService" not in coordinator


def test_unstable_nickname_binding_requires_explicit_confirmation_and_keeps_window():
    ui = read("src/Bot/Options/ShopBindingOptionsControl.cs")
    window = read("src/Bot/Options/WndOption.xaml.cs")
    assert "!_shop.HasStableSellerId" in ui
    assert "_allowNicknameFallback.IsChecked != true" in ui
    assert "当前千牛身份没有 TargetId" in ui
    assert "临时按规范化昵称绑定" in ui
    assert "窗口已保留，请修正后重试" in window


def test_ui_only_claims_token_and_ai_model_api_isolation():
    ui = read("src/Bot/Options/ShopBindingOptionsControl.cs")
    assert "AI 模型/API 设置将写入" in ui
    assert "本阶段已隔离令牌与 AI 模型/API 设置" in ui
    assert "知识库、规则、Web 同步和云备份仍由后续阶段改造" in ui
    assert "AI/功能设置将写入" not in ui


def test_build_includes_new_bot_and_botlib_sources():
    bot_props = read("src/Bot/Directory.Build.props")
    botlib_props = read("src/BotLib/Directory.Build.props")
    for filename in (
        "ShopContextLocator.cs",
        "ShopControlPlaneConnectionStore.cs",
        "ShopScopedParamBridge.cs",
        "ShopScopedSettingsStore.cs",
        "ShopSettingsScope.cs",
        "ShopTokenStore.cs",
        "ShopBindingOptionsControl.cs",
    ):
        assert filename in bot_props
    assert "ScopedParamRouter.cs" in botlib_props


def test_web_cloud_and_full_backup_tokens_are_not_silently_switched_in_pr2():
    web = read("src/Bot/ChromeNs/BotWebConsoleSyncService.cs")
    knowledge = read("src/Bot/Knowledge/KnowledgeCloudSyncService.cs")
    backup = read("src/Bot/Knowledge/ClientDataCloudBackupService.cs")
    assert "ShopControlPlaneConnectionStore" not in web
    assert "ShopControlPlaneConnectionStore" not in knowledge
    assert "ShopControlPlaneConnectionStore" not in backup
