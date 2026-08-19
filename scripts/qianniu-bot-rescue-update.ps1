[CmdletBinding()]
param(
    [string]$InstallDir = "",
    [string]$ReleaseApi = "https://api.github.com/repos/b8vipvip/qianniu-ai-bot/releases/latest",
    [string]$PackagePath = "",
    [string]$ExpectedVersion = "",
    [string]$ExpectedSha256 = ""
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step([string]$Message) {
    Write-Host "`n[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $Message" -ForegroundColor Cyan
}

function Quote-Arg([string]$Value) {
    return '"' + ([string]$Value).Replace('"', '\"') + '"'
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
    if (Test-Path -LiteralPath (Join-Path $cwd 'Bin\Bot.exe') -PathType Leaf) { return $cwd }

    $default = 'C:\qianniu-bot-x64'
    if (Test-Path -LiteralPath (Join-Path $default 'Bin\Bot.exe') -PathType Leaf) { return $default }

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

function Resolve-LatestRelease {
    Write-Step '读取最新正式版本元数据'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $headers = @{ 'User-Agent' = 'QianniuAiBotRescueUpdater' }
    $release = Invoke-RestMethod -UseBasicParsing -Uri $ReleaseApi -Headers $headers
    $manifestAsset = @($release.assets | Where-Object { $_.name -eq 'update.json' } | Select-Object -First 1)
    $packageAsset = @($release.assets | Where-Object { $_.name -eq 'qianniu-bot-x64.zip' } | Select-Object -First 1)
    if ($manifestAsset.Count -ne 1 -or $packageAsset.Count -ne 1) {
        throw '最新正式版本缺少 update.json 或 qianniu-bot-x64.zip。'
    }

    $manifest = Invoke-RestMethod -UseBasicParsing -Uri ([string]$manifestAsset[0].browser_download_url) -Headers $headers
    $version = ([string]$manifest.version).Trim()
    $sha = ([string]$manifest.sha256).Trim().ToUpperInvariant()
    $url = ([string]$manifest.download_url).Trim()
    if ([string]::IsNullOrWhiteSpace($url)) { $url = [string]$packageAsset[0].browser_download_url }
    if ([string]::IsNullOrWhiteSpace($version)) { throw 'update.json 缺少 version。' }
    if ($sha -notmatch '^[A-F0-9]{64}$') { throw 'update.json 中的 SHA-256 无效。' }
    if ([string]::IsNullOrWhiteSpace($url)) { throw 'update.json 缺少安装包下载地址。' }

    return [pscustomobject]@{ Version = $version; Sha256 = $sha; Url = $url }
}

if (-not (Test-IsAdministrator)) {
    Write-Step '请求管理员权限'
    $self = $MyInvocation.MyCommand.Path
    if ([string]::IsNullOrWhiteSpace($self)) { throw '无法确定当前脚本路径，请以管理员 PowerShell 重新运行。' }
    $args = '-NoProfile -ExecutionPolicy Bypass -File ' + (Quote-Arg $self)
    if (-not [string]::IsNullOrWhiteSpace($InstallDir)) { $args += ' -InstallDir ' + (Quote-Arg $InstallDir) }
    if (-not [string]::IsNullOrWhiteSpace($ReleaseApi)) { $args += ' -ReleaseApi ' + (Quote-Arg $ReleaseApi) }
    if (-not [string]::IsNullOrWhiteSpace($PackagePath)) { $args += ' -PackagePath ' + (Quote-Arg $PackagePath) }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion)) { $args += ' -ExpectedVersion ' + (Quote-Arg $ExpectedVersion) }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedSha256)) { $args += ' -ExpectedSha256 ' + (Quote-Arg $ExpectedSha256) }
    $elevated = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $args -PassThru -Wait
    exit $elevated.ExitCode
}

$InstallDir = Resolve-InstallDir $InstallDir
if (Test-Path -LiteralPath (Join-Path $InstallDir '.git')) { throw "拒绝覆盖 Git 源码目录：$InstallDir" }

$downloadRequired = [string]::IsNullOrWhiteSpace($PackagePath)
if ($downloadRequired) {
    $releaseInfo = Resolve-LatestRelease
    $ExpectedVersion = $releaseInfo.Version
    $ExpectedSha256 = $releaseInfo.Sha256
    $PackageUrl = $releaseInfo.Url
} else {
    $PackagePath = [IO.Path]::GetFullPath($PackagePath)
    if ([string]::IsNullOrWhiteSpace($ExpectedVersion) -or [string]::IsNullOrWhiteSpace($ExpectedSha256)) {
        throw '使用本地 -PackagePath 时必须同时提供 -ExpectedVersion 和 -ExpectedSha256。'
    }
}

$ExpectedSha256 = $ExpectedSha256.Trim().ToUpperInvariant()
if ($ExpectedSha256 -notmatch '^[A-F0-9]{64}$') { throw 'ExpectedSha256 必须是 64 位十六进制 SHA-256。' }

