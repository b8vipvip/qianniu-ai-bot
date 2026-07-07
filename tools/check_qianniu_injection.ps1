$ErrorActionPreference = "Stop"

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

  Write-Host "Qianniu install path: $installPath"
  Write-Host "Qianniu resource path: $resourcePath"
  Write-Host "webui.zip: $webuiZip"
  Write-Host "recent.html exists: $($null -ne $recent)"
  Write-Host "qnbot-inject.js exists: $($null -ne $inject)"

  if ($recent) {
    Write-Host "recent.html contains qnbot-inject.js: $($recent.Contains('qnbot-inject.js'))"
    Write-Host "recent.html contains official imsupport: $($recent.Contains('https://iseiya.taobao.com/imsupport'))"
  }

  if ($inject) {
    $m = [regex]::Match($inject, 'window\.__qnbotInjectVersion\s*=\s*"([^"]+)"')
    Write-Host "inject version: $($m.Groups[1].Value)"
    Write-Host "inject contains body-ready patch: $($inject.Contains('__qnbotAppendOfficialImsupportWhenBodyReady'))"
    Write-Host "inject contains websocket: $($inject.Contains('ws://127.0.0.1:41010'))"
    Write-Host "inject contains imsdk hook: $($inject.Contains('im.singlemsg.onReceiveNewMsg'))"
    Write-Host "inject contains status patch: $($inject.Contains('__qnbotStatusPatch'))"
    Write-Host "inject contains GetNewMsg patch: $($inject.Contains('__qnbotGetNewMsgPatch'))"
  }

  $htmlEntries = @($zip.Entries | Where-Object { $_.FullName.EndsWith('.html') })
  $covered = 0
  $mentioned = New-Object System.Collections.Generic.List[string]
  Write-Host "HTML injection coverage:"
  foreach ($entry in $htmlEntries) {
    $text = Read-ZipEntryText $zip $entry.FullName
    $dir = Get-DirNameInZip $entry.FullName
    $sameDirInject = $dir + "qnbot-inject.js"
    $hasInjectEntry = $null -ne $zip.GetEntry($sameDirInject)
    $containsInject = $text -and $text.Contains('qnbot-inject.js')
    if ($containsInject -and $hasInjectEntry) { $covered++ }
    if ($text -and ($text.Contains('qnbot-inject') -or $text.Contains('iseiya.taobao.com/imsupport') -or $text.Contains('5CFB5E11D17E63CDD8CB37B52FA6ACFD'))) {
      $mentioned.Add($entry.FullName) | Out-Null
    }
    Write-Host (" - {0} | html_has_qnbot={1} | same_dir_script={2}" -f $entry.FullName, $containsInject, $hasInjectEntry)
  }
  Write-Host "HTML total: $($htmlEntries.Count), covered: $covered"
  Write-Host "HTML entries mentioning qnbot/imsupport:"
  foreach ($m in $mentioned) { Write-Host " - $m" }
} finally {
  $zip.Dispose()
}
