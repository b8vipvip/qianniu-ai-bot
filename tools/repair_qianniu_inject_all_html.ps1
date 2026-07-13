param(
  [switch]$EveryHtml
)

$ErrorActionPreference = "Stop"
$repairScript = Join-Path $PSScriptRoot "repair_qianniu_language.ps1"

Write-Warning "This compatibility command no longer copies the full Bot/WebSocket injection into every page."
Write-Host "It now installs only the lightweight zh-CN language guard in every HTML entry and keeps qnbot-inject.js in recent.html."

& powershell -ExecutionPolicy Bypass -File $repairScript
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
