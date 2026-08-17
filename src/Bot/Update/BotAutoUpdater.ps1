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
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) { return }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Get-ChildItem -LiteralPath $Source -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse -Force
    }
}

function Get-DirectoryFingerprint([string]$Root) {
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { return @() }

    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $prefix = $rootFull + '\'
    $entries = @()
    foreach ($item in @(Get-ChildItem -LiteralPath $rootFull -Recurse -Force)) {
        $full = [IO.Path]::GetFullPath($item.FullName)
        $relative = $full.Substring($prefix.Length)
        if ($item.PSIsContainer) {
            $entries += "D|$relative"
            continue
        }

        $hash = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToUpperInvariant()
        $entries += "F|$relative|$([int64]$item.Length)|$hash"
    }
    return @($entries | Sort-Object)
}

function Assert-DirectoryCopyMatches([string]$Source, [string]$Backup, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "Backup validation source is missing: $Label ($Source)"
    }
    if (-not (Test-Path -LiteralPath $Backup -PathType Container)) {
        throw "Backup validation destination is missing: $Label ($Backup)"
    }

    $sourceState = @(Get-DirectoryFingerprint $Source)
    $backupState = @(Get-DirectoryFingerprint $Backup)
    if ($sourceState.Count -ne $backupState.Count) {
        throw "Backup validation failed for $Label: entry count differs ($($sourceState.Count) != $($backupState.Count))."
    }

    for ($i = 0; $i -lt $sourceState.Count; $i++) {
        if (-not [string]::Equals([string]$sourceState[$i], [string]$backupState[$i], [StringComparison]::Ordinal)) {
            throw "Backup validation failed for $Label at entry $i."
        }
    }
}

function Test-BackupComplete([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    if ($Path.EndsWith('.partial', [StringComparison]::OrdinalIgnoreCase)) { return $false }
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return $false }

    $marker = Join-Path $Path '.complete'
    $manifestPath = Join-Path $Path 'backup-manifest.json'
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) { return $false }
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { return $false }

    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        return ([int]$manifest.schema -eq 1)
    }
    catch {
        return $false
    }
}

function Restore-PersistentData([string]$CompleteBackupDir, [string]$PersistentRoot) {
    if (-not (Test-BackupComplete $CompleteBackupDir)) {
        throw "Refusing to restore persistent data from an incomplete backup: $CompleteBackupDir"
    }

    $manifestPath = Join-Path $CompleteBackupDir 'backup-manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $persistentBackupRoot = Join-Path $CompleteBackupDir 'persistent'
    $allowedNames = @('data', 'global', 'shops')

    foreach ($entry in @($manifest.persistent)) {
        $name = [string]$entry.name
        if ($allowedNames -notcontains $name) {
            throw "Backup manifest contains an unsupported persistent directory: $name"
        }

        $destination = Join-Path $PersistentRoot $name
        $source = Join-Path $persistentBackupRoot $name
        if ([bool]$entry.existed) {
            if (-not (Test-Path -LiteralPath $source -PathType Container)) {
                throw "Completed backup is missing persistent directory: $name"
            }
            if (Test-Path -LiteralPath $destination) {
                Remove-Item -LiteralPath $destination -Recurse -Force
            }
            Copy-DirectoryContents $source $destination
            Assert-DirectoryCopyMatches $source $destination "persistent/$name restore"
        }
        elseif (Test-Path -LiteralPath $destination) {
            Remove-Item -LiteralPath $destination -Recurse -Force
        }
    }
}

function Get-InstallProcessIds([string]$TargetInstallDir) {
    $ids = @()
    $ids += @(Get-Process -Name 'Bot' -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })

    if (-not [string]::IsNullOrWhiteSpace($TargetInstallDir)) {
        $root = [IO.Path]::GetFullPath($TargetInstallDir).TrimEnd('\') + '\'
        try {
            $ids += @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
                if ($null -eq $_ -or [string]::IsNullOrWhiteSpace([string]$_.ExecutablePath)) { return $false }
                try {
                    $exe = [IO.Path]::GetFullPath([string]$_.ExecutablePath)
                    return $exe.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)
                }
                catch {
                    return $false
                }
            } | ForEach-Object { [int]$_.ProcessId })
        }
        catch {
            Write-Host "Unable to enumerate processes under install directory: $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }

    return @($ids | Where-Object { $_ -gt 0 -and $_ -ne $PID } | Sort-Object -Unique)
}

