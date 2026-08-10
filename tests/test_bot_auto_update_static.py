from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def updater_code():
    paths = [
        "src/Bot/Update/BotUpdateModels.Fast.cs",
        "src/Bot/Update/BotUpdateService.Core.Fast.cs",
        "src/Bot/Update/BotUpdateService.Download.Fast.cs",
        "src/Bot/Update/BotUpdateService.Actions.Fast.cs",
        "src/Bot/Update/BotUpdateService.Network.Fast.cs",
        "src/Bot/Update/BotUpdateService.State.Fast.cs",
        "src/Bot/Update/BotUpdatePromptWindow.Fast.cs",
    ]
    return "\n".join(read(path) for path in paths)


def test_runtime_uses_control_plane_cache_then_single_github_latest_fallback():
    code = updater_code()
    props = read("src/Bot/Directory.Build.props")

    assert "/api/public/v1/bot-update/latest" in code
    assert "control-plane-cache-first" in code
    assert "ServiceMetadataTimeoutSeconds = 6" in code
    assert (
        "https://api.github.com/repos/b8vipvip/qianniu-ai-bot/releases/latest"
        in code
    )
    assert "releases?per_page=20" not in code
    assert "FetchLatestFromControlPlaneAsync" in code
    assert "FetchLatestFromGitHubAsync" in code
    assert "服务端更新缓存不可用" in code

    assert 'Name="UseOptimizedBotUpdateService"' in props
    assert 'Compile Remove="$(MSBuildThisFileDirectory)Update\\BotUpdateService.cs"' in props
    assert "Update\\BotUpdate*.Fast.cs" in props


def test_update_sha_survives_manifest_network_failure_and_shop_scoped_server_urls_are_discovered():
    network = read("src/Bot/Update/BotUpdateService.Network.Fast.cs")

    assert 'package.Value<string>("digest")' in network
    assert "NormalizeGitHubAssetDigest" in network
    assert "ExtractSha256FromReleaseNotes" in network
    assert "安装包 SHA-256" in network
    assert "release.Sha256 = IsSha256(fallbackSha)" in network
    assert "TryBackfillShaFromControlPlaneAsync" in network
    assert "GetConfiguredControlPlaneUrls" in network
    assert "ShopSettingsScope.Current" in network
    assert "new ShopProfileStore(paths)" in network
    assert "profile.ToContext()" in network
    assert "ShopControlPlaneConnectionStore.GetLegacyGlobalServerUrl()" in network
    assert "release.Sha256 = string.Empty;\n                Log.Info(\n                    \"读取更新SHA清单失败" not in network


def test_download_prefers_github_then_falls_back_to_verified_server_mirror():
    code = updater_code()

    assert 'AddDownloadSource(sources, "GitHub", release.PackageUrl)' in code
    assert 'AddDownloadSource(sources, "服务端镜像", release.MirrorUrl)' in code
    assert "DownloadConnectTimeoutSeconds = 20" in code
    assert "DownloadReadTimeoutSeconds = 45" in code
    assert "HashFile(partial)" in code
    assert "GitHub 与服务端镜像均下载失败" in code
    assert "release.Sha256" in code
    assert "SHA-256 校验信息" in code
    assert "release-info.json" in code


def test_server_caches_latest_metadata_and_verified_packages():
    module = read("services/api-control-plane/bot_update_cache.py")
    bootstrap = read("services/api-control-plane/bootstrap.py")
    env = read("services/api-control-plane/.env.example")

    assert 'GITHUB_LATEST_RELEASE_API = f"https://api.github.com/repos/{REPOSITORY}/releases/latest"' in module
    assert '@router.get("/api/public/v1/bot-update/latest"' in module
    assert '"/api/public/v1/bot-update/download/{tag}"' in module
    assert "METADATA_CACHE_SECONDS" in module
    assert "METADATA_STALE_SECONDS" in module
    assert "ensure_cached_package" in module
    assert "_hash_file(target)" in module
    assert "服务端镜像安装包 SHA-256 校验失败" in module
    assert "bot_update_cache.router" in bootstrap
    assert "bot_update_cache.init_bot_update_cache()" in bootstrap
    assert "BOT_UPDATE_METADATA_CACHE_SECONDS=300" in env


