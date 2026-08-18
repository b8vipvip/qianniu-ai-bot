from pathlib import Path


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def write(path: Path, text: str) -> None:
    path.write_text(text, encoding="utf-8-sig")


def replace_once(path: Path, old: str, new: str) -> None:
    text = read(path)
    if new in text:
        return
    if old not in text:
        raise SystemExit(f"expected block not found in {path}: {old[:120]!r}")
    write(path, text.replace(old, new, 1))


actions = Path("src/Bot/Update/BotUpdateService.Actions.Fast.cs")
replace_once(
    actions,
    "using System.IO;\nusing System.Linq;",
    "using System.IO;\nusing System.IO.Compression;\nusing System.Linq;",
)
replace_once(
    actions,
    '''            var sourceScript = Path.Combine(\n                AppDomain.CurrentDomain.BaseDirectory,\n                "BotAutoUpdater.ps1");\n            if (!File.Exists(sourceScript))\n                throw new FileNotFoundException(\n                    "自动更新程序 BotAutoUpdater.ps1 缺失。",\n                    sourceScript);\n\n''',
    '''            // Never bootstrap a new release with the updater bundled in the currently\n            // installed Bot. A defect in that old updater would otherwise make the fixed target\n            // release impossible to install. The verified target package owns its updater.\n\n''',
)
replace_once(
    actions,
    "            File.Copy(sourceScript, tempUpdaterScript, true);",
    "            ExtractTargetUpdaterFromPackage(packagePath, tempUpdaterScript, release);",
)
replace_once(
    actions,
    '''        private static void TryKillUpdaterBootstrap(Process process)\n        {''',
    '''        private static void ExtractTargetUpdaterFromPackage(\n            string packagePath,\n            string destinationPath,\n            BotReleaseInfo release)\n        {\n            if (release == null) throw new ArgumentNullException("release");\n            using (var stream = File.Open(\n                packagePath,\n                FileMode.Open,\n                FileAccess.Read,\n                FileShare.Read))\n            using (var archive = new ZipArchive(\n                stream,\n                ZipArchiveMode.Read,\n                false))\n            {\n                Func<ZipArchiveEntry, string> normalizedName = entry =>\n                    (entry == null ? string.Empty : entry.FullName ?? string.Empty)\n                        .Replace('\\\\', '/')\n                        .TrimStart('/');\n\n                var updaterEntries = archive.Entries\n                    .Where(entry => string.Equals(\n                        normalizedName(entry),\n                        "Bin/BotAutoUpdater.ps1",\n                        StringComparison.OrdinalIgnoreCase))\n                    .ToList();\n                if (updaterEntries.Count != 1)\n                {\n                    throw new Exception(\n                        "目标安装包必须且只能包含一个 Bin/BotAutoUpdater.ps1。actual="\n                        + updaterEntries.Count);\n                }\n\n                var releaseEntries = archive.Entries\n                    .Where(entry => string.Equals(\n                        normalizedName(entry),\n                        "release-info.json",\n                        StringComparison.OrdinalIgnoreCase))\n                    .ToList();\n                if (releaseEntries.Count != 1)\n                {\n                    throw new Exception(\n                        "目标安装包必须且只能包含一个 release-info.json。actual="\n                        + releaseEntries.Count);\n                }\n\n                JObject packageInfo;\n                using (var reader = new StreamReader(\n                    releaseEntries[0].Open(),\n                    Encoding.UTF8,\n                    true))\n                {\n                    packageInfo = JObject.Parse(reader.ReadToEnd());\n                }\n                var packageVersion = NormalizeVersion(\n                    packageInfo.Value<string>("version") ?? string.Empty);\n                if (!string.Equals(\n                        packageVersion,\n                        NormalizeVersion(release.Version),\n                        StringComparison.OrdinalIgnoreCase))\n                {\n                    throw new Exception(\n                        "目标安装包版本与发布清单不一致。expected="\n                        + release.Version + ", actual=" + packageVersion);\n                }\n\n                var packageCommit =\n                    (packageInfo.Value<string>("commit") ?? string.Empty).Trim();\n                if (!string.IsNullOrWhiteSpace(release.Commit)\n                    && !string.IsNullOrWhiteSpace(packageCommit)\n                    && !string.Equals(\n                        packageCommit,\n                        release.Commit.Trim(),\n                        StringComparison.OrdinalIgnoreCase))\n                {\n                    throw new Exception(\n                        "目标安装包提交与发布清单不一致。expected="\n                        + release.Commit + ", actual=" + packageCommit);\n                }\n\n                var destinationDir = Path.GetDirectoryName(destinationPath);\n                if (!string.IsNullOrWhiteSpace(destinationDir))\n                    Directory.CreateDirectory(destinationDir);\n                using (var input = updaterEntries[0].Open())\n                using (var output = File.Create(destinationPath))\n                {\n                    input.CopyTo(output);\n                }\n            }\n\n            if (!File.Exists(destinationPath)\n                || new FileInfo(destinationPath).Length < 256)\n            {\n                throw new Exception(\n                    "目标安装包中的 BotAutoUpdater.ps1 提取失败或内容异常。");\n            }\n        }\n\n        private static void TryKillUpdaterBootstrap(Process process)\n        {''',
)
replace_once(
    actions,
    '''    if ($actualHash -ne $ExpectedSha256) {\n        throw ('Bootstrap SHA256 verification failed. Expected ' + $ExpectedSha256 + ', actual ' + $actualHash)\n    }\n    Write-BootstrapLog ('preflight validated; package=' + $PackagePath + '; install=' + $InstallDir)''',
    '''    if ($actualHash -ne $ExpectedSha256) {\n        throw ('Bootstrap SHA256 verification failed. Expected ' + $ExpectedSha256 + ', actual ' + $actualHash)\n    }\n\n    $tokens = $null\n    $parseErrors = $null\n    [System.Management.Automation.Language.Parser]::ParseFile(\n        $UpdaterScriptPath,\n        [ref]$tokens,\n        [ref]$parseErrors\n    ) | Out-Null\n    if ($parseErrors.Count -gt 0) {\n        $parseSummary = @($parseErrors | ForEach-Object { $_.Message }) -join ' | '\n        throw ('Target updater PowerShell syntax validation failed: ' + $parseSummary)\n    }\n    Write-BootstrapLog 'target updater PowerShell syntax validated.'\n    Write-BootstrapLog ('preflight validated; package=' + $PackagePath + '; install=' + $InstallDir)''',
)

