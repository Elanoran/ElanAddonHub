<#
.SYNOPSIS
  Build Release and run the Outpost self-test in a fresh scratch folder, scoped to what changed.

.EXAMPLE
  Tools\selftest.ps1                          # --changed origin/main (what this branch touched)
  Tools\selftest.ps1 -Only inventory,toasts   # just those sections
  Tools\selftest.ps1 -Changed v2.33.0         # everything since a tag
  Tools\selftest.ps1 -Full                    # every section: do this before a release
  Tools\selftest.ps1 -List                    # section names

  Exit code: 0 = no new failures, 1 = new FAIL lines (or the run did not finish), 2 = could not start.
  The self-test always runs in its own data folder under $env:TEMP, never in %LOCALAPPDATA%\ElansAddonHub
  (App.xaml.cs refuses that too).
#>
param(
    [string[]]$Only,
    [string]$Changed,
    [switch]$Full,
    [switch]$List,
    [switch]$NoBuild,
    [switch]$Visible,   # run on the real desktop (watch it); default is a hidden desktop that never steals focus
    [int]$TimeoutMinutes = 15
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$exe = Join-Path $root 'src\bin\Release\ElansAddonHub.exe'

# FAIL lines that are expected today (environment, not regressions). Anything else FAIL-ish is "new".
$known = @(
    'check-all button: FAIL'                 # needs the real update server
    'zip touching another addon: NOT REFUSED' # present on main before the section split
)

if (-not $NoBuild) {
    Write-Host 'Building Release...'
    Push-Location (Join-Path $root 'src')
    try {
        $out = & dotnet build -c Release 2>&1
        if ($LASTEXITCODE -ne 0) { $out | Select-Object -Last 30 | Write-Host; Write-Host 'BUILD FAILED'; exit 2 }
    } finally { Pop-Location }
}
if (-not (Test-Path $exe)) { Write-Host "No exe at $exe"; exit 2 }

if ($List) { & $exe --list | Out-String | Write-Host; exit 0 }

$argv = @()
if ($Full) { $mode = 'FULL (all sections)' }
elseif ($Only) { $argv += @('--only', ($Only -join ',')); $mode = '--only ' + ($Only -join ',') }
else {
    if (-not $Changed) {
        & git -C $root rev-parse --verify --quiet origin/main 2>$null | Out-Null
        $Changed = if ($LASTEXITCODE -eq 0) { 'origin/main' } else { 'main' }
    }
    $argv += @('--changed', $Changed, '--repo', $root); $mode = "--changed $Changed"
}

# fresh scratch dir; refuse anything that is, or sits inside, the real data folder
$scratch = Join-Path $env:TEMP ('elans-selftest-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$real = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'ElansAddonHub')).TrimEnd('\')
if ([IO.Path]::GetFullPath($scratch).TrimEnd('\').StartsWith($real, [StringComparison]::OrdinalIgnoreCase)) { Write-Host "Refusing: $scratch is inside $real"; exit 2 }
New-Item -ItemType Directory -Path $scratch | Out-Null
Write-Host ("Self-test $mode" + $(if ($Visible) { " (visible)" } else { " (hidden desktop)" }))
Write-Host "Scratch: $scratch"

$sw = [Diagnostics.Stopwatch]::StartNew()
$argLine = (@('--selftest', "`"$scratch`"") + ($argv | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } })) -join ' '
if ($Visible) {
    Remove-Item Env:\ELANSHUB_HIDDEN_DESKTOP -ErrorAction SilentlyContinue
    $p = Start-Process -FilePath $exe -ArgumentList $argLine -PassThru
    if (-not $p.WaitForExit($TimeoutMinutes * 60000)) { $p.Kill(); Write-Host "TIMED OUT after $TimeoutMinutes min"; exit 1 }
    $code = $p.ExitCode
} else {
    # own invisible desktop: no window, toast or dialog can show up on screen or take focus
    Add-Type -Path (Join-Path $PSScriptRoot 'HiddenRun.cs')
    $env:ELANSHUB_HIDDEN_DESKTOP = '1'
    try { $code = [HiddenRun]::Run($exe, $argLine, $root, $TimeoutMinutes * 60000, 'OutpostSelfTest' + $PID) }
    finally { Remove-Item Env:\ELANSHUB_HIDDEN_DESKTOP -ErrorAction SilentlyContinue }
    if ($code -eq -1000) { Write-Host 'Could not start the self-test on a hidden desktop (use -Visible)'; exit 2 }
    if ($code -eq -1001) { Write-Host "TIMED OUT after $TimeoutMinutes min (self-test process terminated)"; exit 1 }
}
$wall = $sw.Elapsed.TotalSeconds
$resultFile = Join-Path $scratch 'result.txt'
if (-not (Test-Path $resultFile)) { Write-Host "No result.txt (exit code $code): the run did not finish"; exit 1 }
$lines = Get-Content $resultFile

Write-Host ''
Get-Content (Join-Path $scratch 'scope.txt') -ErrorAction SilentlyContinue | ForEach-Object { Write-Host $_ }
Write-Host ''
$skipped = @($lines | Where-Object { $_ -match '^skipped ' })
if ($skipped) { Write-Host ('Skipped: ' + (($skipped | ForEach-Object { $_ -replace '^skipped ', '' }) -join ', ')) }

$bad = @($lines | Where-Object { $_ -cmatch 'FAIL|crashed|NOT REFUSED' })
# the "passed" summary lines contain no FAIL; per-check "FAIL " / "FAILED" / "crashed" lines do
$new = @($bad | Where-Object { $l = $_; -not ($known | Where-Object { $l.Contains($_) }) })
$old = @($bad | Where-Object { $l = $_; $known | Where-Object { $l.Contains($_) } })
if ($old) { Write-Host 'Known failures (ignored):'; $old | ForEach-Object { Write-Host ('  ' + $_.Trim().Substring(0, [Math]::Min(150, $_.Trim().Length))) } }
if ($new) { Write-Host 'FAIL lines:' -ForegroundColor Red; $new | ForEach-Object { Write-Host ('  ' + $_.Trim()) -ForegroundColor Red } }

Write-Host ''
Write-Host 'Timing (slowest first):'
$lines | ForEach-Object { if ($_ -match '^section (\S+): ([\d.]+) s\s*$') { [pscustomobject]@{ Name = $Matches[1]; Sec = [double]$Matches[2] } } } |
    Sort-Object Sec -Descending | ForEach-Object { Write-Host ('  {0,-15}{1,7:0.0} s' -f $_.Name, $_.Sec) }
$tot = $lines | Where-Object { $_ -match '^total: ' }
Write-Host "  $tot   (wall clock incl. start/exit: $([math]::Round($wall,1)) s)"
Write-Host "Result: $resultFile"
if ($new) { Write-Host "NEW FAILURES: $($new.Count)" -ForegroundColor Red; exit 1 }
Write-Host 'OK: no new failures'
exit 0