function Stop-BotProcesses([string]$TargetInstallDir) {
    $ids = @(Get-InstallProcessIds $TargetInstallDir)
    foreach ($id in $ids) {
        $process = Get-Process -Id $id -ErrorAction SilentlyContinue
        if ($null -eq $process) { continue }
        Write-Host "Stopping process PID=$id Name=$($process.ProcessName)"
        Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
    }

    $deadline = (Get-Date).AddSeconds(12)
    while ((Get-Date) -lt $deadline) {
        $alive = @($ids | Where-Object { $null -ne (Get-Process -Id $_ -ErrorAction SilentlyContinue) })
        if ($alive.Count -eq 0) { return }
        Start-Sleep -Milliseconds 300
    }

    foreach ($id in $ids) {
        Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 700
}

function Get-PossibleDirectoryBlockers([string]$Path) {
    $needle = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    try {
        return @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
            if ($null -eq $_ -or [int]$_.ProcessId -eq $PID) { return $false }
            $exe = [string]$_.ExecutablePath
            $command = [string]$_.CommandLine
            return ($exe.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) -or
                ($command.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0)
        } | ForEach-Object {
            "PID=$($_.ProcessId) Name=$($_.Name) Exe=$($_.ExecutablePath)"
        })
    }
    catch {
        return @()
    }
}

