$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

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
  }

  Write-Host "HTML entries mentioning qnbot/imsupport:"
  foreach ($entry in $zip.Entries) {
    if (!$entry.FullName.EndsWith('.html')) { continue }
    $text = Read-ZipEntryText $zip $entry.FullName
    if ($text -and ($text.Contains('qnbot-inject') -or $text.Contains('iseiya.taobao.com/imsupport') -or $text.Contains('5CFB5E11D17E63CDD8CB37B52FA6ACFD'))) {
      Write-Host " - $($entry.FullName)"
    }
  }
} finally {
  $zip.Dispose()
}