updater = Path("src/Bot/Update/BotAutoUpdater.ps1")
replace_once(
    updater,
    '''function Get-InstallProcessIds([string]$TargetInstallDir) {\n    $ids = @()\n    $ids += @(Get-Process -Name 'Bot' -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })\n\n    if (-not [string]::IsNullOrWhiteSpace($TargetInstallDir)) {''',
    '''function Get-InstallProcessIds([string]$TargetInstallDir) {\n    $ids = @()\n    # Never terminate unrelated Bot.exe instances from other installations. The handoff PID is\n    # explicit, and additional cleanup is scoped strictly to the target install directory.\n    if ($CurrentPid -gt 0 -and $CurrentPid -ne $PID) {\n        $ids += [int]$CurrentPid\n    }\n\n    if (-not [string]::IsNullOrWhiteSpace($TargetInstallDir)) {''',
)
replace_once(
    updater,
    '''function Test-BotStarted([string]$ExpectedExe) {\n    $deadline = (Get-Date).AddSeconds(15)\n    while ((Get-Date) -lt $deadline) {\n        foreach ($process in (Get-Process -Name 'Bot' -ErrorAction SilentlyContinue)) {\n            try {\n                if ($process.Path -and ([IO.Path]::GetFullPath($process.Path) -ieq [IO.Path]::GetFullPath($ExpectedExe))) {\n                    return $true\n                }\n            }\n            catch { }\n        }\n        Start-Sleep -Milliseconds 500\n    }\n    return $false\n}''',
    '''function Test-BotStarted([string]$ExpectedExe, [int]$ExpectedPid) {\n    $deadline = (Get-Date).AddSeconds(20)\n    $survivalStartedAt = $null\n    while ((Get-Date) -lt $deadline) {\n        $process = Get-Process -Id $ExpectedPid -ErrorAction SilentlyContinue\n        if ($null -eq $process) {\n            $survivalStartedAt = $null\n            Start-Sleep -Milliseconds 500\n            continue\n        }\n\n        $pathMatches = $false\n        try {\n            $pathMatches = $process.Path -and\n                ([IO.Path]::GetFullPath($process.Path) -ieq [IO.Path]::GetFullPath($ExpectedExe))\n        }\n        catch {\n            $pathMatches = $false\n        }\n        if (-not $pathMatches) {\n            return $false\n        }\n\n        if ($null -eq $survivalStartedAt) {\n            $survivalStartedAt = Get-Date\n        }\n        elseif (((Get-Date) - $survivalStartedAt).TotalSeconds -ge 8) {\n            return $true\n        }\n        Start-Sleep -Milliseconds 500\n    }\n    return $false\n}''',
)
replace_once(
    updater,
    '''    $installedReleaseInfo = Join-Path $InstallDir 'release-info.json'\n    if (-not (Test-Path -LiteralPath $installedReleaseInfo)) {\n        throw 'Installed package validation failed: release-info.json was not found.'\n    }\n\n    Write-Step 'Starting and validating new Bot.exe'\n    Start-Process -FilePath $installedExe -WorkingDirectory (Split-Path -Parent $installedExe)\n    if (-not (Test-BotStarted $installedExe)) {\n        throw 'New Bot.exe did not remain running. Automatic rollback will start.'\n    }''',
    '''    $installedReleaseInfo = Join-Path $InstallDir 'release-info.json'\n    if (-not (Test-Path -LiteralPath $installedReleaseInfo)) {\n        throw 'Installed package validation failed: release-info.json was not found.'\n    }\n    $installedInfo = Get-Content -LiteralPath $installedReleaseInfo -Raw | ConvertFrom-Json\n    if ([string]::IsNullOrWhiteSpace([string]$installedInfo.version) -or\n        ([string]$installedInfo.version -ne $ExpectedVersion)) {\n        throw "Installed package version mismatch. Expected $ExpectedVersion, actual $($installedInfo.version)"\n    }\n\n    Write-Step 'Starting and validating new Bot.exe'\n    $newBot = Start-Process -FilePath $installedExe -WorkingDirectory (Split-Path -Parent $installedExe) -PassThru\n    if ($null -eq $newBot) {\n        throw 'New Bot.exe process could not be created. Automatic rollback will start.'\n    }\n    Write-Host "Started target Bot PID=$($newBot.Id); requiring an 8 second survival window."\n    if (-not (Test-BotStarted $installedExe $newBot.Id)) {\n        throw 'New Bot.exe did not survive the required validation window. Automatic rollback will start.'\n    }''',
)

