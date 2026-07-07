param(
  [switch]$RestoreOfficial,
  [switch]$SkipCacheClear
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$injectJs = Join-Path $repoRoot "src\Bin\inject.js"
$marker = "20260707-zh-cn-v2"
$officialUrl = "https://iseiya.taobao.com/imsupport"
$oldRemoteUrl = "https://worklink.oss-cn-hangzhou.aliyuncs.com/5CFB5E11D17E63CDD8CB37B52FA6ACFD.js"
$injectSrc = "qnbot-inject.js"
$recentEntryName = "web_chat-packer/recent.html"
$injectEntryName = "web_chat-packer/qnbot-inject.js"

function Stop-QianniuProcesses {
  foreach ($name in @("Bot", "AliWorkbench", "wwcmd", "wangwang", "AliApp")) {
    Get-Process -Name $name -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  }
  Start-Sleep -Seconds 2
}

function Get-QianniuInstallPath {
  try {
    $key = Get-Item "Registry::HKEY_CLASSES_ROOT\aliim\Shell\Open\Command" -ErrorAction Stop
    $cmd = [string]$key.GetValue("")
    if ($cmd -match '"([^"]*wwcmd\.exe)"') {
      return (Split-Path -Parent (Split-Path -Parent $matches[1]))
    }
    $idx = $cmd.IndexOf("wwcmd.exe", [StringComparison]::OrdinalIgnoreCase)
    if ($idx -gt 0) {
      $exe = $cmd.Substring(0, $idx + 9).Trim('"')
      return (Split-Path -Parent (Split-Path -Parent $exe))
    }
  } catch {}
  return $null
}

function Get-QianniuResourcePath($installPath) {
  $ini = Join-Path $installPath "AliWorkbench.ini"
  if (!(Test-Path $ini)) { return $null }
  $versionLine = Get-Content $ini | Where-Object { $_ -match '^Version=' } | Select-Object -First 1
  if (!$versionLine) { return $null }
  $version = ($versionLine -replace '^Version=', '').Trim()
  return Join-Path (Join-Path $installPath $version) "Resources"
}

function Read-ZipEntryText($zip, $entryName) {
  $entry = $zip.GetEntry($entryName)
  if ($null -eq $entry) { return $null }
  $reader = New-Object System.IO.StreamReader($entry.Open(), [System.Text.Encoding]::UTF8)
  try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}

function Write-ZipEntryText($zip, $entryName, $text) {
  $entry = $zip.GetEntry($entryName)
  if ($null -ne $entry) { $entry.Delete() }
  $newEntry = $zip.CreateEntry($entryName)
  $writer = New-Object System.IO.StreamWriter($newEntry.Open(), [System.Text.Encoding]::UTF8)
  try { $writer.Write($text) } finally { $writer.Dispose() }
}

function Remove-ZipEntry($zip, $entryName) {
  $entry = $zip.GetEntry($entryName)
  if ($null -ne $entry) { $entry.Delete() }
}

function Clear-QianniuWebCaches {
  $roots = @(
    "$env:PUBLIC\Documents\AliWorkBench",
    "$env:PUBLIC\Documents\AliWorkbench",
    "$env:APPDATA\AliWorkbench",
    "$env:LOCALAPPDATA\AliWorkbench",
    "$env:LOCALAPPDATA\Alibaba\AliWorkbench",
    "$env:LOCALAPPDATA\Alibaba\AliWorkBench"
  ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique

  $names = @("Cache", "Code Cache", "GPUCache", "Service Worker", "IndexedDB", "Local Storage", "Session Storage", "blob_storage")
  foreach ($root in $roots) {
    Write-Host "扫描缓存目录: $root"
    Get-ChildItem -Path $root -Recurse -Directory -ErrorAction SilentlyContinue |
      Where-Object { $names -contains $_.Name } |
      ForEach-Object {
        Write-Host "删除缓存: $($_.FullName)"
        Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
      }
  }
}

Stop-QianniuProcesses

$installPath = Get-QianniuInstallPath
if (!$installPath) { throw "没有找到千牛安装路径。请确认千牛已安装并至少启动过一次。" }
Write-Host "千牛安装目录: $installPath"

$resourcePath = Get-QianniuResourcePath $installPath
if (!$resourcePath) { throw "没有找到千牛 Resources 目录。" }
Write-Host "千牛资源目录: $resourcePath"

$webuiZip = Join-Path (Join-Path $resourcePath "newWebui") "webui.zip"
$signPath = Join-Path (Join-Path $resourcePath "newWebui") "sign.json"
if (!(Test-Path $webuiZip)) { throw "没有找到 webui.zip: $webuiZip" }

$backup = "$webuiZip.bak-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
Copy-Item $webuiZip $backup -Force
Write-Host "已备份: $backup"

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open($webuiZip, [System.IO.Compression.ZipArchiveMode]::Update)
try {
  $recent = Read-ZipEntryText $zip $recentEntryName
  if ($null -eq $recent) { throw "webui.zip 内没有找到 $recentEntryName" }

  if ($RestoreOfficial) {
    Write-Host "恢复官方 imsupport 脚本，不注入 qnbot。"
    $recent = $recent.Replace($oldRemoteUrl, $officialUrl)
    $recent = $recent.Replace($injectSrc, $officialUrl)
    Remove-ZipEntry $zip $injectEntryName
  } else {
    if (!(Test-Path $injectJs)) { throw "没有找到本地 inject.js: $injectJs" }
    $injectContent = Get-Content $injectJs -Raw -Encoding UTF8
    if ($injectContent -notmatch [regex]::Escape($marker)) {
      throw "本地 inject.js 不包含语言修复标记 $marker，请先 git pull 更新。"
    }

    Write-Host "强制写入本地 qnbot-inject.js，并设置 zh-CN。"
    if ($recent.Contains($oldRemoteUrl)) {
      $recent = $recent.Replace($oldRemoteUrl, $injectSrc)
    } elseif ($recent.Contains($officialUrl)) {
      $recent = $recent.Replace($officialUrl, $injectSrc)
    } elseif (!$recent.Contains($injectSrc)) {
      $tag = "<script src=\"$injectSrc\"></script>"
      if ($recent -match '</body>') {
        $recent = $recent -replace '</body>', "$tag</body>"
      } else {
        $recent += $tag
      }
    }
    Write-ZipEntryText $zip $injectEntryName $injectContent
  }

  Write-ZipEntryText $zip $recentEntryName $recent
} finally {
  $zip.Dispose()
}

if (Test-Path $signPath) {
  Clear-Content $signPath -ErrorAction SilentlyContinue
  Write-Host "已清空签名文件: $signPath"
}

if (!$SkipCacheClear) {
  Clear-QianniuWebCaches
}

Write-Host "完成。现在请重新打开千牛，再打开 Bot.exe。"
