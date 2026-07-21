[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$PackagePath,

    [Parameter(Mandatory = $false)]
    [string]$InstallDir,

    [Parameter(Mandatory = $false)]
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step([string]$Message) {
    Write-Host "`n[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $Message" -ForegroundColor Cyan
}

function Resolve-BotPackage([string]$RequestedPath) {
    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        return (Resolve-Path -LiteralPath $RequestedPath).Path
    }

    $candidates = @()
    $searchDirs = @((Get-Location).Path, (Join-Path $env:USERPROFILE 'Downloads')) | Select-Object -Unique
    foreach ($dir in $searchDirs) {
        if (Test-Path -LiteralPath $dir) {
            $candidates += Get-ChildItem -LiteralPath $dir -File -Filter '*.zip' -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match 'qianniu-bot.*x64|x64.*qianniu-bot' }
        }
    }

    $latest = $candidates | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($null -eq $latest) {
        throw 'No qianniu-bot x64 ZIP was found. Use -PackagePath to specify the downloaded package.'
    }
    return $latest.FullName
}

function Get-RunningBotInstallDir {
    $process = Get-Process -Name 'Bot' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $process) {
        return $null
    }

    try {
        $exePath = $process.Path
    }
    catch {
        return $null
    }

    if ([string]::IsNullOrWhiteSpace($exePath)) {
        return $null
    }

    $binDir = Split-Path -Parent $exePath
    if ((Split-Path -Leaf $binDir) -ieq 'Bin') {
        return (Split-Path -Parent $binDir)
    }
    return $binDir
}

function Copy-DirectoryContents([string]$Source, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Source)) {
        return
    }

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
    Start-Sleep -Milliseconds 800
}

function Test-BotStarted([string]$ExpectedExe) {
    $deadline = (Get-Date).AddSeconds(12)
    while ((Get-Date) -lt $deadline) {
        foreach ($process in (Get-Process -Name 'Bot' -ErrorAction SilentlyContinue)) {
            try {
                if ($process.Path -and ([IO.Path]::GetFullPath($process.Path) -ieq [IO.Path]::GetFullPath($ExpectedExe))) {
                    return $true
                }
            }
            catch {
                continue
            }
        }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

$PackagePath = Resolve-BotPackage $PackagePath
$detectedInstallDir = Get-RunningBotInstallDir

if ([string]::IsNullOrWhiteSpace($InstallDir)) {
    if (-not [string]::IsNullOrWhiteSpace($detectedInstallDir)) {
        $InstallDir = $detectedInstallDir
        Write-Host "Detected running Bot install directory: $InstallDir" -ForegroundColor Green
    }
    else {
        $InstallDir = 'C:\QianniuAiBot'
        Write-Host "Running Bot.exe was not detected. Using default install directory: $InstallDir" -ForegroundColor Yellow
    }
}

$InstallDir = [IO.Path]::GetFullPath($InstallDir)
if (Test-Path -LiteralPath (Join-Path $InstallDir '.git')) {
    throw "Refusing to overwrite a Git source repository: $InstallDir. Use -InstallDir to specify the actual Bot runtime directory."
}

$packageHash = (Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupRoot = Join-Path $env:LOCALAPPDATA 'QianniuAiBotUpdater\backups'
$backupDir = Join-Path $backupRoot $timestamp
$programBackup = Join-Path $backupDir 'program'
$persistentData = Join-Path $env:LOCALAPPDATA 'QianniuAiBot\data'
$persistentBackup = Join-Path $backupDir 'persistent-data'
$tempDir = Join-Path $env:TEMP "qianniu-bot-update-$timestamp"
$oldProgramExisted = Test-Path -LiteralPath $InstallDir
$oldExe = Join-Path $InstallDir 'Bin\Bot.exe'

Write-Step 'Preparing update'
Write-Host "ZIP: $PackagePath"
Write-Host "SHA256: $packageHash"
Write-Host "Install directory: $InstallDir"
Write-Host "Persistent data directory: $persistentData"

try {
    Stop-BotProcesses

    Write-Step 'Backing up current program and persistent user data'
    New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
    if ($oldProgramExisted) {
        Copy-DirectoryContents $InstallDir $programBackup
    }
    if (Test-Path -LiteralPath $persistentData) {
        Copy-DirectoryContents $persistentData $persistentBackup
    }
    Write-Host "Backup directory: $backupDir" -ForegroundColor Green

    Write-Step 'Extracting and validating new package'
    if (Test-Path -LiteralPath $tempDir) {
        Remove-Item -LiteralPath $tempDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    Expand-Archive -LiteralPath $PackagePath -DestinationPath $tempDir -Force

    $packageRoot = $tempDir
    if (-not (Test-Path -LiteralPath (Join-Path $packageRoot 'Bin\Bot.exe'))) {
        $roots = @(Get-ChildItem -LiteralPath $tempDir -Directory | Where-Object {
            Test-Path -LiteralPath (Join-Path $_.FullName 'Bin\Bot.exe')
        })
        if ($roots.Count -ne 1) {
            throw 'Invalid ZIP layout: expected exactly one Bin\Bot.exe.'
        }
        $packageRoot = $roots[0].FullName
    }

    $newExe = Join-Path $packageRoot 'Bin\Bot.exe'
    if (-not (Test-Path -LiteralPath $newExe)) {
        throw "New package does not contain Bot.exe: $newExe"
    }

    # Preserve legacy runtime data so the new first-run migration can move it safely.
    $legacyData = Join-Path $InstallDir 'data'
    if (Test-Path -LiteralPath $legacyData) {
        Write-Host 'Legacy runtime data directory detected. Copying it into the new package for first-run migration.'
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

    if (-not $NoStart) {
        Write-Step 'Starting and validating Bot.exe'
        Start-Process -FilePath $installedExe -WorkingDirectory (Split-Path -Parent $installedExe)
        if (-not (Test-BotStarted $installedExe)) {
            throw 'New Bot.exe did not remain running. Automatic rollback will start.'
        }
        Write-Host 'Bot.exe started successfully.' -ForegroundColor Green
    }

    Write-Step 'Update completed successfully'
    Write-Host "Current program: $installedExe"
    Write-Host "Backup: $backupDir"
    Write-Host 'Persistent user data remains under %LocalAppData%\QianniuAiBot\data.'
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
    if ($oldProgramExisted -and (Test-Path -LiteralPath $oldExe) -and -not $NoStart) {
        Start-Process -FilePath $oldExe -WorkingDirectory (Split-Path -Parent $oldExe)
    }

    throw $failure
}
finally {
    if (Test-Path -LiteralPath $tempDir) {
        Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
