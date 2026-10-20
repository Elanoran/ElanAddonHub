<#
  Starts a developer's Outpost (3.2) for work on the 3D world map with an assistant through Tools/OutpostDevMcp:
    - built with -p:ElansDevPipe=true (src/Services/DevPipe.cs: the pipe only this Windows user can open), into its own folder
    - its own data folder (default %LOCALAPPDATA%\ElansAddonHub-dev): never your real settings, Lodge login or caches; it runs next to your real Outpost
    - such a build never updates itself and does not register the elanshub:// links
  First start: the new data folder gets only the WoW folder from your real settings (nothing else is copied).
    Tools\devoutpost.ps1                       build and start
    Tools\devoutpost.ps1 -NoBuild              start the last build
    Tools\devoutpost.ps1 -Data D:\outpost-dev  another data folder
    Tools\devoutpost.ps1 -MapCache <folder>    first start: copy a world map cache (atlas\worldmap\<build>\...) into the data folder, e.g. one a self-test made
#>
param([switch]$NoBuild, [string]$Data, [string]$MapCache)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$out = Join-Path $repo 'src\bin\devpipe'
if (-not $Data) { $Data = Join-Path $env:LOCALAPPDATA 'ElansAddonHub-dev' }
$real = Join-Path $env:LOCALAPPDATA 'ElansAddonHub'
if ((Resolve-Path -LiteralPath $real -ErrorAction SilentlyContinue) -and ([IO.Path]::GetFullPath($Data).TrimEnd('\') -ieq [IO.Path]::GetFullPath($real).TrimEnd('\'))) { throw "the developer's Outpost must not use the real data folder ($real)" }

if (-not $NoBuild) {
    Write-Host "Building the developer's Outpost (Debug, dev pipe on) into $out ..."
    & dotnet build (Join-Path $repo 'src\ElansAddonHub.csproj') -c Debug -p:ElansDevPipe=true -o $out --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }
    & dotnet build (Join-Path $repo 'Tools\OutpostDevMcp\OutpostDevMcp.csproj') -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'MCP server build failed' }
}

New-Item -ItemType Directory -Force $Data | Out-Null
$settings = Join-Path $Data 'settings.json'
if (-not (Test-Path $settings)) {
    $wow = $null
    $rs = Join-Path $real 'settings.json'
    if (Test-Path $rs) { try { $wow = (Get-Content $rs -Raw | ConvertFrom-Json).wowRoot } catch { } }
    @{ wowRoot = $wow } | ConvertTo-Json | Set-Content -Encoding UTF8 $settings
    Write-Host "New data folder $Data (WoW folder: $wow)"
}
if ($MapCache) {
    $dst = Join-Path $Data 'atlas\worldmap'
    New-Item -ItemType Directory -Force $dst | Out-Null
    Copy-Item -Recurse -Force (Join-Path $MapCache '*') $dst
    Write-Host "Copied the world map cache from $MapCache"
}

$exe = Join-Path $out 'ElansAddonHub.exe'
if (-not (Test-Path $exe)) { throw "no build at $exe (run without -NoBuild)" }
$env:ELANSHUB_DATA = $Data
$env:ELANS_DEV_PAGE = Join-Path $repo 'src\WorldMap'
Start-Process -FilePath $exe -WorkingDirectory $out
Write-Host "Started. The assistant connects through the outpost-dev MCP server; the page files reload from $env:ELANS_DEV_PAGE."