function Clear-DirectoryContentsWithRetry([string]$Path, [int]$MaxAttempts = 24) {
    if (-not (Test-Path -LiteralPath $Path)) { return }

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        Stop-BotProcesses $Path
        $failures = @()
        foreach ($item in @(Get-ChildItem -LiteralPath $Path -Force -ErrorAction SilentlyContinue)) {
            try {
                Remove-Item -LiteralPath $item.FullName -Recurse -Force -ErrorAction Stop
            }
            catch {
                $failures += "$($item.FullName): $($_.Exception.Message)"
            }
        }

        $remaining = @(Get-ChildItem -LiteralPath $Path -Force -ErrorAction SilentlyContinue)
        if ($remaining.Count -eq 0) { return }

        $summary = if ($failures.Count -gt 0) { $failures -join ' | ' } else { ($remaining.FullName -join ' | ') }
        Write-Host "Install files are still busy; retry $attempt/$MaxAttempts. $summary" -ForegroundColor Yellow
        Start-Sleep -Milliseconds ([Math]::Min(1500, 250 + ($attempt * 75)))
    }

    $blockers = @(Get-PossibleDirectoryBlockers $Path)
    $detail = if ($blockers.Count -gt 0) { $blockers -join '; ' } else { 'No executable-path blocker was found. Close terminals, Explorer windows, antivirus scans, or other programs using this directory.' }
    throw "Unable to clear install directory contents after $MaxAttempts attempts: $Path. $detail"
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

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$updaterRoot = Join-Path $env:LOCALAPPDATA 'QianniuAiBotUpdater'
$backupRoot = Join-Path $updaterRoot 'backups'
$backupDir = Join-Path $backupRoot $timestamp
$partialBackupDir = "$backupDir.partial"
$programBackup = Join-Path $backupDir 'program'
$persistentRoot = Join-Path $env:LOCALAPPDATA 'QianniuAiBot'
$persistentNames = @('data', 'global', 'shops')
$tempDir = Join-Path $env:TEMP "qianniu-bot-auto-update-$timestamp"
$logDir = Join-Path $updaterRoot 'logs'
$logPath = Join-Path $logDir "auto-update-$timestamp.log"
$oldProgramExisted = Test-Path -LiteralPath $InstallDir -PathType Container
$oldExe = Join-Path $InstallDir 'Bin\Bot.exe'
$backupFinalized = $false
$installMutationStarted = $false

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
    Stop-BotProcesses $InstallDir

    Write-Step "Preparing Bot update to version $ExpectedVersion"
    Write-Host "Package: $PackagePath"
    Write-Host "SHA256: $actualHash"
    Write-Host "Install directory: $InstallDir"
    Write-Host "Persistent root: $persistentRoot (data/global/shops)"
    Write-Host "Log: $logPath"

    Write-Step 'Backing up current program and persistent data'
    New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
    if (Test-Path -LiteralPath $partialBackupDir) {
        Remove-Item -LiteralPath $partialBackupDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $partialBackupDir -Force | Out-Null

    if ($oldProgramExisted) {
        $partialProgramBackup = Join-Path $partialBackupDir 'program'
        Copy-DirectoryContents $InstallDir $partialProgramBackup
        Assert-DirectoryCopyMatches $InstallDir $partialProgramBackup 'program'
    }

    $persistentEntries = @()
    $partialPersistentRoot = Join-Path $partialBackupDir 'persistent'
    foreach ($name in $persistentNames) {
        $source = Join-Path $persistentRoot $name
        $existed = Test-Path -LiteralPath $source -PathType Container
        $persistentEntries += [pscustomobject]@{ name = $name; existed = [bool]$existed }
        if (-not $existed) { continue }

        $destination = Join-Path $partialPersistentRoot $name
        Copy-DirectoryContents $source $destination
        Assert-DirectoryCopyMatches $source $destination "persistent/$name"
    }

    $manifest = [ordered]@{
        schema = 1
        created_at = (Get-Date).ToUniversalTime().ToString('o')
        install_dir = $InstallDir
        old_program_existed = [bool]$oldProgramExisted
        persistent_root = $persistentRoot
        persistent = $persistentEntries
    }
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $partialBackupDir 'backup-manifest.json') -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $partialBackupDir '.complete') -Value 'validated' -Encoding ASCII

    Move-Item -LiteralPath $partialBackupDir -Destination $backupDir
    if (-not (Test-BackupComplete $backupDir)) {
        throw "Backup finalization failed: $backupDir"
    }
    $backupFinalized = $true
    Write-Host "Validated backup directory: $backupDir" -ForegroundColor Green

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
    if (-not $backupFinalized -or -not (Test-BackupComplete $backupDir)) {
        throw 'Refusing to replace program files because no finalized validated backup is available.'
    }
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    $installMutationStarted = $true
    # Keep the install root itself. A shell/helper may retain a transient directory handle
    # after Bot.exe exits; deleting only children avoids treating that harmless root lock as failure.
    Clear-DirectoryContentsWithRetry $InstallDir
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
    Write-Host 'Persistent user data remains under %LocalAppData%\QianniuAiBot (data/global/shops).'

    if (Test-Path -LiteralPath $backupRoot) {
        @(Get-ChildItem -LiteralPath $backupRoot -Directory | Where-Object {
            Test-BackupComplete $_.FullName
        } | Sort-Object Name -Descending | Select-Object -Skip 8) | ForEach-Object {
            Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
        }
        @(Get-ChildItem -LiteralPath $backupRoot -Directory -Filter '*.partial' -ErrorAction SilentlyContinue | Where-Object {
            $_.LastWriteTime -lt (Get-Date).AddDays(-7)
        }) | ForEach-Object {
            Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
catch {
    $failure = $_
    Write-Host "`nUpdate failed: $($failure.Exception.Message)" -ForegroundColor Red
    Write-Host 'Starting automatic rollback...' -ForegroundColor Yellow

    $rollbackSucceeded = $false
    $backupUsable = $backupFinalized -and (Test-BackupComplete $backupDir)

    if (-not $installMutationStarted) {
        Write-Host 'Install directory was not modified; destructive rollback is skipped.' -ForegroundColor Yellow
        $rollbackSucceeded = $true
    }
    elseif (-not $backupUsable) {
        Write-Host 'Rollback refused: the only available backup is incomplete or unvalidated. No .partial backup will be used.' -ForegroundColor Red
    }
    else {
        try {
            Stop-BotProcesses $InstallDir
            New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
            Clear-DirectoryContentsWithRetry $InstallDir

            if ($oldProgramExisted) {
                if (-not (Test-Path -LiteralPath $programBackup -PathType Container)) {
                    throw "Completed backup is missing the program directory: $programBackup"
                }
                Copy-DirectoryContents $programBackup $InstallDir
                Assert-DirectoryCopyMatches $programBackup $InstallDir 'program restore'
            }

            Restore-PersistentData $backupDir $persistentRoot
            $rollbackSucceeded = $true
        }
        catch {
            Write-Host "Rollback failed: $($_.Exception.Message)" -ForegroundColor Red
        }
    }

    if ($oldProgramExisted -and $rollbackSucceeded -and (Test-Path -LiteralPath $oldExe)) {
        Start-Process -FilePath $oldExe -WorkingDirectory (Split-Path -Parent $oldExe)
    }

    if ($rollbackSucceeded) {
        Write-Host "Rollback completed safely. Log: $logPath" -ForegroundColor Yellow
    }
    else {
        Write-Host "Rollback was not completed. The updater refused to restore from an incomplete backup. Log: $logPath" -ForegroundColor Red
    }
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
