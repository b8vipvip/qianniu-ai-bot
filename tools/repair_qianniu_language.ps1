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
    Write-Host "Scan cache root: $root"
    Get-ChildItem -Path $root -Recurse -Directory -ErrorAction SilentlyContinue |
      Where-Object { $names -contains $_.Name } |
      ForEach-Object {
        Write-Host "Remove cache: $($_.FullName)"
        Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
      }
  }
}

Stop-QianniuProcesses

$installPath = Get-QianniuInstallPath
if (!$installPath) { throw "Qianniu install path not found. Please install and start Qianniu once." }
Write-Host "Qianniu install path: $installPath"

$resourcePath = Get-QianniuResourcePath $installPath
if (!$resourcePath) { throw "Qianniu Resources path not found." }
Write-Host "Qianniu resource path: $resourcePath"

$webuiZip = Join-Path (Join-Path $resourcePath "newWebui") "webui.zip"
$signPath = Join-Path (Join-Path $resourcePath "newWebui") "sign.json"
if (!(Test-Path $webuiZip)) { throw "webui.zip not found: $webuiZip" }

$backup = "$webuiZip.bak-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
Copy-Item $webuiZip $backup -Force
Write-Host "Backup created: $backup"

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open($webuiZip, [System.IO.Compression.ZipArchiveMode]::Update)
try {
  $recent = Read-ZipEntryText $zip $recentEntryName
  if ($null -eq $recent) { throw "Entry not found in webui.zip: $recentEntryName" }

  if ($RestoreOfficial) {
    Write-Host "Restore official imsupport script. qnbot injection will be removed."
    $recent = $recent.Replace($oldRemoteUrl, $officialUrl)
    $recent = $recent.Replace($injectSrc, $officialUrl)
    Remove-ZipEntry $zip $injectEntryName
  } else {
    if (!(Test-Path $injectJs)) { throw "Local inject.js not found: $injectJs" }
    $injectContent = Get-Content $injectJs -Raw -Encoding UTF8
    if ($injectContent -notmatch [regex]::Escape($marker)) {
      throw "Local inject.js does not contain marker $marker. Run git pull first."
    }

    Write-Host "Write local qnbot-inject.js and force zh-CN locale."
    if ($recent.Contains($oldRemoteUrl)) {
      $recent = $recent.Replace($oldRemoteUrl, $injectSrc)
    } elseif ($recent.Contains($officialUrl)) {
      $recent = $recent.Replace($officialUrl, $injectSrc)
    } elseif (!$recent.Contains($injectSrc)) {
      $tag = '<script src="' + $injectSrc + '"></script>'
      if ($recent -match '</body>') {
        $recent = $recent -replace '</body>', ($tag + '</body>')
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
  Write-Host "sign.json cleared: $signPath"
}

if (!$SkipCacheClear) {
  Clear-QianniuWebCaches
}

Write-Host "Done. Reopen Qianniu first, then start Bot.exe."