ui = Path("src/Bot/Options/BotUpdateOptionsControl.cs")
replace_once(
    ui,
    "        private readonly TextBlock _skipped;\n        private readonly TextBlock _buildCommit;",
    "        private readonly TextBlock _skipped;\n        private readonly TextBlock _failedInstall;\n        private readonly TextBlock _buildCommit;",
)
replace_once(
    ui,
    "            for (var i = 0; i < 8; i++) versionGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });",
    "            for (var i = 0; i < 9; i++) versionGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });",
)
replace_once(
    ui,
    '''            AddLabel(versionGrid, 7, "已跳过版本");\n            _skipped = AddValue(versionGrid, 7, "无");''',
    '''            AddLabel(versionGrid, 7, "用户跳过版本");\n            _skipped = AddValue(versionGrid, 7, "无");\n            AddLabel(versionGrid, 8, "安装失败隔离");\n            _failedInstall = AddValue(versionGrid, 8, "无");''',
)
replace_once(
    ui,
    '''            var clearSkip = CreateButton("取消跳过版本", false);\n            clearSkip.Click += (s, e) =>\n            {\n                BotUpdateService.ClearSkippedVersion();\n                LoadSettings();\n                _status.Text = "已取消跳过版本，下次检查时会重新提示。";\n            };''',
    '''            var clearSkip = CreateButton("清除跳过/失败隔离", false);\n            clearSkip.Click += (s, e) =>\n            {\n                BotUpdateService.ClearSkippedVersion();\n                LoadSettings();\n                _status.Text = "已清除用户跳过和安装失败隔离，并重新连接服务端版本通知。";\n            };''',
)
replace_once(
    ui,
    '''        private void LoadSkippedVersionOnly()\n        {\n            var settings = BotUpdateService.GetSettings();\n            _skipped.Text = string.IsNullOrWhiteSpace(settings.SkippedVersion) ? "无" : settings.SkippedVersion;\n        }''',
    '''        private void LoadSkippedVersionOnly()\n        {\n            var settings = BotUpdateService.GetSettings();\n            _skipped.Text = string.IsNullOrWhiteSpace(settings.UserSkippedVersion)\n                ? "无"\n                : settings.UserSkippedVersion;\n            _failedInstall.Text = string.IsNullOrWhiteSpace(settings.FailedInstallVersion)\n                ? "无"\n                : settings.FailedInstallVersion;\n        }''',
)

