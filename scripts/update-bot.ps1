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
        throw '没有找到 qianniu-bot x64 ZIP。请使用 -PackagePath 指定下载的完整运行包。'
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
        Write-Host "停止 Bot.exe PID=$($_.Id)"
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
        Write-Host "已从正在运行的 Bot.exe 自动识别安装目录: $InstallDir" -ForegroundColor Green
    }
    else {
        $InstallDir = 'C:\QianniuAiBot'
        Write-Host "未检测到正在运行的 Bot.exe，使用默认安装目录: $InstallDir" -ForegroundColor Yellow
    }
}

$InstallDir = [IO.Path]::GetFullPath($InstallDir)
if (Test-Path -LiteralPath (Join-Path $InstallDir '.git')) {
    throw "拒绝把运行包覆盖到 Git 源码仓库目录：$InstallDir。请用 -InstallDir 指定真正的 Bot 运行目录。"
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

Write-Step "准备更新"
Write-Host "ZIP: $PackagePath"
Write-Host "SHA256: $packageHash"
Write-Host "安装目录: $InstallDir"
Write-Host "永久数据目录: $persistentData"

try {
    Stop-BotProcesses

    Write-Step "备份当前程序和永久用户数据"
    New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
    if ($oldProgramExisted) {
        Copy-DirectoryContents $InstallDir $programBackup
    }
    if (Test-Path -LiteralPath $persistentData) {
        Copy-DirectoryContents $persistentData $persistentBackup
    }
    Write-Host "备份目录: $backupDir" -ForegroundColor Green

    Write-Step "解压并验证新版本"
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
            throw 'ZIP 结构无效：未找到唯一的 Bin\Bot.exe。'
        }
        $packageRoot = $roots[0].FullName
    }

    $newExe = Join-Path $packageRoot 'Bin\Bot.exe'
    if (-not (Test-Path -LiteralPath $newExe)) {
        throw "新包缺少 Bot.exe: $newExe"
    }

    # 兼容尚未迁移到 %LocalAppData% 的旧版本：把旧运行目录 data 带到新程序目录，
    # 新版首次启动时会由 UserDataMigrationManager 安全迁移到永久数据目录。
    $legacyData = Join-Path $InstallDir 'data'
    if (Test-Path -LiteralPath $legacyData) {
        Write-Host '检测到旧程序目录 data，复制到新包以便首次启动迁移。'
        Copy-DirectoryContents $legacyData (Join-Path $packageRoot 'data')
    }

    Write-Step "替换程序文件"
    if (Test-Path -LiteralPath $InstallDir) {
        Remove-Item -LiteralPath $InstallDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    Copy-DirectoryContents $packageRoot $InstallDir

    $installedExe = Join-Path $InstallDir 'Bin\Bot.exe'
    if (-not (Test-Path -LiteralPath $installedExe)) {
        throw '替换完成后未找到 Bin\Bot.exe。'
    }

    if (-not $NoStart) {
        Write-Step "启动并验证 Bot.exe"
        Start-Process -FilePath $installedExe -WorkingDirectory (Split-Path -Parent $installedExe)
        if (-not (Test-BotStarted $installedExe)) {
            throw '新 Bot.exe 启动后未保持运行，触发自动回滚。'
        }
        Write-Host 'Bot.exe 已成功启动。' -ForegroundColor Green
    }

    Write-Step "更新成功"
    Write-Host "当前程序: $installedExe"
    Write-Host "备份位置: $backupDir"
    Write-Host '用户数据继续保存在 %LocalAppData%\QianniuAiBot\data，不会随程序升级被覆盖。'
}
catch {
    $failure = $_
    Write-Host "`n更新失败：$($failure.Exception.Message)" -ForegroundColor Red
    Write-Host '正在自动回滚...' -ForegroundColor Yellow

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
