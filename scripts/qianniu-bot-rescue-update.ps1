[CmdletBinding()]
param(
    [string]$InstallDir = ""
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# These three placeholders are replaced by the stable-release workflow before publication.
$TargetVersion = '__QIANNIU_TARGET_VERSION__'
$PackageUrl = '__QIANNIU_PACKAGE_URL__'
$ExpectedSha256 = '__QIANNIU_SHA256__'

function Write-Step([string]$Message) {
    Write-Host "`n[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $Message" -ForegroundColor Cyan
}

function Quote-Arg([string]$Value) {
    $text = [string]$Value
    return '"' + $text.Replace('"', '\"') + '"'
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Resolve-InstallDir([string]$Requested) {
    if (-not [string]::IsNullOrWhiteSpace($Requested)) {
        return [IO.Path]::GetFullPath($Requested.Trim().Trim('"'))
    }

    $roots = @()
    try {
        foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name='Bot.exe'" -ErrorAction SilentlyContinue)) {
            $exe = [string]$process.ExecutablePath
            if ([string]::IsNullOrWhiteSpace($exe)) { continue }
            try {
                $full = [IO.Path]::GetFullPath($exe)
                if ([IO.Path]::GetFileName($full) -ieq 'Bot.exe' -and
                    [IO.Path]::GetFileName((Split-Path -Parent $full)) -ieq 'Bin') {
                    $root = Split-Path -Parent (Split-Path -Parent $full)
                    if ($roots -notcontains $root) { $roots += $root }
                }
            } catch {}
        }
    } catch {}

    if ($roots.Count -eq 1) { return $roots[0] }
    if ($roots.Count -gt 1) {
        throw '检测到多个 Bot 安装目录，请使用 -InstallDir 明确指定要修复的安装目录。'
    }

    $cwd = [IO.Path]::GetFullPath((Get-Location).Path)
    if (Test-Path -LiteralPath (Join-Path $cwd 'Bin\Bot.exe') -PathType Leaf) {
        return $cwd
    }

    $default = 'C:\qianniu-bot-x64'
    if (Test-Path -LiteralPath (Join-Path $default 'Bin\Bot.exe') -PathType Leaf) {
        return $default
    }

    throw '未自动找到 Bot 安装目录，请使用 -InstallDir 指定，例如 C:\qianniu-bot-x64。'
}

function Get-TargetBotPid([string]$Root) {
    $expectedExe = [IO.Path]::GetFullPath((Join-Path $Root 'Bin\Bot.exe'))
    $ids = @()
    try {
        foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name='Bot.exe'" -ErrorAction SilentlyContinue)) {
            $exe = [string]$process.ExecutablePath
            if ([string]::IsNullOrWhiteSpace($exe)) { continue }
            try {
                if ([string]::Equals([IO.Path]::GetFullPath($exe), $expectedExe, [StringComparison]::OrdinalIgnoreCase)) {
                    $ids += [int]$process.ProcessId
                }
            } catch {}
        }
    } catch {}
    if ($ids.Count -eq 0) { return 0 }
    return [int]($ids | Sort-Object | Select-Object -First 1)
}

if ($TargetVersion.Contains('__QIANNIU_') -or
    $PackageUrl.Contains('__QIANNIU_') -or
    $ExpectedSha256.Contains('__QIANNIU_')) {
    throw '这是仓库模板，不是可执行的正式救援更新器。请从 GitHub Release 下载 qianniu-bot-rescue-update.ps1。'
}

if (-not (Test-IsAdministrator)) {
    Write-Step '请求管理员权限'
    $self = $MyInvocation.MyCommand.Path
    if ([string]::IsNullOrWhiteSpace($self)) {
        throw '无法确定当前脚本路径，请以管理员 PowerShell 重新运行。'
    }
    $argLine = '-NoProfile -ExecutionPolicy Bypass -File ' + (Quote-Arg $self)
    if (-not [string]::IsNullOrWhiteSpace($InstallDir)) {
        $argLine += ' -InstallDir ' + (Quote-Arg $InstallDir)
    }
    $elevated = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argLine -PassThru -Wait
    exit $elevated.ExitCode
}

$InstallDir = Resolve-InstallDir $InstallDir
if (Test-Path -LiteralPath (Join-Path $InstallDir '.git')) {
    throw "拒绝覆盖 Git 源码目录：$InstallDir"
}

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$ExpectedSha256 = $ExpectedSha256.Trim().ToUpperInvariant()
$root = Join-Path $env:LOCALAPPDATA 'QianniuAiBotUpdater\rescue'
New-Item -ItemType Directory -Path $root -Force | Out-Null
$packagePath = Join-Path $root ("qianniu-bot-x64-$TargetVersion.zip")
$partialPath = $packagePath + '.partial'
$updaterPath = Join-Path $root ("BotAutoUpdater-$TargetVersion-" + [Guid]::NewGuid().ToString('N') + '.ps1')