push = Path("src/Bot/Update/BotUpdateService.ServerPush.Fast.cs")
replace_once(
    push,
    '''            if (skipped)\n            {\n                result.Message += " 当前版本已设置为跳过。";\n                RaiseStatus(result);\n                return;\n            }''',
    '''            if (skipped)\n            {\n                var failedInstallBlocked = string.Equals(\n                    settings.FailedInstallVersion,\n                    release.Version,\n                    StringComparison.OrdinalIgnoreCase);\n                result.Message += failedInstallBlocked\n                    ? " 当前版本因上次安装失败处于隔离状态；不会自动循环重装，清除失败隔离后才会重试。"\n                    : " 当前版本已由用户设置为跳过。";\n                RaiseStatus(result);\n                return;\n            }''',
)

# Add a focused regression suite for the cross-version updater bootstrap and version-state semantics.
test_path = Path("tests/test_bot_update_target_updater_self_heal_static.py")
test_path.write_text('''from pathlib import Path\n\nROOT = Path(__file__).resolve().parents[1]\n\ndef read(path):\n    return (ROOT / path).read_text(encoding="utf-8-sig")\n\ndef test_installer_bootstraps_with_target_package_updater_not_current_updater():\n    actions = read("src/Bot/Update/BotUpdateService.Actions.Fast.cs")\n    assert "using System.IO.Compression;" in actions\n    assert "ExtractTargetUpdaterFromPackage(packagePath, tempUpdaterScript, release);" in actions\n    assert "new ZipArchive(" in actions\n    assert '"Bin/BotAutoUpdater.ps1"' in actions\n    assert '"release-info.json"' in actions\n    launch = actions.split("public static void LaunchInstaller", 1)[1].split("private static void ExtractTargetUpdaterFromPackage", 1)[0]\n    assert "AppDomain.CurrentDomain.BaseDirectory" not in launch\n    assert "File.Copy(sourceScript" not in launch\n\ndef test_target_updater_syntax_is_validated_before_handoff_ack():\n    actions = read("src/Bot/Update/BotUpdateService.Actions.Fast.cs")\n    parser = actions.index("System.Management.Automation.Language.Parser]::ParseFile")\n    ready = actions.index(") | Set-Content -LiteralPath $HandoffPath -Encoding UTF8")\n    assert parser < ready\n    assert "target updater PowerShell syntax validated." in actions\n\ndef test_auto_updater_only_stops_target_install_and_requires_survival_window():\n    script = read("src/Bot/Update/BotAutoUpdater.ps1")\n    assert "$ids += @(Get-Process -Name 'Bot'" not in script\n    assert "if ($CurrentPid -gt 0 -and $CurrentPid -ne $PID)" in script\n    assert "Test-BotStarted([string]$ExpectedExe, [int]$ExpectedPid)" in script\n    assert "TotalSeconds -ge 8" in script\n    assert "-PassThru" in script\n    assert "Installed package version mismatch" in script\n\ndef test_update_ui_separates_user_skip_from_failed_install_quarantine():\n    ui = read("src/Bot/Options/BotUpdateOptionsControl.cs")\n    push = read("src/Bot/Update/BotUpdateService.ServerPush.Fast.cs")\n    assert '"用户跳过版本"' in ui\n    assert '"安装失败隔离"' in ui\n    assert "settings.UserSkippedVersion" in ui\n    assert "settings.FailedInstallVersion" in ui\n    assert '"清除跳过/失败隔离"' in ui\n    assert "因上次安装失败处于隔离状态" in push\n''', encoding="utf-8")

print("patched target-updater bootstrap, updater survival validation, and skip/quarantine UI semantics")
