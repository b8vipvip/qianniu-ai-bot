using BotLib;
using BotLib.Db.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bot.UpdateNs
{
    internal static partial class BotUpdateService
    {
        public static bool IsPackageReady(BotReleaseInfo release)
        {
            if (release == null || string.IsNullOrWhiteSpace(release.Sha256))
                return false;
            try
            {
                var path = Path.Combine(
                    GetUpdateRoot(),
                    SanitizeFileName(release.Version),
                    PackageAssetName);
                return File.Exists(path)
                    && HashFile(path).Equals(
                        release.Sha256,
                        StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static void ShowUpdatePrompt(BotReleaseInfo release, Window owner)
        {
            if (release == null) return;
            if (Interlocked.CompareExchange(ref _promptOpen, 1, 0) != 0) return;
            try
            {
                var window = new BotUpdatePromptWindow(release);
                if (owner != null && owner.IsVisible) window.Owner = owner;
                window.Closed += delegate
                {
                    Interlocked.Exchange(ref _promptOpen, 0);
                };
                window.Show();
                window.Activate();
            }
            catch
            {
                Interlocked.Exchange(ref _promptOpen, 0);
                throw;
            }
        }

        public static void SkipVersion(string version)
        {
            var settings = GetSettings();
            settings.SkippedVersion = (version ?? string.Empty).Trim();
            SaveSettings(settings);
        }

        public static void ClearSkippedVersion()
        {
            var settings = GetSettings();
            settings.SkippedVersion = string.Empty;
            SaveSettings(settings);
        }

        public static void OpenReleasesPage()
        {
            OpenUrl(
                LatestRelease == null
                || string.IsNullOrWhiteSpace(LatestRelease.HtmlUrl)
                    ? ReleasesPage
                    : LatestRelease.HtmlUrl);
        }

        public static void LaunchInstaller(
            string packagePath,
            BotReleaseInfo release)
        {
            if (release == null) throw new ArgumentNullException("release");
            if (string.IsNullOrWhiteSpace(packagePath)
                || !File.Exists(packagePath))
                throw new FileNotFoundException(
                    "更新安装包不存在。",
                    packagePath);
            if (!HashFile(packagePath).Equals(
                release.Sha256,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception(
                    "安装前 SHA-256 校验失败，已拒绝执行更新。");
            }

            var sourceScript = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "BotAutoUpdater.ps1");
            if (!File.Exists(sourceScript))
                throw new FileNotFoundException(
                    "自动更新程序 BotAutoUpdater.ps1 缺失。",
                    sourceScript);

            // Persist a provisional skip before handoff. If anything after this point fails,
            // the same bad target will not be auto-installed in a tight restart loop.
            QuarantineVersionForUpdaterHandoff(release.Version);

            var handoffRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QianniuAiBotUpdater",
                "handoff");
            Directory.CreateDirectory(handoffRoot);
            var handoffId = Process.GetCurrentProcess().Id
                + "-" + Guid.NewGuid().ToString("N");
            var handoffPath = Path.Combine(
                handoffRoot,
                "updater-handoff-" + handoffId + ".ready");
            var bootstrapLogPath = handoffPath + ".bootstrap.log";

            var tempUpdaterScript = Path.Combine(
                Path.GetTempPath(),
                "QianniuAiBotUpdater-"
                + Guid.NewGuid().ToString("N")
                + ".ps1");
            var tempBootstrapScript = Path.Combine(
                Path.GetTempPath(),
                "QianniuAiBotUpdaterBootstrap-"
                + Guid.NewGuid().ToString("N")
                + ".ps1");
            File.Copy(sourceScript, tempUpdaterScript, true);
            File.WriteAllText(
                tempBootstrapScript,
                BuildUpdaterBootstrapScript(),
                new UTF8Encoding(false));

            var installRoot = GetInstallRoot();
            var arguments =
                "-NoProfile -ExecutionPolicy Bypass -File "
                + QuoteArgument(tempBootstrapScript)
                + " -UpdaterScriptPath " + QuoteArgument(tempUpdaterScript)
                + " -PackagePath " + QuoteArgument(packagePath)
                + " -InstallDir " + QuoteArgument(installRoot)
                + " -ExpectedSha256 " + QuoteArgument(release.Sha256)
                + " -ExpectedVersion " + QuoteArgument(release.Version)
                + " -CurrentPid " + Process.GetCurrentProcess().Id
                + " -HandoffPath " + QuoteArgument(handoffPath);
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(tempBootstrapScript),
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process == null)
                throw new Exception("无法启动自动更新安全交接程序。");

            string handoffDetail;
            if (!WaitForUpdaterHandoff(
                    process,
                    handoffPath,
                    TimeSpan.FromSeconds(12),
                    out handoffDetail))
            {
                try
                {
                    if (!process.HasExited) process.Kill();
                }
                catch { }
                throw new Exception(
                    "自动更新器未完成安全交接，Bot 已保持运行，不会退出。"
                    + " detail=" + handoffDetail
                    + "；bootstrapLog=" + bootstrapLogPath);
            }

            // Only mark the current process as an expected updater exit after the bootstrap has
            // positively acknowledged that it is alive, has validated the package, and can recover
            // the old Bot if the inner updater fails.
            BotProcessWatchdog.MarkExpectedExit("auto-update:" + release.Version);
            Log.Info(
                "Bot自动更新安全交接已确认: version=" + release.Version
                + ", handoff=" + handoffPath
                + ", detail=" + handoffDetail
                + "；现在才允许当前Bot退出。若更新器异常，bootstrap/watchdog会恢复Bot。");

            if (Application.Current != null)
            {
                Application.Current.Dispatcher.BeginInvoke(
                    new Action(delegate
                    {
                        Application.Current.Shutdown();
                    }));
            }
        }

        private static bool WaitForUpdaterHandoff(
            Process process,
            string handoffPath,
            TimeSpan timeout,
            out string detail)
        {
            detail = string.Empty;
            var deadline = DateTime.UtcNow.Add(timeout);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (File.Exists(handoffPath))
                    {
                        try
                        {
                            detail = File.ReadAllText(handoffPath, Encoding.UTF8).Trim();
                        }
                        catch
                        {
                            detail = "handoff marker exists";
                        }
                        return true;
                    }
                }
                catch { }

                try
                {
                    if (process.HasExited)
                    {
                        detail = "bootstrap exited before acknowledgement; exitCode="
                            + process.ExitCode;
                        return false;
                    }
                }
                catch { }

                Thread.Sleep(100);
            }
            detail = "handoff acknowledgement timed out after "
                + ((int)timeout.TotalSeconds) + "s";
            return false;
        }

        private static string BuildUpdaterBootstrapScript()
        {
            return @"param(
    [Parameter(Mandatory=$true)][string]$UpdaterScriptPath,
    [Parameter(Mandatory=$true)][string]$PackagePath,
    [Parameter(Mandatory=$true)][string]$InstallDir,
    [Parameter(Mandatory=$true)][string]$ExpectedSha256,
    [Parameter(Mandatory=$true)][string]$ExpectedVersion,
    [Parameter(Mandatory=$true)][int]$CurrentPid,
    [Parameter(Mandatory=$true)][string]$HandoffPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$handoffSignaled = $false
$bootstrapLog = $HandoffPath + '.bootstrap.log'

function Write-BootstrapLog([string]$Message) {
    try {
        $dir = Split-Path -Parent $bootstrapLog
        if (-not [string]::IsNullOrWhiteSpace($dir)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }
        $line = ('{0:yyyy-MM-dd HH:mm:ss.fff} {1}' -f (Get-Date), $Message)
        Add-Content -LiteralPath $bootstrapLog -Value $line -Encoding UTF8
    } catch {}
}

function Quote-Arg([string]$Value) {
    return '"' + (($Value ?? '').Replace('"', '\"')) + '"'
}

function Test-BotRunning {
    $exe = Join-Path $InstallDir 'Bin\Bot.exe'
    try {
        foreach ($p in @(Get-CimInstance Win32_Process -Filter "Name='Bot.exe'" -ErrorAction SilentlyContinue)) {
            if ($p.ExecutablePath -and [string]::Equals([string]$p.ExecutablePath, $exe, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        }
    } catch {}
    return $false
}

function Start-BotIfNeeded {
    if (Test-BotRunning) {
        Write-BootstrapLog 'Bot already running; recovery start not required.'
        return $true
    }
    $exe = Join-Path $InstallDir 'Bin\Bot.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        Write-BootstrapLog ('Cannot recover Bot because executable is missing: ' + $exe)
        return $false
    }
    try {
        $p = Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) -PassThru
        Write-BootstrapLog ('Recovery start requested; pid=' + $p.Id)
    } catch {
        Write-BootstrapLog ('Recovery start failed: ' + $_.Exception.Message)
        return $false
    }
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        if (Test-BotRunning) { return $true }
        Start-Sleep -Milliseconds 500
    }
    Write-BootstrapLog 'Recovery Bot did not remain running within 15 seconds.'
    return $false
}

try {
    Write-BootstrapLog ('bootstrap started; pid=' + $PID + '; target=' + $ExpectedVersion)

    if (-not (Test-Path -LiteralPath $UpdaterScriptPath -PathType Leaf)) {
        throw ('Inner updater script does not exist: ' + $UpdaterScriptPath)
    }
    $PackagePath = [IO.Path]::GetFullPath($PackagePath)
    $InstallDir = [IO.Path]::GetFullPath($InstallDir)
    $ExpectedSha256 = $ExpectedSha256.Trim().ToUpperInvariant()
    if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
        throw ('Update package does not exist: ' + $PackagePath)
    }
    if (Test-Path -LiteralPath (Join-Path $InstallDir '.git')) {
        throw ('Refusing to overwrite a Git source repository: ' + $InstallDir)
    }
    $actualHash = (Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actualHash -ne $ExpectedSha256) {
        throw ('Bootstrap SHA256 verification failed. Expected ' + $ExpectedSha256 + ', actual ' + $actualHash)
    }
    Write-BootstrapLog ('preflight validated; package=' + $PackagePath + '; install=' + $InstallDir)

    $handoffDir = Split-Path -Parent $HandoffPath
    if (-not [string]::IsNullOrWhiteSpace($handoffDir)) {
        New-Item -ItemType Directory -Path $handoffDir -Force | Out-Null
    }
    @(
        'ready=true',
        ('bootstrap_pid=' + $PID),
        ('target=' + $ExpectedVersion),
        ('bootstrap_log=' + $bootstrapLog)
    ) | Set-Content -LiteralPath $HandoffPath -Encoding UTF8
    $handoffSignaled = $true
    Write-BootstrapLog ('handoff acknowledged; waiting for inner updater completion')

    $childArgs = '-NoProfile -ExecutionPolicy Bypass -File ' + (Quote-Arg $UpdaterScriptPath)
    $childArgs += ' -PackagePath ' + (Quote-Arg $PackagePath)
    $childArgs += ' -InstallDir ' + (Quote-Arg $InstallDir)
    $childArgs += ' -ExpectedSha256 ' + (Quote-Arg $ExpectedSha256)
    $childArgs += ' -ExpectedVersion ' + (Quote-Arg $ExpectedVersion)
    $childArgs += ' -CurrentPid ' + $CurrentPid

    $child = Start-Process -FilePath 'powershell.exe' -ArgumentList $childArgs -PassThru -WindowStyle Hidden
    if ($null -eq $child) { throw 'Unable to start inner updater process.' }
    Write-BootstrapLog ('inner updater started; pid=' + $child.Id)

    $deadline = (Get-Date).AddMinutes(5)
    while (-not $child.HasExited -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        try { $child.Refresh() } catch {}
    }
    if (-not $child.HasExited) {
        try { Stop-Process -Id $child.Id -Force -ErrorAction SilentlyContinue } catch {}
        throw 'Inner updater timed out after 5 minutes and was terminated.'
    }
    Write-BootstrapLog ('inner updater exited; exitCode=' + $child.ExitCode)
    if ($child.ExitCode -ne 0) {
        throw ('Inner updater failed with exit code ' + $child.ExitCode)
    }

    $botDeadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $botDeadline) {
        if (Test-BotRunning) {
            Write-BootstrapLog 'update handoff completed; Bot is running.'
            exit 0
        }
        Start-Sleep -Milliseconds 500
    }

    Write-BootstrapLog 'Inner updater returned success but Bot is not running; invoking recovery start.'
    if (-not (Start-BotIfNeeded)) {
        throw 'Inner updater returned success but Bot could not be started.'
    }
    Write-BootstrapLog 'Bot recovered after updater success without running process.'
    exit 0
}
catch {
    Write-BootstrapLog ('bootstrap failure: ' + $_.Exception.Message)
    if ($handoffSignaled) {
        $exitDeadline = (Get-Date).AddSeconds(20)
        while ((Get-Date) -lt $exitDeadline -and $null -ne (Get-Process -Id $CurrentPid -ErrorAction SilentlyContinue)) {
            Start-Sleep -Milliseconds 500
        }
        if (-not (Start-BotIfNeeded)) {
            Write-BootstrapLog 'bootstrap recovery could not start Bot.'
        } else {
            Write-BootstrapLog 'bootstrap recovery ensured Bot is running.'
        }
    } else {
        Write-BootstrapLog 'handoff was never acknowledged; current Bot should remain running.'
    }
    exit 1
}
";
        }

        private static void QuarantineVersionForUpdaterHandoff(string version)
        {
            version = NormalizeVersion(version);
            if (string.IsNullOrWhiteSpace(version)) return;
            try
            {
                lock (SettingsSync)
                {
                    var settings = _settings == null
                        ? LoadSettingsInternal()
                        : CloneSettings(_settings);
                    settings.SkippedVersion = version;
                    _settings = CloneSettings(settings);
                    SaveSettingsInternal(_settings);
                }
                Log.Info("自动更新交接保护已记录目标版本隔离: version=" + version);
            }
            catch (Exception ex)
            {
                Log.Info("记录自动更新交接保护失败，将依赖更新器失败隔离: version="
                    + version + ", error=" + Short(ex.Message, 180));
            }
        }

        private static void MaybeShowBackgroundPrompt(BotReleaseInfo release)
        {
            var settings = GetSettings();
            if (!settings.NotifyPopup) return;
            if (string.Equals(
                settings.SkippedVersion,
                release.Version,
                StringComparison.OrdinalIgnoreCase)) return;
            DateTime lastAt;
            if (string.Equals(
                    settings.LastNotifiedVersion,
                    release.Version,
                    StringComparison.OrdinalIgnoreCase)
                && DateTime.TryParse(settings.LastNotifiedAt, out lastAt)
                && lastAt >= DateTime.Now.AddHours(-24))
            {
                return;
            }

            settings.LastNotifiedVersion = release.Version;
            settings.LastNotifiedAt = DateTime.Now.ToString("o");
            SaveSettings(settings);
            if (Application.Current == null) return;
            Application.Current.Dispatcher.BeginInvoke(
                new Action(delegate
                {
                    Window owner = null;
                    try { owner = Application.Current.MainWindow; } catch { }
                    ShowUpdatePrompt(release, owner);
                }));
        }
    }
}