try {
    Write-Step "下载正式安装包 $TargetVersion"
    Remove-Item -LiteralPath $partialPath -Force -ErrorAction SilentlyContinue
    Invoke-WebRequest -UseBasicParsing -Uri $PackageUrl -OutFile $partialPath
    if (-not (Test-Path -LiteralPath $partialPath -PathType Leaf)) {
        throw '下载安装包失败：临时文件不存在。'
    }
    $actualHash = (Get-FileHash -LiteralPath $partialPath -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actualHash -ne $ExpectedSha256) {
        throw "安装包 SHA-256 校验失败。expected=$ExpectedSha256 actual=$actualHash"
    }
    Move-Item -LiteralPath $partialPath -Destination $packagePath -Force
    Write-Host "SHA-256 校验通过：$actualHash" -ForegroundColor Green

    Write-Step '提取并预检目标版本自己的更新器'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        $updaterEntries = @($archive.Entries | Where-Object {
            ([string]$_.FullName).Replace('\', '/').TrimStart('/') -ieq 'Bin/BotAutoUpdater.ps1'
        })
        $releaseEntries = @($archive.Entries | Where-Object {
            ([string]$_.FullName).Replace('\', '/').TrimStart('/') -ieq 'release-info.json'
        })
        if ($updaterEntries.Count -ne 1) {
            throw "目标包必须且只能包含一个 Bin/BotAutoUpdater.ps1，actual=$($updaterEntries.Count)"
        }
        if ($releaseEntries.Count -ne 1) {
            throw "目标包必须且只能包含一个 release-info.json，actual=$($releaseEntries.Count)"
        }

        $reader = New-Object IO.StreamReader($releaseEntries[0].Open(), [Text.Encoding]::UTF8, $true)
        try { $releaseInfo = ($reader.ReadToEnd() | ConvertFrom-Json) } finally { $reader.Dispose() }
        if ([string]$releaseInfo.version -ne $TargetVersion) {
            throw "目标包版本不匹配。expected=$TargetVersion actual=$($releaseInfo.version)"
        }

        [IO.Compression.ZipFileExtensions]::ExtractToFile($updaterEntries[0], $updaterPath, $true)
    }
    finally {
        $archive.Dispose()
    }

    $tokens = $null
    $parseErrors = $null
    [Management.Automation.Language.Parser]::ParseFile($updaterPath, [ref]$tokens, [ref]$parseErrors) | Out-Null
    if ($parseErrors.Count -gt 0) {
        throw ('目标更新器 PowerShell 5.1 语法预检失败：' + (@($parseErrors | ForEach-Object { $_.Message }) -join ' | '))
    }

    $currentPid = Get-TargetBotPid $InstallDir
    Write-Step "执行独立救援更新；install=$InstallDir currentPid=$currentPid"
    $args = '-NoProfile -ExecutionPolicy Bypass -File ' + (Quote-Arg $updaterPath)
    $args += ' -PackagePath ' + (Quote-Arg $packagePath)
    $args += ' -InstallDir ' + (Quote-Arg $InstallDir)
    $args += ' -ExpectedSha256 ' + (Quote-Arg $ExpectedSha256)
    $args += ' -ExpectedVersion ' + (Quote-Arg $TargetVersion)
    $args += ' -CurrentPid ' + $currentPid

    $installer = Start-Process -FilePath 'powershell.exe' -ArgumentList $args -PassThru -Wait
    if ($installer.ExitCode -ne 0) {
        throw "目标更新器执行失败，exitCode=$($installer.ExitCode)。安装器会按自身事务备份自动回滚。"
    }

    $installedInfoPath = Join-Path $InstallDir 'release-info.json'
    if (-not (Test-Path -LiteralPath $installedInfoPath -PathType Leaf)) {
        throw '救援更新结束但 release-info.json 不存在。'
    }
    $installedInfo = Get-Content -LiteralPath $installedInfoPath -Raw | ConvertFrom-Json
    if ([string]$installedInfo.version -ne $TargetVersion) {
        throw "救援更新版本校验失败。expected=$TargetVersion actual=$($installedInfo.version)"
    }

    Write-Step "救援更新成功：$TargetVersion"
    Write-Host '新 Bot 已由目标更新器启动；永久用户数据仍保存在 %LOCALAPPDATA%\QianniuAiBot。' -ForegroundColor Green
}
finally {
    Remove-Item -LiteralPath $partialPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $updaterPath -Force -ErrorAction SilentlyContinue
}
