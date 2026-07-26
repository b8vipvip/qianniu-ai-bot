[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,

    [Parameter(Mandatory = $true)]
    [string]$InstallDir,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedSha256,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedVersion,

    [Parameter(Mandatory = $true)]
    [int]$CurrentPid
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step([string]$Message) {
    Write-Host "`n[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $Message" -ForegroundColor Cyan
}

function Copy-DirectoryContents([string]$Source, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Source)) { return }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Get-ChildItem -LiteralPath $Source -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse -Force
    }
}

function Stop-BotProcesses {
    Get-Process -Name 'Bot' -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host "Stopping Bot.exe PID=$($_.Id)"
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 900
}

function Test-BotStarted([string]$ExpectedExe) {
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        foreach ($process in (Get-Process -Name 'Bot' -ErrorAction SilentlyContinue)) {
            try {
                if ($process.Path -and ([IO.Path]::GetFullPath($process.Path) -ieq [IO.Path]::GetFullPath($ExpectedExe))) {
                    return $true
                }
            }
            catch { }
        }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

$PackagePath = [IO.Path]::GetFullPath($PackagePath)
$InstallDir = [IO.Path]::GetFullPath($InstallDir)
$ExpectedSha256 = $ExpectedSha256.Trim().ToUpperInvariant()
if (-not (Test-Path -LiteralPath $PackagePath)) {
    throw "Update package does not exist: $PackagePath"
}
if (Test-Path -LiteralPath (Join-Path $InstallDir '.git')) {
    throw "Refusing to overwrite a Git source repository: $InstallDir"
}

$actualHash = (Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash.ToUpperInvariant()
if ($actualHash -ne $ExpectedSha256) {
    throw "SHA256 verification failed. Expected $ExpectedSha256, actual $actualHash"
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$updaterRoot = Join-Path $env:LOCALAPPDATA 'QianniuAiBotUpdater'
$backupRoot = Join-Path $updaterRoot 'backups'
$backupDir = Join-Path $backupRoot $timestamp
$programBackup = Join-Path $backupDir 'program'
$persistentData = Join-Path $env:LOCALAPPDATA 'QianniuAiBot\data'
$persistentBackup = Join-Path $backupDir 'persistent-data'
$tempDir = Join-Path $env:TEMP "qianniu-bot-auto-update-$timestamp"
$logDir = Join-Path $updaterRoot 'logs'
$logPath = Join-Path $logDir "auto-update-$timestamp.log"
$oldProgramExisted = Test-Path -LiteralPath $InstallDir
$oldExe = Join-Path $InstallDir 'Bin\Bot.exe'

New-Item -ItemType Directory -Path $logDir -Force | Out-Null
try { Start-Transcript -Path $logPath -Force | Out-Null } catch { }

try {
    Write-Step "Waiting for Bot.exe PID=$CurrentPid to exit"
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        if ($null -eq (Get-Process -Id $CurrentPid -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Milliseconds 350
    }
    if ($null -ne (Get-Process -Id $CurrentPid -ErrorAction SilentlyContinue)) {
        Write-Host 'Bot did not exit in time; stopping it now.' -ForegroundColor Yellow
        Stop-Process -Id $CurrentPid -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 800
    }
    Stop-BotProcesses

    Write-Step "Preparing Bot update to version $ExpectedVersion"
    Write-Host "Package: $PackagePath"
    Write-Host "SHA256: $actualHash"
    Write-Host "Install directory: $InstallDir"
    Write-Host "Persistent data: $persistentData"
    Write-Host "Log: $logPath"

    Write-Step 'Backing up current program and persistent data'
    New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
    if ($oldProgramExisted) { Copy-DirectoryContents $InstallDir $programBackup }
    if (Test-Path -LiteralPath $persistentData) { Copy-DirectoryContents $persistentData $persistentBackup }
    Write-Host "Backup directory: $backupDir" -ForegroundColor Green

    Write-Step 'Extracting and validating package'
    if (Test-Path -LiteralPath $tempDir) { Remove-Item -LiteralPath $tempDir -Recurse -Force }
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    Expand-Archive -LiteralPath $PackagePath -DestinationPath $tempDir -Force

    $packageRoot = $tempDir
    if (-not (Test-Path -LiteralPath (Join-Path $packageRoot 'Bin\Bot.exe'))) {
        $roots = @(Get-ChildItem -LiteralPath $tempDir -Directory | Where-Object {
            Test-Path -LiteralPath (Join-Path $_.FullName 'Bin\Bot.exe')
        })
        if ($roots.Count -ne 1) {
            throw 'Invalid package layout: expected exactly one Bin\Bot.exe.'
        }
        $packageRoot = $roots[0].FullName
    }

    $newExe = Join-Path $packageRoot 'Bin\Bot.exe'
    if (-not (Test-Path -LiteralPath $newExe)) {
        throw "Package does not contain Bot.exe: $newExe"
    }
    $releaseInfoPath = Join-Path $packageRoot 'release-info.json'
    if (-not (Test-Path -LiteralPath $releaseInfoPath)) {
        throw 'Package does not contain release-info.json.'
    }
    $releaseInfo = Get-Content -LiteralPath $releaseInfoPath -Raw | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace([string]$releaseInfo.version) -or ([string]$releaseInfo.version -ne $ExpectedVersion)) {
        throw "Package version mismatch. Expected $ExpectedVersion, actual $($releaseInfo.version)"
    }

    $legacyData = Join-Path $InstallDir 'data'
    if (Test-Path -LiteralPath $legacyData) {
        Write-Host 'Legacy runtime data detected; preserving it for first-run migration.'
        Copy-DirectoryContents $legacyData (Join-Path $packageRoot 'data')
    }

    Write-Step 'Replacing program files'
    if (Test-Path -LiteralPath $InstallDir) {
        Remove-Item -LiteralPath $InstallDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    Copy-DirectoryContents $packageRoot $InstallDir

    $installedExe = Join-Path $InstallDir 'Bin\Bot.exe'
    if (-not (Test-Path -LiteralPath $installedExe)) {
        throw 'Installed package validation failed: Bin\Bot.exe was not found.'
    }
    $installedReleaseInfo = Join-Path $InstallDir 'release-info.json'
    if (-not (Test-Path -LiteralPath $installedReleaseInfo)) {
        throw 'Installed package validation failed: release-info.json was not found.'
    }

    Write-Step 'Starting and validating new Bot.exe'
    Start-Process -FilePath $installedExe -WorkingDirectory (Split-Path -Parent $installedExe)
    if (-not (Test-BotStarted $installedExe)) {
        throw 'New Bot.exe did not remain running. Automatic rollback will start.'
    }

    Write-Step "Update to $ExpectedVersion completed successfully"
    Write-Host "Current program: $installedExe" -ForegroundColor Green
    Write-Host "Backup: $backupDir"
    Write-Host 'Persistent user data remains under %LocalAppData%\QianniuAiBot\data.'

    if (Test-Path -LiteralPath $backupRoot) {
        Get-ChildItem -LiteralPath $backupRoot -Directory | Sort-Object Name -Descending | Select-Object -Skip 8 | ForEach-Object {
            Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
catch {
    $failure = $_
    Write-Host "`nUpdate failed: $($failure.Exception.Message)" -ForegroundColor Red
    Write-Host 'Starting automatic rollback...' -ForegroundColor Yellow

    Stop-BotProcesses
    if (Test-Path -LiteralPath $InstallDir) {
        Remove-Item -LiteralPath $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($oldProgramExisted -and (Test-Path -LiteralPath $programBackup)) {
        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
        Copy-DirectoryContents $programBackup $InstallDir
    }
    if (Test-Path -LiteralPath $persistentBackup) {
        if (Test-Path -LiteralPath $persistentData) {
            Remove-Item -LiteralPath $persistentData -Recurse -Force -ErrorAction SilentlyContinue
        }
        New-Item -ItemType Directory -Path $persistentData -Force | Out-Null
        Copy-DirectoryContents $persistentBackup $persistentData
    }
    if ($oldProgramExisted -and (Test-Path -LiteralPath $oldExe)) {
        Start-Process -FilePath $oldExe -WorkingDirectory (Split-Path -Parent $oldExe)
    }

    Write-Host "Rollback completed. Log: $logPath" -ForegroundColor Yellow
    Start-Sleep -Seconds 8
    throw $failure
}
finally {
    if (Test-Path -LiteralPath $tempDir) {
        Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    try { Stop-Transcript | Out-Null } catch { }
    try { Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue } catch { }
}
