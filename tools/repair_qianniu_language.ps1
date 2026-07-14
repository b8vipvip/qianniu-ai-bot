param(
  [switch]$RestoreOfficial,
  [switch]$SkipCacheClear
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$injectJs = Join-Path $repoRoot "src\Bin\inject.js"
$languageJs = Join-Path $repoRoot "src\Bin\language.js"
$injectMarker = "20260714-zh-cn-v10"
$languageMarker = "20260713-hans-all-pages-v3"
$officialUrl = "https://iseiya.taobao.com/imsupport"
$oldRemoteUrl = "https://worklink.oss-cn-hangzhou.aliyuncs.com/5CFB5E11D17E63CDD8CB37B52FA6ACFD.js"
$injectFileName = "qnbot-inject.js"
$languageFileName = "qnbot-language.js"
$injectScriptSrc = $injectFileName + "?v=" + $injectMarker
$languageScriptSrc = $languageFileName + "?v=" + $languageMarker
$recentEntryName = "web_chat-packer/recent.html"
$injectEntryName = "web_chat-packer/qnbot-inject.js"

function Stop-QianniuProcesses {
  foreach ($name in @("Bot", "AliWorkbench", "new_AliWorkbench", "AliRender", "wwcmd", "wangwang", "AliApp")) {
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

function Get-ZipDirectory($entryName) {
  $idx = $entryName.LastIndexOf('/')
  if ($idx -lt 0) { return "" }
  return $entryName.Substring(0, $idx + 1)
}

function Remove-ScriptTags($html, $namePattern) {
  $pattern = '<script\b[^>]*\bsrc\s*=\s*["''][^"'']*' + $namePattern + '[^"'']*["''][^>]*>\s*</script\s*>'
  return [regex]::Replace($html, $pattern, '', [System.Text.RegularExpressions.RegexOptions]'IgnoreCase, Singleline')
}

function Insert-FirstInHead($html, $tags) {
  $head = [regex]::Match($html, '<head\b[^>]*>', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
  if ($head.Success) { return $html.Insert($head.Index + $head.Length, $tags) }
  return $tags + $html
}

function Clear-QianniuWebCaches {
  $cefRoots = @()
  if ($env:LOCALAPPDATA -and (Test-Path $env:LOCALAPPDATA)) {
    $cefRoots = @(Get-ChildItem -Path $env:LOCALAPPDATA -Directory -Filter "QNCEF*Temp" -ErrorAction SilentlyContinue |
      Select-Object -ExpandProperty FullName)
  }
  $roots = @(@(
      "$env:PUBLIC\Documents\AliWorkBench",
      "$env:PUBLIC\Documents\AliWorkbench",
      "$env:APPDATA\AliWorkbench",
      "$env:LOCALAPPDATA\AliWorkbench",
      "$env:LOCALAPPDATA\Alibaba\AliWorkbench",
      "$env:LOCALAPPDATA\Alibaba\AliWorkBench"
    ) + $cefRoots) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique

  # Only remove disposable Chromium caches. Preserve account/session data.
  $names = @("Cache", "Code Cache", "GPUCache", "DawnCache", "GrShaderCache", "GraphiteDawnCache", "Service Worker", "blob_storage")
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

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open($webuiZip, [System.IO.Compression.ZipArchiveMode]::Update)
try {
  $htmlNames = @($zip.Entries | Where-Object { !$_.FullName.EndsWith('/') -and $_.FullName.EndsWith('.html', [StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { $_.FullName })
  if ($htmlNames -notcontains $recentEntryName) { throw "Entry not found in webui.zip: $recentEntryName" }

  if ($RestoreOfficial) {
    Write-Host "Restore official imsupport and remove qnbot language injection from every HTML entry."
    foreach ($name in $htmlNames) {
      $html = Read-ZipEntryText $zip $name
      if ($null -eq $html) { continue }
      $html = Remove-ScriptTags $html 'qnbot-language\.js'
      $html = Remove-ScriptTags $html 'qnbot-inject\.js'
      $html = Remove-ScriptTags $html 'iseiya\.taobao\.com/imsupport'
      $html = Remove-ScriptTags $html '5CFB5E11D17E63CDD8CB37B52FA6ACFD\.js'
      if ($name -eq $recentEntryName) {
        $html = Insert-FirstInHead $html ('<script src="' + $officialUrl + '"></script>')
      }
      Write-ZipEntryText $zip $name $html
    }
    $scriptEntries = @($zip.Entries | Where-Object { $_.FullName.EndsWith('/' + $languageFileName, [StringComparison]::OrdinalIgnoreCase) -or $_.FullName -eq $languageFileName } | ForEach-Object { $_.FullName })
    foreach ($scriptEntry in $scriptEntries) { Remove-ZipEntry $zip $scriptEntry }
    Remove-ZipEntry $zip $injectEntryName
  } else {
    if (!(Test-Path $injectJs)) { throw "Local inject.js not found: $injectJs" }
    if (!(Test-Path $languageJs)) { throw "Local language.js not found: $languageJs" }
    $injectContent = Get-Content $injectJs -Raw -Encoding UTF8
    $languageContent = Get-Content $languageJs -Raw -Encoding UTF8
    if (!$injectContent.Contains($injectMarker)) { throw "Local inject.js does not contain marker $injectMarker. Run git pull first." }
    if (!$languageContent.Contains($languageMarker)) { throw "Local language.js does not contain marker $languageMarker. Run git pull first." }

    $languageEntryNames = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $htmlNames) {
      $html = Read-ZipEntryText $zip $name
      if ($null -eq $html) { continue }
      $html = Remove-ScriptTags $html 'qnbot-language\.js'
      $tags = '<script src="' + $languageScriptSrc + '"></script>'
      if ($name -eq $recentEntryName) {
        $html = Remove-ScriptTags $html 'qnbot-inject\.js'
        $html = Remove-ScriptTags $html 'iseiya\.taobao\.com/imsupport'
        $html = Remove-ScriptTags $html '5CFB5E11D17E63CDD8CB37B52FA6ACFD\.js'
        $tags += '<script src="' + $injectScriptSrc + '"></script>'
      }
      $html = Insert-FirstInHead $html $tags
      Write-ZipEntryText $zip $name $html
      [void]$languageEntryNames.Add((Get-ZipDirectory $name) + $languageFileName)
    }

    foreach ($languageEntryName in $languageEntryNames) {
      Write-ZipEntryText $zip $languageEntryName $languageContent
    }
    Write-ZipEntryText $zip $injectEntryName $injectContent
    Write-Host "Write embedded-version qnbot scripts: inject=$injectMarker, language=$languageMarker"
    Write-Host "HTML language coverage: $($htmlNames.Count), language script copies: $($languageEntryNames.Count)"
  }
} finally {
  $zip.Dispose()
}

if (Test-Path $signPath) {
  Clear-Content $signPath -ErrorAction SilentlyContinue
  Write-Host "sign.json cleared: $signPath"
}

if (!$SkipCacheClear) { Clear-QianniuWebCaches }

Write-Host "Done. Reopen Qianniu first, then start Bot.exe. A second Bot start should log needInject=0."