$root = Join-Path $env:LOCALAPPDATA 'QianniuAiBotUpdater\rescue'
New-Item -ItemType Directory -Path $root -Force | Out-Null
$partialPath = Join-Path $root ("qianniu-bot-x64-$ExpectedVersion.zip.partial")
$updaterPath = Join-Path $root ("BotAutoUpdater-$ExpectedVersion-" + [Guid]::NewGuid().ToString('N') + '.ps1')
if ($downloadRequired) { $PackagePath = Join-Path $root ("qianniu-bot-x64-$ExpectedVersion.zip") }

try {
    if ($downloadRequired) {
        Write-Step "下载正式安装包 $ExpectedVersion"
        Remove-Item -LiteralPath $partialPath -Force -ErrorAction SilentlyContinue
        Invoke-WebRequest -UseBasicParsing -Uri $PackageUrl -OutFile $partialPath
        Move-Item -LiteralPath $partialPath -Destination $PackagePath -Force
    }

    if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) { throw "安装包不存在：$PackagePath" }
    $actualHash = (Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actualHash -ne $ExpectedSha256) {
        throw "安装包 SHA-256 校验失败。expected=$ExpectedSha256 actual=$actualHash"
    }
    Write-Host "SHA-256 校验通过：$actualHash" -ForegroundColor Green

    Write-Step '提取并预检目标版本自己的更新器'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $updaterEntries = @($archive.Entries | Where-Object { ([string]$_.FullName).Replace('\', '/').TrimStart('/') -ieq 'Bin/BotAutoUpdater.ps1' })
        $releaseEntries = @($archive.Entries | Where-Object { ([string]$_.FullName).Replace('\', '/').TrimStart('/') -ieq 'release-info.json' })
        if ($updaterEntries.Count -ne 1) { throw "目标包必须且只能包含一个 Bin/BotAutoUpdater.ps1，actual=$($updaterEntries.Count)" }
        if ($releaseEntries.Count -ne 1) { throw "目标包必须且只能包含一个 release-info.json，actual=$($releaseEntries.Count)" }

        $reader = New-Object IO.StreamReader($releaseEntries[0].Open(), [Text.Encoding]::UTF8, $true)
        try { $packageInfo = ($reader.ReadToEnd() | ConvertFrom-Json) } finally { $reader.Dispose() }
        if ([string]$packageInfo.version -ne $ExpectedVersion) {
            throw "目标包版本不匹配。expected=$ExpectedVersion actual=$($packageInfo.version)"
        }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($updaterEntries[0], $updaterPath, $true)
    } finally { $archive.Dispose() }

    $tokens = $null
    $parseErrors = $null
    [Management.Automation.Language.Parser]::ParseFile($updaterPath, [ref]$tokens, [ref]$parseErrors) | Out-Null
    if ($parseErrors.Count -gt 0) {
        throw ('目标更新器 Windows PowerShell 5.1 语法预检失败：' + (@($parseErrors | ForEach-Object { $_.Message }) -join ' | '))
    }

    $currentPid = Get-TargetBotPid $InstallDir
    Write-Step "绕过旧 Bot 更新器执行救援安装；install=$InstallDir currentPid=$currentPid"
    $args = '-NoProfile -ExecutionPolicy Bypass -File ' + (Quote-Arg $updaterPath)
    $args += ' -PackagePath ' + (Quote-Arg $PackagePath)
    $args += ' -InstallDir ' + (Quote-Arg $InstallDir)
    $args += ' -ExpectedSha256 ' + (Quote-Arg $ExpectedSha256)
    $args += ' -ExpectedVersion ' + (Quote-Arg $ExpectedVersion)
    $args += ' -CurrentPid ' + $currentPid
    $installer = Start-Process -FilePath 'powershell.exe' -ArgumentList $args -PassThru -Wait
    if ($installer.ExitCode -ne 0) { throw "目标更新器执行失败，exitCode=$($installer.ExitCode)。目标更新器会按事务备份自动回滚。" }

    $installedInfoPath = Join-Path $InstallDir 'release-info.json'
    if (-not (Test-Path -LiteralPath $installedInfoPath -PathType Leaf)) { throw '救援更新结束但 release-info.json 不存在。' }
    $installedInfo = Get-Content -LiteralPath $installedInfoPath -Raw | ConvertFrom-Json
    if ([string]$installedInfo.version -ne $ExpectedVersion) {
        throw "救援更新版本校验失败。expected=$ExpectedVersion actual=$($installedInfo.version)"
    }

    Write-Step "救援更新成功：$ExpectedVersion"
    Write-Host '新 Bot 已由目标更新器启动；永久用户数据仍保存在 %LOCALAPPDATA%\QianniuAiBot。' -ForegroundColor Green
}
finally {
    Remove-Item -LiteralPath $partialPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $updaterPath -Force -ErrorAction SilentlyContinue
}