def test_update_defaults_are_safe_and_install_still_requires_confirmation():
    code = updater_code()
    ui = read("src/Bot/Options/BotUpdateOptionsControl.cs")
    assert "AutoCheck = true" in code
    assert "NotifyPopup = true" in code
    assert "AutoDownload = false" in code
    assert "CheckIntervalHours = 6" in code
    assert "安装前仍需人工确认" in ui
    assert "MessageBoxButton.YesNo" in code
    assert "Application.Current.Shutdown()" in code


def test_updater_backs_up_validates_restarts_and_rolls_back():
    script = read("src/Bot/Update/BotAutoUpdater.ps1")
    assert "Get-FileHash" in script
    assert "ExpectedSha256" in script
    assert "release-info.json" in script
    assert "Backing up current program and persistent data" in script
    assert "Starting automatic rollback" in script
    assert "Test-BotStarted" in script
    assert "Persistent user data remains" in script
    assert "Select-Object -Skip 8" in script


def test_updaters_keep_locked_install_root_and_retry_only_child_cleanup():
    auto = read("src/Bot/Update/BotAutoUpdater.ps1")
    manual = read("scripts/update-bot.ps1")

    for script in (auto, manual):
        assert "Clear-DirectoryContentsWithRetry" in script
        assert "Get-InstallProcessIds" in script
        assert "Get-CimInstance Win32_Process" in script
        assert "Install files are still busy; retry" in script
        assert "Get-PossibleDirectoryBlockers" in script
        assert "Clear-DirectoryContentsWithRetry $InstallDir" in script
        assert "Remove-Item -LiteralPath $InstallDir -Recurse -Force" not in script


def test_settings_page_and_startup_are_wired():
    app = read("src/Bot/App.xaml.cs")
    wnd = read("src/Bot/Options/WndOption.xaml.cs")
    options = read("src/Bot/Options/IOptions.cs")
    targets = read("src/Directory.Build.targets")
    props = read("src/Bot/Directory.Build.props")
    assert "BotUpdateService.Initialize()" in app
    assert "aboutUpdate = new BotUpdateOptionsControl();" in wnd
    assert 'AddPage("系统", "关于与更新"' in wnd
    assert "AboutUpdate" in options
    assert "Update\\BotUpdateService.cs" in targets
    assert "Update\\BotUpdate*.Fast.cs" in props
    assert "Options\\BotUpdateOptionsControl.cs" in targets
    assert "Update\\BotAutoUpdater.ps1" in targets
    assert "CopyToOutputDirectory" in targets


def test_release_workflow_publishes_stable_asset_and_manifest_from_verified_build():
    workflow = read(".github/workflows/publish-bot-auto-update-release.yml")
    assert 'workflows: ["Windows x64 release build"]' in workflow
    assert "github.event.workflow_run.conclusion == 'success'" in workflow
    assert "github.event.workflow_run.head_branch == 'master'" in workflow
    assert "actions: read" in workflow
    assert "contents: write" in workflow
    assert 'version="1.1.${run_number}"' in workflow
    assert 'tag="bot-v${version}"' in workflow
    assert "qianniu-bot-complete-x64-" in workflow
    assert "release-info.json" in workflow
    assert "qianniu-bot-x64.zip" in workflow
    assert "update.json" in workflow
    assert "sha256sum" in workflow
    assert "gh release create" in workflow
    assert "--latest" in workflow


def test_assembly_has_nonlegacy_update_baseline():
    assembly = read("src/Bot/Properties/AssemblyInfo.cs")
    assert 'AssemblyVersion("1.1.0.0")' in assembly
    assert 'AssemblyFileVersion("1.1.0.0")' in assembly
