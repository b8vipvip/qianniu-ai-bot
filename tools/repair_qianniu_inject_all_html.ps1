param(
  [switch]$EveryHtml
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"

$officialUrl = "https://iseiya.taobao.com/imsupport"
$oldRemoteUrl = "https://worklink.oss-cn-hangzhou.aliyuncs.com/5CFB5E11D17E63CDD8CB37B52FA6ACFD.js"
$injectFileName = "qnbot-inject.js"
$primaryInjectEntryName = "web_chat-packer/qnbot-inject.js"

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

function Get-DirNameInZip($entryName) {
  $idx = $entryName.LastIndexOf('/')
  if ($idx -lt 0) { return "" }
  return $entryName.Substring(0, $idx + 1)
}

function Patch-InjectContent($injectContent) {
  if (!$injectContent) { return $injectContent }

  if ($injectContent -notmatch "__qnbotStatusPatch") {
    $oldOpen = 'window.chatWebsocket = socket; // 存储到全局变量'
    $newOpen = @'
window.chatWebsocket = socket; // 存储到全局变量
      try {
        socket.send(JSON.stringify({
          type: 'qnbotStatus',
          response: JSON.stringify({
            patch: '__qnbotStatusPatch',
            href: location.href,
            title: document.title || '',
            hasImsdk: !!window.imsdk,
            hasQN: !!window.QN,
            hasVs: !!window._vs,
            hasLoginID: !!(window._vs && window._vs.loginID),
            hasConversationID: !!(window._vs && window._vs.conversationID)
          })
        }));
      } catch (e) {}
'@
    if ($injectContent.Contains($oldOpen)) {
      Write-Host "Patch qnbot-inject.js: add websocket status diagnostic."
      $injectContent = $injectContent.Replace($oldOpen, $newOpen)
    }
  }

  if ($injectContent -notmatch "__qnbotGetNewMsgPatch") {
    $oldBlock = @'
        // if(cid.ccode !== window._conversationId.ccode)
        // {
        //     imsdk.invoke('im.singlemsg.GetNewMsg', {
        //         ccode: cid.ccode
        //     }).then(response => {
        //         window.chatWebsocket.send(JSON.stringify({type:'receiveNewMsg',response:JSON.stringify(response)}));                       
        //     });
        // }
'@
    $newBlock = @'
        // __qnbotGetNewMsgPatch: actively fetch unread messages and send them to Bot.
        try {
          imsdk.invoke('im.singlemsg.GetNewMsg', {
            ccode: cid.ccode
          }).then(response => {
            window.chatWebsocket.send(JSON.stringify({type:'receiveNewMsg',response:JSON.stringify(response)}));
          }).catch(err => console.error('qnbot GetNewMsg failed', err));
        } catch (e) {
          console.error('qnbot GetNewMsg exception', e);
        }
'@
    if ($injectContent.Contains($oldBlock)) {
      Write-Host "Patch qnbot-inject.js: actively call GetNewMsg on new message."
      $injectContent = $injectContent.Replace($oldBlock, $newBlock)
    } else {
      Write-Host "WARN: GetNewMsg patch target not found. It may already be patched or source changed."
    }
  }

  return $injectContent
}

Stop-QianniuProcesses

$installPath = Get-QianniuInstallPath
if (!$installPath) { throw "Qianniu install path not found." }
$resourcePath = Get-QianniuResourcePath $installPath
if (!$resourcePath) { throw "Qianniu resource path not found." }
$webuiZip = Join-Path (Join-Path $resourcePath "newWebui") "webui.zip"
$signPath = Join-Path (Join-Path $resourcePath "newWebui") "sign.json"
if (!(Test-Path $webuiZip)) { throw "webui.zip not found: $webuiZip" }

$backup = "$webuiZip.allhtml.bak-$stamp"
Copy-Item $webuiZip $backup -Force
Write-Host "Backup created: $backup"

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open($webuiZip, [System.IO.Compression.ZipArchiveMode]::Update)
try {
  $injectContent = Read-ZipEntryText $zip $primaryInjectEntryName
  if (!$injectContent) {
    $localInject = Join-Path $repoRoot "src\Bin\inject.js"
    if (!(Test-Path $localInject)) { throw "Cannot find qnbot inject content." }
    $injectContent = Get-Content $localInject -Raw -Encoding UTF8
  }
  $injectContent = Patch-InjectContent $injectContent

  $htmlNames = @($zip.Entries | Where-Object { $_.FullName.EndsWith('.html') } | ForEach-Object { $_.FullName })
  $patched = New-Object System.Collections.Generic.List[string]
  $writeScriptCount = 0

  foreach ($name in $htmlNames) {
    $text = Read-ZipEntryText $zip $name
    if (!$text) { continue }

    $shouldPatch = $EveryHtml -or $text.Contains($officialUrl) -or $text.Contains($oldRemoteUrl) -or $text.Contains($injectFileName)
    if (!$shouldPatch) { continue }

    $newText = $text.Replace($oldRemoteUrl, $injectFileName)
    $newText = $newText.Replace($officialUrl, $injectFileName)

    if (!$newText.Contains($injectFileName)) {
      $tag = '<script src="' + $injectFileName + '"></script>'
      if ($newText -match '</body>') { $newText = $newText -replace '</body>', ($tag + '</body>') }
      elseif ($newText -match '</head>') { $newText = $newText -replace '</head>', ($tag + '</head>') }
      else { $newText += $tag }
    }

    Write-ZipEntryText $zip $name $newText

    $dir = Get-DirNameInZip $name
    $injectEntry = $dir + $injectFileName
    Write-ZipEntryText $zip $injectEntry $injectContent
    $writeScriptCount++
    $patched.Add($name) | Out-Null
  }

  Write-Host "EveryHtml mode: $EveryHtml"
  Write-Host "Patched HTML entries: $($patched.Count)"
  Write-Host "Injected script copies written: $writeScriptCount"
  foreach ($p in $patched) { Write-Host " - $p" }
} finally {
  $zip.Dispose()
}

if (Test-Path $signPath) {
  Clear-Content $signPath -ErrorAction SilentlyContinue
  Write-Host "sign.json cleared: $signPath"
}

Write-Host "Done. Start Bot.exe first, then reopen Qianniu and wait 20 seconds."
