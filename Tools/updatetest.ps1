# End-to-end self-update test on a hidden desktop, with a scratch data folder and a local manifest (never GitHub, never the real data).
#   hop 1: the 4.8 Outpost (3.0.1) updates itself to the .NET 10 build (3.1.0)
#   hop 2: that .NET 10 build updates itself to another .NET 10 build (3.1.1)
# Usage (from the repo root; the three exes are published builds, e.g. dotnet publish -p:Version=3.1.0 -o <dir>):
#   pwsh -NoProfile -File Tools\updatetest.ps1 -Old48 <3.0.1 exe> -New310 <3.1.0 exe> -New311 <3.1.1 exe> [-Work <scratch dir>]
# Needs python on PATH (the local update server). Only processes started from the scratch folder are ever stopped.
param([string]$Old48, [string]$New310, [string]$New311, [string]$Work = (Join-Path $env:TEMP 'elans-updatetest'))
$ErrorActionPreference = 'Stop'
$base = $Work
Get-CimInstance Win32_Process -Filter "Name='ElansAddonHub.exe'" | Where-Object { $_.ExecutablePath -like "$base*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
Start-Sleep 1
Remove-Item -Recurse -Force $base -ErrorAction SilentlyContinue
$app = Join-Path $base 'app'; $data = Join-Path $base 'data'; $serve = Join-Path $base 'serve'
New-Item -ItemType Directory -Force $app, $data, $serve | Out-Null
Copy-Item $Old48 (Join-Path $app 'ElansAddonHub.exe')
$port = 18766
$env:ELANSHUB_DATA = $data
$env:ELANSHUB_DEV_SOURCES = '1'
$env:ELANSHUB_HIDDEN_DESKTOP = '1'
@{ manifestUrl = "http://127.0.0.1:$port/manifest.json"; autoUpdate = $false } | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $data 'settings.json')
$server = Start-Process -PassThru -WindowStyle Hidden python -ArgumentList '-m', 'http.server', $port, '--bind', '127.0.0.1', '--directory', $serve
Start-Sleep 1
Add-Type -Path (Join-Path $PSScriptRoot 'HiddenRun.cs')

function Offer($exe, $ver) {
  Copy-Item $exe (Join-Path $serve "hub-$ver.exe") -Force
  $sha = (Get-FileHash (Join-Path $serve "hub-$ver.exe") -Algorithm SHA256).Hash.ToLower()
  @{ hub = @{ version = $ver; url = "http://127.0.0.1:$port/hub-$ver.exe"; sha256 = $sha }; addons = @() } | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 (Join-Path $serve 'manifest.json')
  return $sha
}
function Hop($label, $exe, $ver) {
  $sha = Offer $exe $ver
  $target = Join-Path $app 'ElansAddonHub.exe'
  $code = [HiddenRun]::Run($target, '--test-selfupdate', $app, 120000, 'OutpostUpdTest' + $PID)
  # the new version starts with --updated on the same hidden desktop; give it time to come up and clean up the .old file
  $proc = $null
  for ($i = 0; $i -lt 40 -and -not $proc; $i++) { Start-Sleep -Milliseconds 500; $proc = Get-CimInstance Win32_Process -Filter "Name='ElansAddonHub.exe'" | Where-Object { $_.ExecutablePath -eq $target } }
  Start-Sleep 4
  $now = (Get-FileHash $target -Algorithm SHA256).Hash.ToLower()
  $old = Test-Path ($target + '.old')
  $log = Get-ChildItem $data -Recurse -Filter '*.log' -ErrorAction SilentlyContinue | Get-Content -ErrorAction SilentlyContinue | Select-String 'hub updated|self-update' | Select-Object -Last 3
  "${label}: old exit=$code  exe swapped=$($now -eq $sha)  new version running=$([bool]$proc)  .old left=$old"
  $log | ForEach-Object { "   log: $($_.Line.Trim())" }
  $ver = (Get-Item $target).VersionInfo.ProductVersion
  "   running exe version: $ver"
  if ($proc) { $proc | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }; Start-Sleep 2 }
}
try {
  Hop 'hop 1 (4.8 3.0.1 -> .NET 10 3.1.0)' $New310 '3.1.0'
  Hop 'hop 2 (.NET 10 3.1.0 -> 3.1.1)' $New311 '3.1.1'
} finally {
  Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
  Get-CimInstance Win32_Process -Filter "Name='ElansAddonHub.exe'" | Where-Object { $_.ExecutablePath -like "$app*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
}
