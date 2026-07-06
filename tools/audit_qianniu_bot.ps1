param(
  [string]$ProjectDir = "C:\openbot",
  [int]$EventMinutes = 60
)

$ErrorActionPreference = "Continue"
$OutDir = Join-Path $ProjectDir "audit-output"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

"=== Qianniu Bot Local Audit ===" | Out-File (Join-Path $OutDir "00-summary.txt") -Encoding utf8
"ProjectDir: $ProjectDir" | Out-File (Join-Path $OutDir "00-summary.txt") -Encoding utf8 -Append
"Time: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" | Out-File (Join-Path $OutDir "00-summary.txt") -Encoding utf8 -Append

# 1. File inventory
Get-ChildItem -Path $ProjectDir -Recurse -Force |
  Select-Object FullName, Length, LastWriteTime, Attributes |
  Sort-Object FullName |
  Export-Csv (Join-Path $OutDir "01-files.csv") -NoTypeInformation -Encoding utf8

# 2. Hashes for executable files
Get-ChildItem -Path $ProjectDir -Recurse -Force -Include *.exe,*.dll,*.sys,*.bat,*.cmd,*.ps1,*.vbs,*.js,*.json,*.config |
  ForEach-Object {
    $hash = $null
    try { $hash = Get-FileHash $_.FullName -Algorithm SHA256 } catch {}
    [PSCustomObject]@{
      FullName = $_.FullName
      Length = $_.Length
      LastWriteTime = $_.LastWriteTime
      SHA256 = if ($hash) { $hash.Hash } else { "" }
    }
  } |
  Export-Csv (Join-Path $OutDir "02-hashes.csv") -NoTypeInformation -Encoding utf8

# 3. Authenticode signatures
Get-ChildItem -Path $ProjectDir -Recurse -Force -Include *.exe,*.dll,*.sys |
  ForEach-Object {
    $sig = Get-AuthenticodeSignature $_.FullName
    [PSCustomObject]@{
      FullName = $_.FullName
      Status = $sig.Status
      StatusMessage = $sig.StatusMessage
      SignerCertificate = if ($sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { "" }
      Issuer = if ($sig.SignerCertificate) { $sig.SignerCertificate.Issuer } else { "" }
      NotBefore = if ($sig.SignerCertificate) { $sig.SignerCertificate.NotBefore } else { "" }
      NotAfter = if ($sig.SignerCertificate) { $sig.SignerCertificate.NotAfter } else { "" }
    }
  } |
  Export-Csv (Join-Path $OutDir "03-signatures.csv") -NoTypeInformation -Encoding utf8

# 4. Quick readable string extraction for common sensitive patterns
$patterns = "http://|https://|api_key|apikey|token|password|secret|cmd.exe|powershell|reg add|schtasks|startup|webhook|AliWork|千牛|淘宝|taobao|wangwang|deepseek|dashscope|openai"
$stringsOut = Join-Path $OutDir "04-suspicious-strings.txt"
"" | Out-File $stringsOut -Encoding utf8
Get-ChildItem -Path $ProjectDir -Recurse -Force -Include *.exe,*.dll,*.config,*.json,*.txt,*.xml |
  ForEach-Object {
    try {
      $bytes = [System.IO.File]::ReadAllBytes($_.FullName)
      $text = -join ($bytes | ForEach-Object { if ($_ -ge 32 -and $_ -le 126) { [char]$_ } else { "`n" } })
      $hits = ($text -split "`n") | Where-Object { $_ -match $patterns } | Select-Object -First 80
      if ($hits) {
        "===== $($_.FullName) =====" | Out-File $stringsOut -Encoding utf8 -Append
        $hits | Out-File $stringsOut -Encoding utf8 -Append
      }
    } catch {}
  }

# 5. Recent crash logs
$start = (Get-Date).AddMinutes(-1 * $EventMinutes)
Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=$start} -ErrorAction SilentlyContinue |
  Where-Object { $_.ProviderName -match '\.NET Runtime|Application Error|Windows Error Reporting' -or $_.Message -match 'bot|openbot|千牛|AliWorkbench' } |
  Select-Object TimeCreated, ProviderName, Id, LevelDisplayName, Message |
  Format-List |
  Out-File (Join-Path $OutDir "05-crash-events.txt") -Encoding utf8

# 6. Running process command line snapshot
Get-CimInstance Win32_Process |
  Where-Object { $_.Name -match 'bot|openbot|AliWorkbench|Qianniu|千牛|wangwang' -or $_.CommandLine -match 'bot|openbot|AliWorkbench|Qianniu|wangwang' } |
  Select-Object ProcessId, Name, ExecutablePath, CommandLine |
  Format-List |
  Out-File (Join-Path $OutDir "06-processes.txt") -Encoding utf8

# 7. Pack result
$zip = Join-Path $ProjectDir "qianniu-bot-audit-output.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $OutDir\* -DestinationPath $zip -Force

Write-Host "Audit completed: $zip"
Write-Host "Please review files before sharing. Remove API keys or private data if present."
