param(
  [switch]$NoRenameData
)

$ErrorActionPreference = "Stop"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"

function Stop-QianniuProcesses {
  foreach ($name in @("Bot", "AliWorkbench", "wwcmd", "wangwang", "AliApp")) {
    Get-Process -Name $name -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  }
  Start-Sleep -Seconds 2
}

function Backup-RenamePath($path) {
  if (!(Test-Path $path)) { return }
  $target = "$path.bak-$stamp"
  Write-Host "Backup/Rename: $path -> $target"
  Rename-Item -LiteralPath $path -NewName (Split-Path $target -Leaf) -Force -ErrorAction SilentlyContinue
  if (Test-Path $path) {
    Write-Host "Rename failed, try remove cache directory: $path"
    Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
  }
}

Stop-QianniuProcesses

Write-Host "Set current user international registry to zh-CN where possible."
try {
  Set-ItemProperty -Path "HKCU:\Control Panel\International" -Name "LocaleName" -Value "zh-CN" -ErrorAction SilentlyContinue
  Set-ItemProperty -Path "HKCU:\Control Panel\International" -Name "sCountry" -Value "China" -ErrorAction SilentlyContinue
  Set-ItemProperty -Path "HKCU:\Control Panel\International" -Name "sLanguage" -Value "CHS" -ErrorAction SilentlyContinue
  Set-ItemProperty -Path "HKCU:\Control Panel\Desktop" -Name "PreferredUILanguages" -Value "zh-CN" -ErrorAction SilentlyContinue
} catch {}

if (!$NoRenameData) {
  Write-Host "Backup and reset Qianniu local profile/cache directories. This may require logging in Qianniu again."
  $paths = @(
    "$env:PUBLIC\Documents\AliWorkBench",
    "$env:PUBLIC\Documents\AliWorkbench",
    "$env:APPDATA\AliWorkbench",
    "$env:LOCALAPPDATA\AliWorkbench",
    "$env:LOCALAPPDATA\Alibaba\AliWorkbench",
    "$env:LOCALAPPDATA\Alibaba\AliWorkBench",
    "$env:ProgramData\AliWorkbench",
    "$env:ProgramData\AliWorkBench"
  ) | Where-Object { $_ } | Select-Object -Unique

  foreach ($p in $paths) {
    Backup-RenamePath $p
  }
}

Write-Host "Done. Reboot Windows, then open Qianniu only first. After Qianniu is simplified Chinese, start Bot.exe."
