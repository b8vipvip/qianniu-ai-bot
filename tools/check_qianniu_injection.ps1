$ErrorActionPreference = "Stop"
$expectedInjectVersion = "20260713-zh-cn-v8"
$expectedLanguageVersion = "20260713-hans-all-pages-v3"

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

function Get-DirNameInZip($entryName) {
  $idx = $entryName.LastIndexOf('/')
  if ($idx -lt 0) { return "" }
  return $entryName.Substring(0, $idx + 1)
}

$installPath = Get-QianniuInstallPath
if (!$installPath) { throw "Qianniu install path not found." }
$resourcePath = Get-QianniuResourcePath $installPath
if (!$resourcePath) { throw "Qianniu resource path not found." }
$webuiZip = Join-Path (Join-Path $resourcePath "newWebui") "webui.zip"
if (!(Test-Path $webuiZip)) { throw "webui.zip not found: $webuiZip" }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($webuiZip)
try {
  $recent = Read-ZipEntryText $zip "web_chat-packer/recent.html"
  $inject = Read-ZipEntryText $zip "web_chat-packer/qnbot-inject.js"
  $language = Read-ZipEntryText $zip "web_chat-packer/qnbot-language.js"

  Write-Host "Qianniu install path: $installPath"
  Write-Host "Qianniu resource path: $resourcePath"
  Write-Host "webui.zip: $webuiZip"
  Write-Host "recent.html exists: $($null -ne $recent)"
  Write-Host "qnbot-inject.js exists: $($null -ne $inject)"
  Write-Host "qnbot-language.js exists: $($null -ne $language)"

  if ($recent) {
    Write-Host "recent.html contains qnbot-inject.js: $($recent.Contains('qnbot-inject.js'))"
    Write-Host "recent.html contains official imsupport: $($recent.Contains('https://iseiya.taobao.com/imsupport'))"
    $head = [regex]::Match($recent, '<head\b[^>]*>', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    $scriptIndex = $recent.IndexOf('qnbot-inject.js', [StringComparison]::OrdinalIgnoreCase)
    $languageScriptIndex = $recent.IndexOf('qnbot-language.js', [StringComparison]::OrdinalIgnoreCase)
    Write-Host "language script precedes bot script: $($languageScriptIndex -ge 0 -and $scriptIndex -gt $languageScriptIndex)"
    Write-Host "recent uses versioned bot script URL: $($recent.Contains('qnbot-inject.js?v=' + $expectedInjectVersion))"
    Write-Host "recent uses versioned language script URL: $($recent.Contains('qnbot-language.js?v=' + $expectedLanguageVersion))"
  }

  if ($inject) {
    $m = [regex]::Match($inject, 'window\.__qnbotInjectVersion\s*=\s*"([^"]+)"')
    $runtime = [regex]::Match($inject, 'window\.__qnbotRuntimePatch\s*=\s*"([^"]+)"')
    $languagePatch = [regex]::Match($inject, 'window\.__qnbotLanguagePatch\s*=\s*"([^"]+)"')
    Write-Host "inject version: $($m.Groups[1].Value)"
    Write-Host "inject runtime patch: $($runtime.Groups[1].Value)"
    Write-Host "inject language patch: $($languagePatch.Groups[1].Value)"
    Write-Host "inject contains body-ready patch: $($inject.Contains('__qnbotAppendOfficialImsupportWhenBodyReady') -or $inject.Contains('appendOfficialWhenReady'))"
    Write-Host "inject contains websocket: $($inject.Contains('ws://127.0.0.1:41010'))"
    Write-Host "inject contains imsdk hook: $($inject.Contains('im.singlemsg.onReceiveNewMsg'))"
    Write-Host "inject contains status patch: $($inject.Contains('__qnbotStatusPatch'))"
    Write-Host "inject contains GetNewMsg patch: $($inject.Contains('__qnbotGetNewMsgPatch'))"
    Write-Host "inject contains safe delayed hooks: $($inject.Contains('20260707-safe-hooks-v5'))"
    Write-Host "inject traverses Shadow DOM: $($inject.Contains('attachShadow') -and $inject.Contains('shadowRoots'))"
    Write-Host "inject traverses iframe documents: $($inject.Contains('contentDocument') -and $inject.Contains('frameDocuments'))"
    Write-Host "inject reports active language pages: $($inject.Contains('languagePages') -and $inject.Contains('__qnbotLanguageReport:'))"
  }

  if ($language) {
    $languageVersion = [regex]::Match($language, 'window\.__qnbotLanguageVersion\s*=\s*"([^"]+)"')
    Write-Host "standalone language version: $($languageVersion.Groups[1].Value)"
    Write-Host "standalone language forces zh-CN: $($language.Contains('forceZhCn'))"
    Write-Host "standalone language traverses Shadow DOM: $($language.Contains('attachShadow') -and $language.Contains('shadowRoots'))"
    Write-Host "standalone language traverses iframe documents: $($language.Contains('contentDocument') -and $language.Contains('frameDocuments'))"
  }

  $htmlEntries = @($zip.Entries | Where-Object { $_.FullName.EndsWith('.html') })
  $botCovered = 0
  $languageCovered = 0
  $mentioned = New-Object System.Collections.Generic.List[string]
  Write-Host "HTML injection coverage:"
  foreach ($entry in $htmlEntries) {
    $text = Read-ZipEntryText $zip $entry.FullName
    $dir = Get-DirNameInZip $entry.FullName
    $sameDirInject = $dir + "qnbot-inject.js"
    $sameDirLanguage = $dir + "qnbot-language.js"
    $hasInjectEntry = $null -ne $zip.GetEntry($sameDirInject)
    $languageEntry = Read-ZipEntryText $zip $sameDirLanguage
    $hasCurrentLanguageEntry = $languageEntry -and $languageEntry.Contains($expectedLanguageVersion)
    $containsInject = $text -and $text.Contains('qnbot-inject.js')
    $containsLanguage = $text -and $text.Contains('qnbot-language.js')
    if ($containsInject -and $hasInjectEntry) { $botCovered++ }
    if ($containsLanguage -and $hasCurrentLanguageEntry) { $languageCovered++ }
    if ($text -and ($text.Contains('qnbot-inject') -or $text.Contains('iseiya.taobao.com/imsupport') -or $text.Contains('5CFB5E11D17E63CDD8CB37B52FA6ACFD'))) {
      $mentioned.Add($entry.FullName) | Out-Null
    }
    Write-Host (" - {0} | bot={1} | language={2} | current_language_script={3}" -f $entry.FullName, $containsInject, $containsLanguage, $hasCurrentLanguageEntry)
  }
  Write-Host "HTML total: $($htmlEntries.Count), bot covered: $botCovered, language covered: $languageCovered"
  Write-Host "All HTML language covered: $($htmlEntries.Count -gt 0 -and $languageCovered -eq $htmlEntries.Count)"
  Write-Host "HTML entries mentioning qnbot/imsupport:"
  foreach ($m in $mentioned) { Write-Host " - $m" }
} finally {
  $zip.Dispose()
}
