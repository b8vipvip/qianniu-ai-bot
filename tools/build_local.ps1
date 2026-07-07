param(
  [string]$Configuration = "Debug",
  [string]$Platform = "Any CPU"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $Root

Write-Host "=== 1. Locate MSBuild ==="
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (!(Test-Path $vswhere)) {
  throw "未找到 vswhere.exe。请先安装 Visual Studio 2022 Build Tools，并勾选 .NET desktop build tools / .NET Framework 4.8 targeting pack。"
}
$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
if (!$msbuild) { throw "未找到 MSBuild。请确认已安装 Visual Studio Build Tools。" }
Write-Host "MSBuild: $msbuild"

Write-Host "=== 2. Locate NuGet ==="
$nuget = Get-Command nuget.exe -ErrorAction SilentlyContinue
if (!$nuget) {
  $nugetPath = Join-Path $Root "tools\nuget.exe"
  if (!(Test-Path $nugetPath)) {
    Write-Host "下载 nuget.exe ..."
    Invoke-WebRequest -Uri "https://dist.nuget.org/win-x86-commandline/latest/nuget.exe" -OutFile $nugetPath
  }
  $nugetExe = $nugetPath
} else {
  $nugetExe = $nuget.Source
}
Write-Host "NuGet: $nugetExe"

Write-Host "=== 3. Restore packages ==="
& $nugetExe restore "$Root\src\Bot.sln"

Write-Host "=== 4. Build ==="
& $msbuild "$Root\src\Bot.sln" /m /t:Rebuild /p:Configuration=$Configuration /p:Platform="$Platform"

Write-Host "=== 5. Output ==="
$exe = Join-Path $Root "src\Bin\Bot.exe"
if (Test-Path $exe) {
  Get-Item $exe | Select-Object FullName,Length,LastWriteTime
  Write-Host "构建完成：$exe"
} else {
  Write-Warning "没有在 src\Bin 找到 Bot.exe，请检查 MSBuild 输出。"
}
