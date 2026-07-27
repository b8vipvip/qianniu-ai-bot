from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_runtime_checks_only_stable_bot_releases_and_requires_sha256():
    code = read("src/Bot/Update/BotUpdateService.cs")
    assert "https://api.github.com/repos/b8vipvip/qianniu-ai-bot/releases?per_page=20" in code
    assert 'StartsWith("bot-v"' in code
    assert 'PackageAssetName = "qianniu-bot-x64.zip"' in code
    assert 'ManifestAssetName = "update.json"' in code
    assert "release.Sha256" in code
    assert "SHA-256 校验信息" in code
    assert "HashFile(partial)" in code
    assert "release-info.json" in code


def test_update_defaults_are_safe_and_install_still_requires_confirmation():
    code = read("src/Bot/Update/BotUpdateService.cs")
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
    assert "BotUpdateService.Initialize()" in app
    assert 'CreateOpTab("关于与更新", new BotUpdateOptionsControl(), style)' in wnd
    assert "AboutUpdate" in options
    assert "Update\\BotUpdateService.cs" in targets
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
