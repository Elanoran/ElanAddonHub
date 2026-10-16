<#
.SYNOPSIS
  Lists what DESIGN.md forbids in src\: raw hex colours, FontSize values outside the Text ramp, margins / paddings off the 4-pt grid,
  CornerRadius values outside {8, 10, 12, 14, pill}. Read-only; run it before a pass and after, the list should only get shorter.

.EXAMPLE
  Tools\design-scan.ps1                      # everything, grouped by rule
  Tools\design-scan.ps1 -Rule hex            # only raw hex colours  (hex | font | space | radius)
  Tools\design-scan.ps1 -Summary             # counts per file only
  Tools\design-scan.ps1 -ShowDeliberate      # also the looks that are kept on purpose (see below)

  Kept on purpose (docs\design\SWEEP.md): the art dictionaries (AddonIcons, Avatars, ReactionIcons), SplashScene, the token file's own colours,
  class / quality / rank / avatar-palette colours, the Bags slot grid, the artwork inside AvatarView's 100-unit Viewbox, the viewer stage margin,
  focus rings (their radius follows the control they hug), circle icon buttons, the in-game overlay's rank-framed pills.
#>
param([ValidateSet('all', 'hex', 'font', 'space', 'radius')][string]$Rule = 'all', [switch]$Summary, [switch]$ShowDeliberate)
function Num($s) { [double]::Parse($s, [Globalization.CultureInfo]::InvariantCulture) }
function OnGrid($v) { $v -eq 2 -or ($v % 4) -eq 0 }      # every multiple of 4; 2 is the one half-step (title-to-subtitle micro gaps)
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\src')).Path
$tokenFile = 'Theme.xaml'                                 # defines the colours: hex is allowed there
$artFiles = 'AddonIcons.xaml', 'Avatars.xaml', 'ReactionIcons.xaml', 'SplashScene.cs'
$files = Get-ChildItem $root -Recurse -Include *.xaml, *.cs | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
$fontOk = 11, 12, 13, 15, 22, 16, 20, 32                  # Text ramp + icon sizes (inline glyph 12, row 16, rail 20, hero 32)
$radiusOk = 0, 8, 10, 12, 14
$deliberate = 'ClassColors|"HUNTER"|"PALADIN"|QualityHex|EdgeHex|Palette =|RoleColor|AccentHex|SlotBg|EmptyEdge|SlotColor|Teal =|Margin="64,56,64,64"|Thickness\(64, 56, 64, 64\)|"#8A94A3"|GradientStop Color="#(FF8000|FFD27A|B85800)"|r == "owner" ? "#FF8000"|"#B048F8"|x:Name="thumb"|x:Name="ph"|Background="#01000000"|Margin="14" Background="{StaticResource Brush.Window}"'
$deliberateFiles = 'AvatarView.xaml', 'OverlayWindow.xaml', 'InventoryPage.Bags.cs', 'WorldMapLayers.cs', 'WorldMapPyramid.cs'    # the world map's marker colours and sea colour (drawn by the web page over game art, not WPF theme colours); scaled artwork in a 100-unit Viewbox; the in-game overlay's rank-framed pills; the Bags slot grid (in-game look)
$devOnly = 'IconSheet.cs', 'Splash.cs'                     # contact sheets for --iconsheet / --splash previews, never shown to users
$radiusPill = 'x:Name="ring"|PART_Track|PART_Indicator|x:Name="th"|CornerRadius="17"|CornerRadius="0,3,3,0"|CornerRadius="13'
$hits = New-Object System.Collections.Generic.List[object]
function Hit($rule, $f, $n, $value, $t) { $hits.Add([pscustomobject]@{ Rule = $rule; File = $f.Name; Line = $n; Value = $value; Text = $t }) }
foreach ($f in $files) {
    if ($artFiles -contains $f.Name) { continue }
    $n = 0
    $inJoin = $false
    foreach ($line in [IO.File]::ReadLines($f.FullName)) {
        $n++
        # the join box and its invite / code entry belong to the invite rework (another branch): left as it is
        if ($f.Name -eq 'LodgeView.xaml') { if ($line -match 'join =====') { $inJoin = $true } elseif ($line -match 'lodge: the page panel') { $inJoin = $false }; if ($inJoin -and -not $ShowDeliberate) { continue } }
        $t = $line.Trim()
        if ($t.StartsWith('//') -or $t.StartsWith('<!--')) { continue }
        if (-not $ShowDeliberate -and ($line -match $deliberate -or $deliberateFiles -contains $f.Name -or $devOnly -contains $f.Name)) { continue }
        if ($f.Name -ne $tokenFile -and $f.Name -notmatch 'Test' -and $Rule -in 'all', 'hex') {
            foreach ($m in [regex]::Matches($line, '#[0-9A-Fa-f]{8}\b|#[0-9A-Fa-f]{6}\b')) { Hit 'hex' $f $n $m.Value $t }
        }
        if ($Rule -in 'all', 'font') {
            foreach ($m in [regex]::Matches($line, 'FontSize(?:"|\s*=\s*)\s*"?([0-9.]+)')) { if ($fontOk -notcontains (Num $m.Groups[1].Value)) { Hit 'font' $f $n $m.Groups[1].Value $t } }
            foreach ($m in [regex]::Matches($line, 'Property="FontSize" Value="([0-9.]+)"')) { if ($fontOk -notcontains (Num $m.Groups[1].Value)) { Hit 'font' $f $n $m.Groups[1].Value $t } }
        }
        if ($Rule -in 'all', 'space') {
            foreach ($m in [regex]::Matches($line, '(?:Margin|Padding)="([^"{]+)"')) {
                foreach ($p in ($m.Groups[1].Value -split '[,\s]+' | Where-Object { $_ -ne '' })) {
                    $v = Num $p; if ($v -lt 0) { continue }       # negative margins are geometry (overlap), not spacing
                    if (-not (OnGrid $v)) { Hit 'space' $f $n $m.Groups[1].Value $t; break }
                }
            }
            $cs = [regex]::Replace($line, 'BorderThickness\s*=\s*new Thickness\([^)]*\)', '')    # a hairline is a stroke, not spacing
            foreach ($m in [regex]::Matches($cs, 'new Thickness\(([^)]*)\)')) {
                foreach ($p in ($m.Groups[1].Value -split '[,\s]+' | Where-Object { $_ -match '^[0-9.]+$' })) {
                    if (-not (OnGrid (Num $p))) { Hit 'space' $f $n $m.Groups[1].Value $t; break }
                }
            }
        }
        if ($Rule -in 'all', 'radius' -and ($ShowDeliberate -or $line -notmatch $radiusPill)) {
            $hh = [regex]::Match($line, 'Height\s*=\s*"?([0-9.]+)'); $ww = [regex]::Match($line, 'Width\s*=\s*"?([0-9.]+)')
            foreach ($m in [regex]::Matches($line, 'CornerRadius="([0-9.,\s]+)"|new CornerRadius\(([0-9.,\s]+)\)')) {
                $val = if ($m.Groups[1].Success) { $m.Groups[1].Value } else { $m.Groups[2].Value }
                foreach ($p in ($val -split '[,\s]+' | Where-Object { $_ -ne '' })) {
                    # a pill / circle: radius = half the height (or width = height)
                    if ($hh.Success -and ((Num $hh.Groups[1].Value) -le 2 * (Num $p) -or ($ww.Success -and $hh.Groups[1].Value -eq $ww.Groups[1].Value))) { continue }
                    if ($radiusOk -notcontains (Num $p)) { Hit 'radius' $f $n $val $t; break }
                }
            }
        }
    }
}
if ($Summary) { $hits | Group-Object File, Rule | Sort-Object Count -Descending | ForEach-Object { '{0,4}  {1}' -f $_.Count, $_.Name }; '{0,4}  TOTAL' -f $hits.Count; return }
foreach ($g in $hits | Group-Object Rule) {
    "== $($g.Name) ($($g.Count))"
    foreach ($h in $g.Group | Sort-Object File, Line) { '{0}:{1}  [{2}]  {3}' -f $h.File, $h.Line, $h.Value, $(if ($h.Text.Length -gt 110) { $h.Text.Substring(0, 110) } else { $h.Text }) }
}
