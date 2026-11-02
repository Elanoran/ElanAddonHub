<#
  Layout watch: does a WoW Forever build change the layout of a game table the Outpost reads?
  The Outpost reads each DB2 table only in the layouts it was written against (a layout hash it does not know switches that table off,
  nothing is guessed). wowdev/WoWDBDefs lists every build's layouts within days, often before the launcher installs the build here, so this
  script warns ahead of players:
    - our tables: every table whose table hash appears as a 0x........ constant in src (matched through WoWDBDefs' manifest.json)
    - for the newest 1.60.1 build WoWDBDefs lists (and the build installed here, when it is newer or different): the layout of each table
      in that build; a layout hash that appears nowhere in src is a NEW LAYOUT - printed with its fields next to the layout we know
      (field numbers count stored columns, like Services\Db2\Wdc5.cs: $noninline$ columns are not fields, an array is one field)
    Tools\layoutwatch.ps1                    check the newest Forever build WoWDBDefs knows (and the installed one)
    Tools\layoutwatch.ps1 -Build 1.60.1.70707
    Tools\layoutwatch.ps1 -Offline           use the cached definitions only
  Exit code 1 when a table has a new layout, 2 when the definitions could not be read; 0 otherwise.
  The definitions are cached in %TEMP%\elans-wowdbdefs (CC BY-SA 4.0, wowdev/WoWDBDefs; read here, never shipped).
#>
param([string]$Build, [switch]$Offline, [string]$Wow = $env:ELANSHUB_WOW)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$cache = Join-Path $env:TEMP 'elans-wowdbdefs'
$raw = 'https://raw.githubusercontent.com/wowdev/WoWDBDefs/master'
New-Item -ItemType Directory -Force (Join-Path $cache 'definitions') | Out-Null

function Fetch([string]$rel) {
    $path = Join-Path $cache $rel
    if (-not $Offline) {
        try { Invoke-WebRequest -Uri "$raw/$rel" -OutFile "$path.part" -UseBasicParsing -TimeoutSec 30; Move-Item -Force "$path.part" $path }
        catch { Remove-Item "$path.part" -ErrorAction SilentlyContinue; if (-not (Test-Path $path)) { throw "could not fetch $rel ($($_.Exception.Message))" } }
    }
    if (-not (Test-Path $path)) { throw "$rel is not cached (run without -Offline once)" }
    return $path
}

# "1.60.1.70707" -> sortable number; null when not a Forever (1.60.1) build
function BuildNo([string]$b) { if ($b -match '^1\.60\.1\.(\d+)$') { [int]$Matches[1] } else { $null } }

# a .dbd's layout blocks: hashes, builds (single builds and a-b ranges), fields in order (noninline ones kept but marked)
function ParseDbd([string]$path) {
    $blocks = @(); $cur = $null
    foreach ($line in [IO.File]::ReadAllLines($path)) {
        if ($line -match '^LAYOUT\s+(.+)$') { $cur = [pscustomobject]@{ Hashes = @($Matches[1] -split ',\s*' | ForEach-Object { $_.Trim().ToUpperInvariant() }); Builds = @(); Ranges = @(); Fields = @() }; $blocks += $cur; continue }
        if ($null -eq $cur) { continue }
        if ($line.Trim() -eq '') { $cur = $null; continue }
        if ($line -match '^BUILD\s+(.+)$') {
            foreach ($part in ($Matches[1] -split ',\s*')) {
                $p = $part.Trim()
                if ($p -match '^(\S+)-(\S+)$') { $cur.Ranges += ,@($Matches[1], $Matches[2]) } else { $cur.Builds += $p }
            }
            continue
        }
        if ($line -match '^COMMENT') { continue }
        $noninline = $line -match '^\$[^$]*noninline[^$]*\$'
        $name = ($line -replace '^\$[^$]*\$', '' -replace '<[^>]*>', '' -replace '//.*$', '').Trim()
        $cur.Fields += [pscustomobject]@{ Name = $name; NonInline = $noninline }
    }
    return $blocks
}

function Lists($block, [string]$b) {
    if ($block.Builds -contains $b) { return $true }
    $n = BuildNo $b
    foreach ($r in $block.Ranges) { $lo = BuildNo $r[0]; $hi = BuildNo $r[1]; if ($null -ne $n -and $null -ne $lo -and $null -ne $hi -and $n -ge $lo -and $n -le $hi) { return $true } }
    return $false
}

function FieldList($block) {
    $i = 0
    foreach ($f in $block.Fields) { if ($f.NonInline) { "      -  $($f.Name)  (not a field)" } else { "     {0,2}  {1}" -f $i, $f.Name; $i++ } }
}

# ---- the hashes our code knows
$src = Join-Path $repo 'src'
$ours = [Collections.Generic.HashSet[string]]::new()
Get-ChildItem $src -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | ForEach-Object {
    foreach ($m in [regex]::Matches([IO.File]::ReadAllText($_.FullName), '0x([0-9A-Fa-f]{8})\b')) { [void]$ours.Add($m.Groups[1].Value.ToUpperInvariant()) }
}

try { $manifest = Get-Content -Raw (Fetch 'manifest.json') | ConvertFrom-Json }
catch { Write-Host "layoutwatch: $($_.Exception.Message)"; exit 2 }
$tables = @($manifest | Where-Object { $_.tableHash -and $ours.Contains($_.tableHash.ToUpperInvariant()) } | Sort-Object tableName)
if ($tables.Count -eq 0) { Write-Host 'layoutwatch: no table of ours found in the manifest'; exit 2 }

$defs = @{}
foreach ($t in $tables) {
    try { $defs[$t.tableName] = ParseDbd (Fetch "definitions/$($t.tableName).dbd") }
    catch { Write-Host "layoutwatch: $($t.tableName): $($_.Exception.Message)" }
}

# ---- which builds: the asked one, else the newest Forever build any of our tables lists; plus the installed one
$installed = $null
foreach ($root in @($Wow, 'C:\Program Files (x86)\World of Warcraft', 'D:\Blizzard\World of Warcraft', 'D:\World of Warcraft', 'C:\World of Warcraft') | Where-Object { $_ }) {
    $bi = Join-Path $root '.build.info'
    if (Test-Path $bi) {
        $lines = Get-Content $bi; $head = $lines[0] -split '\|' | ForEach-Object { ($_ -split '!')[0] }
        $iv = [array]::IndexOf($head, 'Version'); $ip = [array]::IndexOf($head, 'Product')
        foreach ($l in $lines | Select-Object -Skip 1) { $c = $l -split '\|'; if ($ip -ge 0 -and $c[$ip] -eq 'wowf') { $installed = $c[$iv] } }
        if ($installed) { break }
    }
}
$known = @($defs.Values | ForEach-Object { $_ } | ForEach-Object { $_.Builds + ($_.Ranges | ForEach-Object { $_[1] }) } | Where-Object { BuildNo $_ } | Sort-Object { BuildNo $_ } -Unique)
$targets = @()
if ($Build) { $targets += $Build } else { if ($known.Count) { $targets += $known[-1] } }
if ($installed -and $targets -notcontains $installed) { $targets += $installed }
Write-Host ("layoutwatch: {0} tables of ours; WoWDBDefs' newest Forever build: {1}; installed here: {2}" -f $tables.Count, ($(if ($known.Count) { $known[-1] } else { 'none' })), ($(if ($installed) { $installed } else { 'not found' })))

$new = 0
foreach ($b in $targets) {
    $ok = 0; $unlisted = @(); $hashOnly = @()
    Write-Host ""
    Write-Host "== $b"
    foreach ($t in $tables) {
        $bl = $defs[$t.tableName]; if (-not $bl) { continue }
        $hit = @($bl | Where-Object { Lists $_ $b })
        if ($hit.Count -eq 0) { $unlisted += $t.tableName; continue }
        $fresh = @($hit | Where-Object { -not ($_.Hashes | Where-Object { $ours.Contains($_) }) })
        if ($fresh.Count -eq 0) { $ok++; continue }
        $mine = $bl | Where-Object { $_.Hashes | Where-Object { $ours.Contains($_) } } | Select-Object -Last 1
        if (-not $mine) { $hashOnly += $t.tableName; continue }   # known by its table hash only (hotfix rows), no layout of it is read
        $new++
        Write-Host ("NEW LAYOUT  {0} ({1})  {2} -> {3}" -f $t.tableName, $t.db2FileDataID, ($(if ($mine) { $mine.Hashes -join ',' } else { '?' })), ($fresh[0].Hashes -join ','))
        if ($mine) { Write-Host '    the layout we know:'; FieldList $mine | Write-Host }
        Write-Host "    the layout of ${b}:"; FieldList $fresh[0] | Write-Host
    }
    Write-Host ("{0} tables in a layout we know" -f $ok)
    if ($hashOnly.Count) { Write-Host ("known by table hash only, no layout read ({0}): {1}" -f $hashOnly.Count, ($hashOnly -join ', ')) }
    if ($unlisted.Count) { Write-Host ("not listed for {0} yet ({1}): {2}" -f $b, $unlisted.Count, ($unlisted -join ', ')) }
}
Write-Host ""
if ($new) { Write-Host "layoutwatch: $new table(s) with a new layout - add the layout (and check the field numbers) in src\Services\..."; exit 1 }
Write-Host 'layoutwatch: OK'
exit 0
