using BotLib;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;

namespace Bot
{
    public partial class App
    {
        private readonly object _botExternalWatchdogBootstrap =
            UpdateNs.BotProcessWatchdog.InitializeForApp();
    }
}

namespace Bot.UpdateNs
{
    /// <summary>
    /// An external PowerShell watcher survives native crashes / process kills that cannot be
    /// recovered by in-process exception handlers. Normal exits and updater-driven exits create
    /// an expected-exit marker so the watcher does not fight intentional shutdowns.
    /// </summary>
    internal static class BotProcessWatchdog
    {
        private static readonly object Sync = new object();
        private static bool _initialized;
        private static string _expectedExitMarker = string.Empty;

        public static object InitializeForApp()
        {
            lock (Sync)
            {
                if (_initialized) return new object();
                _initialized = true;
            }

            try
            {
                var runtimeDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "QianniuAiBot",
                    "runtime");
                Directory.CreateDirectory(runtimeDir);
                _expectedExitMarker = Path.Combine(
                    runtimeDir,
                    "expected-exit-" + Process.GetCurrentProcess().Id + ".marker");
                try { if (File.Exists(_expectedExitMarker)) File.Delete(_expectedExitMarker); } catch { }

                var scriptPath = Path.Combine(runtimeDir, "bot-process-watchdog.ps1");
                var logPath = Path.Combine(runtimeDir, "bot-process-watchdog.log");
                var restartState = Path.Combine(runtimeDir, "bot-watchdog-restarts.txt");
                File.WriteAllText(scriptPath, BuildScript(), new UTF8Encoding(false));

                var exe = Process.GetCurrentProcess().MainModule.FileName;
                var arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "
                    + Quote(scriptPath)
                    + " -CurrentPid " + Process.GetCurrentProcess().Id
                    + " -ExePath " + Quote(exe)
                    + " -WorkingDirectory " + Quote(AppDomain.CurrentDomain.BaseDirectory)
                    + " -ExpectedExitMarker " + Quote(_expectedExitMarker)
                    + " -RestartState " + Quote(restartState)
                    + " -WatchdogLog " + Quote(logPath);
                var watcher = Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = runtimeDir
                });
                if (watcher == null) throw new Exception("无法启动外部守护进程");

                if (Application.Current != null)
                {
                    Application.Current.Exit += delegate { MarkExpectedExit("normal-app-exit"); };
                    Application.Current.SessionEnding += delegate { MarkExpectedExit("windows-session-ending"); };
                }
                Log.Info("Bot外部进程守护已启动：异常退出将自动重启；正常退出和自动更新不会误拉起。watchdogPid="
                    + watcher.Id);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("启动Bot外部进程守护失败：" + ex.Message, 5);
            }
            return new object();
        }

        public static void MarkExpectedExit(string reason)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_expectedExitMarker)) return;
                File.WriteAllText(
                    _expectedExitMarker,
                    DateTime.Now.ToString("o") + " " + (reason ?? string.Empty),
                    new UTF8Encoding(false));
            }
            catch { }
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static string BuildScript()
        {
            return @"param(
    [Parameter(Mandatory=$true)][int]$CurrentPid,
    [Parameter(Mandatory=$true)][string]$ExePath,
    [Parameter(Mandatory=$true)][string]$WorkingDirectory,
    [Parameter(Mandatory=$true)][string]$ExpectedExitMarker,
    [Parameter(Mandatory=$true)][string]$RestartState,
    [Parameter(Mandatory=$true)][string]$WatchdogLog
)
$ErrorActionPreference = 'SilentlyContinue'
function Write-WatchdogLog([string]$Message) {
    try {
        $line = ('{0:yyyy-MM-dd HH:mm:ss.fff} {1}' -f (Get-Date), $Message)
        Add-Content -LiteralPath $WatchdogLog -Value $line -Encoding UTF8
    } catch {}
}
try {
    $p = Get-Process -Id $CurrentPid -ErrorAction SilentlyContinue
    if ($null -ne $p) { Wait-Process -Id $CurrentPid -ErrorAction SilentlyContinue }
} catch {}
Start-Sleep -Seconds 2
if (Test-Path -LiteralPath $ExpectedExitMarker) {
    Write-WatchdogLog ('expected exit pid=' + $CurrentPid + '; no restart')
    Remove-Item -LiteralPath $ExpectedExitMarker -Force -ErrorAction SilentlyContinue
    exit 0
}
try {
    $same = Get-CimInstance Win32_Process -Filter "Name='Bot.exe'" | Where-Object {
        $_.ExecutablePath -and ([string]::Equals($_.ExecutablePath, $ExePath, [System.StringComparison]::OrdinalIgnoreCase))
    }
    if ($same) {
        Write-WatchdogLog 'another Bot.exe instance already owns this install; no restart'
        exit 0
    }
} catch {}
$now = Get-Date
$recent = @()
try {
    if (Test-Path -LiteralPath $RestartState) {
        $recent = @(Get-Content -LiteralPath $RestartState | ForEach-Object {
            try { [DateTime]::Parse($_) } catch { $null }
        } | Where-Object { $_ -and $_ -gt $now.AddMinutes(-10) })
    }
} catch { $recent = @() }
if ($recent.Count -ge 5) {
    Write-WatchdogLog 'restart suppressed: reached 5 unexpected exits in 10 minutes'
    exit 2
}
$recent += $now
try { $recent | ForEach-Object { $_.ToString('o') } | Set-Content -LiteralPath $RestartState -Encoding UTF8 } catch {}
try {
    $newProcess = Start-Process -FilePath $ExePath -WorkingDirectory $WorkingDirectory -PassThru
    Write-WatchdogLog ('unexpected exit pid=' + $CurrentPid + '; restarted pid=' + $newProcess.Id)
    exit 0
} catch {
    Write-WatchdogLog ('restart failed: ' + $_.Exception.Message)
    exit 3
}
";
        }
    }
}
