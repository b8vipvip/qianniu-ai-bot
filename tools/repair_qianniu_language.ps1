param(
  [switch]$RestoreOfficial,
  [switch]$SkipCacheClear
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$injectJs = Join-Path $repoRoot "src\Bin\inject.js"
$marker = "20260712-zh-cn-v3"
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

function Add-HansTextPatch($injectContent) {
  $patch = @'
(function(){
  if (window.__qnbotHansPatchInstalled) return;
  window.__qnbotHansPatchInstalled = true;
  var map = {
    "\u767c":"\u53d1","\u95dc":"\u5173","\u9589":"\u95ed","\u806f":"\u8054","\u7e6b":"\u7cfb","\u7d61":"\u7edc",
    "\u8a02":"\u8ba2","\u55ae":"\u5355","\u865f":"\u53f7","\u8a18":"\u8bb0","\u9304":"\u5f55","\u8cb7":"\u4e70","\u8ce3":"\u5356",
    "\u5f8c":"\u540e","\u8a73":"\u8be6","\u6b77":"\u5386","\u8aee":"\u54a8","\u8a62":"\u8be2","\u5bf6":"\u5b9d","\u8c9d":"\u8d1d",
    "\u8a2d":"\u8bbe","\u85a6":"\u8350","\u512a":"\u4f18","\u8a08":"\u8ba1","\u5132":"\u50a8","\u8cc7":"\u8d44","\u8a55":"\u8bc4",
    "\u50f9":"\u4ef7","\u5099":"\u5907","\u8a3b":"\u6ce8","\u6703":"\u4f1a","\u54e1":"\u5458","\u7522":"\u4ea7","\u7de8":"\u7f16",
    "\u6a19":"\u6807","\u984c":"\u9898","\u8f38":"\u8f93","\u8f49":"\u8f6c","\u6a5f":"\u673a","\u500b":"\u4e2a","\u55ce":"\u5417",
    "\u9019":"\u8fd9","\u7121":"\u65e0","\u8655":"\u5904","\u8cfc":"\u8d2d","\u9ede":"\u70b9","\u64ca":"\u51fb","\u555f":"\u542f",
    "\u78ba":"\u786e","\u8a8d":"\u8ba4","\u7576":"\u5f53","\u4f86":"\u6765","\u9801":"\u9875","\u7db2":"\u7f51","\u958b":"\u5f00",
    "\u8acb":"\u8bf7","\u8a0a":"\u8baf","\u88e1":"\u91cc","\u6eff":"\u6ee1","\u50c5":"\u4ec5","\u689d":"\u6761","\u6578":"\u6570"
  };
  function conv(s){
    if (!s || typeof s !== "string") return s;
    return s.replace(/[\u4e00-\u9fff]/g,function(ch){return map[ch] || ch;});
  }
  function skip(el){
    if (!el || !el.tagName) return false;
    var t = el.tagName.toUpperCase();
    return t === "SCRIPT" || t === "STYLE" || t === "TEXTAREA";
  }
  function convert(root){
    try{
      if (!root) return;
      if (root.nodeType === 3) {
        var n = conv(root.nodeValue);
        if (n !== root.nodeValue) root.nodeValue = n;
        return;
      }
      if (root.nodeType !== 1 && root.nodeType !== 9) return;
      if (skip(root)) return;
      if (root.nodeType === 1) {
        ["title","aria-label","placeholder","value"].forEach(function(a){
          try{ if(root.hasAttribute && root.hasAttribute(a)){ var o=root.getAttribute(a); var n=conv(o); if(n!==o) root.setAttribute(a,n); } }catch(e){}
        });
      }
      var w = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {acceptNode:function(node){return (!node.parentElement || skip(node.parentElement)) ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT;}});
      var arr=[]; while(w.nextNode()) arr.push(w.currentNode); arr.forEach(convert);
    }catch(e){}
  }
  function run(){ try{ convert(document.body || document.documentElement); }catch(e){} }
  window.__qnbotForceHansText = run;
  if(document.readyState === "loading") document.addEventListener("DOMContentLoaded", run); else run();
  setTimeout(run,500); setTimeout(run,1500); setTimeout(run,3500); setInterval(run,4000);
  try{
    var obs = new MutationObserver(function(ms){ms.forEach(function(m){ if(m.type==="characterData") convert(m.target); if(m.addedNodes) m.addedNodes.forEach(convert); if(m.type==="attributes") convert(m.target); });});
    var start=function(){var t=document.body||document.documentElement; if(t) obs.observe(t,{childList:true,subtree:true,characterData:true,attributes:true,attributeFilter:["title","aria-label","placeholder","value"]});};
    if(document.body) start(); else document.addEventListener("DOMContentLoaded",start);
  }catch(e){}
})();
'@

  if ($injectContent -notmatch "__qnbotHansPatchInstalled") {
    return $patch + "`r`n" + $injectContent
  }
  return $injectContent
}

function Add-BodyReadyOfficialScriptPatch($injectContent) {
  if ($injectContent -match "__qnbotAppendOfficialImsupportWhenBodyReady") {
    return $injectContent
  }
  $old = @'
const script = document.createElement("script");
script.type = "text/javascript";
script.src = "https://iseiya.taobao.com/imsupport?locale=zh-CN&lang=zh-CN";
document.getElementsByTagName("body")[0].appendChild(script);
'@
  $new = @'
(function __qnbotAppendOfficialImsupportWhenBodyReady(){
  function append(){
    try{
      if(!document.body){ setTimeout(append, 300); return; }
      const script = document.createElement("script");
      script.type = "text/javascript";
      script.src = "https://iseiya.taobao.com/imsupport?locale=zh-CN&lang=zh-CN";
      document.body.appendChild(script);
      console.log("[qnbot] official imsupport appended after body ready");
    }catch(e){ console.error("[qnbot] append official imsupport failed", e); }
  }
  append();
})();
'@
  if ($injectContent.Contains($old)) {
    Write-Host "Patch qnbot-inject.js: wait document.body before loading official imsupport."
    return $injectContent.Replace($old, $new)
  }
  Write-Host "WARN: Body-ready patch target not found in inject.js."
  return $injectContent
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
    $injectContent = Add-HansTextPatch $injectContent
    $injectContent = Add-BodyReadyOfficialScriptPatch $injectContent

    Write-Host "Write local qnbot-inject.js, force zh-CN locale, and patch UI text."
    foreach ($pattern in @(
      '<script\b[^>]*\bsrc\s*=\s*["''][^"'']*qnbot-inject\.js[^"'']*["''][^>]*>\s*</script\s*>',
      '<script\b[^>]*\bsrc\s*=\s*["''][^"'']*iseiya\.taobao\.com/imsupport[^"'']*["''][^>]*>\s*</script\s*>',
      '<script\b[^>]*\bsrc\s*=\s*["''][^"'']*5CFB5E11D17E63CDD8CB37B52FA6ACFD\.js[^"'']*["''][^>]*>\s*</script\s*>'
    )) {
      $recent = [regex]::Replace($recent, $pattern, '', [System.Text.RegularExpressions.RegexOptions]'IgnoreCase, Singleline')
    }
    $tag = '<script src="' + $injectSrc + '"></script>'
    $head = [regex]::Match($recent, '<head\b[^>]*>', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if ($head.Success) {
      $recent = $recent.Insert($head.Index + $head.Length, $tag)
    } else {
      $recent = $tag + $recent
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
