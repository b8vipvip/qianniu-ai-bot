param(
  [int]$MaxElements = 1500
)

$ErrorActionPreference = "Continue"
$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$outDir = Join-Path $repoRoot "diagnostics"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$out = Join-Path $outDir "qianniu-runtime-$stamp.txt"

function Write-Line($s = "") {
  $s | Tee-Object -FilePath $out -Append | Out-Null
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

Write-Line "============ Qianniu Runtime Diagnostics $stamp ============"
Write-Line "Computer: $env:COMPUTERNAME"
Write-Line "User: $env:USERNAME"
Write-Line "PWD: $(Get-Location)"
Write-Line ""

Write-Line "---- Processes ----"
try {
  Get-CimInstance Win32_Process |
    Where-Object { $_.Name -match 'Ali|wangwang|wwcmd|Bot' -or $_.CommandLine -match 'AliWorkbench|alires|webui|imsupport|qnbot' } |
    Select-Object ProcessId, ParentProcessId, Name, CommandLine |
    ForEach-Object {
      Write-Line ("PID={0} PPID={1} NAME={2}" -f $_.ProcessId, $_.ParentProcessId, $_.Name)
      Write-Line ("CMD={0}" -f $_.CommandLine)
      Write-Line ""
    }
} catch { Write-Line "Process dump failed: $($_.Exception.Message)" }

Write-Line "---- Netstat 41010 ----"
try { netstat -ano | findstr ":41010" | ForEach-Object { Write-Line $_ } } catch {}
Write-Line ""

Write-Line "---- Qianniu install/resources ----"
$installPath = Get-QianniuInstallPath
Write-Line "InstallPath=$installPath"
$resourcePath = $null
if ($installPath) { $resourcePath = Get-QianniuResourcePath $installPath }
Write-Line "ResourcePath=$resourcePath"
Write-Line ""

if ($resourcePath) {
  $webuiZip = Join-Path (Join-Path $resourcePath "newWebui") "webui.zip"
  Write-Line "---- webui.zip scan ----"
  Write-Line "webuiZip=$webuiZip"
  try {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($webuiZip)
    try {
      $patterns = @('imsdk','im.singlemsg','onReceiveNewMsg','GetNewMsg','iseiya.taobao.com/imsupport','qnbot-inject','worklink.oss')
      foreach ($entry in @($zip.Entries | Where-Object { $_.FullName -match '\.(html|js)$' })) {
        $text = Read-ZipEntryText $zip $entry.FullName
        if (!$text) { continue }
        $hits = @()
        foreach ($p in $patterns) { if ($text.IndexOf($p, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $hits += $p } }
        if ($hits.Count -gt 0) { Write-Line ("ZIP-HIT {0} => {1}" -f $entry.FullName, ($hits -join ',')) }
      }
    } finally { if ($zip) { $zip.Dispose() } }
  } catch { Write-Line "webui.zip scan failed: $($_.Exception.Message)" }
  Write-Line ""
}

Write-Line "---- Local/cache file scan, newest likely runtime assets ----"
$roots = @(
  "$env:PUBLIC\Documents\AliWorkBench",
  "$env:PUBLIC\Documents\AliWorkbench",
  "$env:APPDATA\AliWorkbench",
  "$env:LOCALAPPDATA\AliWorkbench",
  "$env:LOCALAPPDATA\Alibaba\AliWorkbench",
  "$env:LOCALAPPDATA\Alibaba\AliWorkBench",
  $resourcePath
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique

foreach ($root in $roots) {
  Write-Line "SCAN-ROOT $root"
  try {
    Get-ChildItem -Path $root -Recurse -File -ErrorAction SilentlyContinue |
      Where-Object { $_.Length -lt 5MB -and $_.Extension -match '\.(html|js|json|txt|log)$' } |
      Sort-Object LastWriteTime -Descending |
      Select-Object -First 2000 |
      ForEach-Object {
        try {
          $txt = Get-Content $_.FullName -Raw -ErrorAction Stop
          $hits = @()
          foreach ($p in @('imsdk','im.singlemsg','onReceiveNewMsg','GetNewMsg','iseiya.taobao.com/imsupport','qnbot-inject','worklink.oss','MutilChatView')) {
            if ($txt.IndexOf($p, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $hits += $p }
          }
          if ($hits.Count -gt 0) {
            Write-Line ("FILE-HIT {0} | {1} | {2}" -f $_.FullName, $_.LastWriteTime, ($hits -join ','))
          }
        } catch {}
      }
  } catch { Write-Line "scan failed: $root $($_.Exception.Message)" }
}
Write-Line ""

Write-Line "---- UI Automation tree: MutilChatView / AliWorkbench ----"
try {
  Add-Type -AssemblyName UIAutomationClient
  Add-Type -AssemblyName UIAutomationTypes
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $trueCond = [System.Windows.Automation.Condition]::TrueCondition
  $wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $trueCond)
  $targets = @()
  for ($i=0; $i -lt $wins.Count; $i++) {
    $w = $wins.Item($i)
    $name = $w.Current.Name
    $class = $w.Current.ClassName
    $pid = $w.Current.ProcessId
    if (($class -match 'MutilChatView|AliWorkbench|Chrome|Cef|Qt' -or $name -match '千牛|接待|客服|旺旺|Ali') -and $pid) {
      $targets += $w
      Write-Line ("TOP-WINDOW hwnd={0} pid={1} class={2} name={3}" -f $w.Current.NativeWindowHandle, $pid, $class, $name)
    }
  }

  $n = 0
  foreach ($w in $targets) {
    Write-Line ""
    Write-Line ("UI-DUMP-START hwnd={0} class={1} name={2}" -f $w.Current.NativeWindowHandle, $w.Current.ClassName, $w.Current.Name)
    $all = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, $trueCond)
    for ($i=0; $i -lt $all.Count -and $n -lt $MaxElements; $i++) {
      $el = $all.Item($i)
      $name = $el.Current.Name
      $class = $el.Current.ClassName
      $aid = $el.Current.AutomationId
      $ctype = $el.Current.ControlType.ProgrammaticName
      $rect = $el.Current.BoundingRectangle
      if ($name -or $class -or $aid) {
        $line = "UI pid={0} hwnd={1} type={2} class={3} aid={4} rect={5},{6},{7},{8} name={9}" -f $el.Current.ProcessId, $el.Current.NativeWindowHandle, $ctype, $class, $aid, [int]$rect.Left, [int]$rect.Top, [int]$rect.Width, [int]$rect.Height, (($name -replace '\r|\n',' ') -replace '\s+',' ')
        Write-Line $line
        $n++
      }
    }
    Write-Line "UI-DUMP-END"
  }
} catch { Write-Line "UI Automation dump failed: $($_.Exception.Message)" }

Write-Line ""
Write-Line "Done. Output: $out"
Write-Host "Done. Output: $out"
