<#
.SYNOPSIS
  Lists what DESIGN.md forbids in src\: raw hex colours, FontSize values outside the Text ramp, margins / paddings off the 4-pt scale,
  CornerRadius values outside {8, 10, 12, 14, pill}. Read-only; run it before a pass and after, the list should only get shorter.

.EXAMPLE
  Tools\design-scan.ps1                 # everything, grouped by rule
  Tools\design-scan.ps1 -Rule hex       # only raw hex colours  (hex | font | space | radius)
  Tools\design-scan.ps1 -Summary        # counts per file only

  Deliberate exceptions (see docs\design\SWEEP.md): the art dictionaries (AddonIcons, Avatars, ReactionIcons), SplashScene, the token file itself,
  class / quality / rank colours, the Bags slot grid, scaled artwork inside a Viewbox, focus rings (their radius follows the control they hug).
#>
param([ValidateSet('all', 'hex', 'font', 'space', 'radius')][string]$Rule = 'all', [switch]$Summary, [switch]$ShowDeliberate)
function Num($s) { [double]::Parse($s, [Globalization.CultureInfo]::InvariantCulture) }
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\src')).Path
$skipFiles = 'AddonIcons.xaml', 'Avatars.xaml', 'ReactionIcons.xaml', 'SplashScene.cs', 'Theme.xaml'
$files = Get-ChildItem $root -Recurse -Include *.xaml, *.cs | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
$fontOk = 11, 12, 13, 15, 22, 16, 20, 32          # Text ramp + icon sizes (inline glyph 12, row 16, rail 20, hero 32)
$spaceOk = 0, 2, 4, 8, 12, 16, 24, 32, 48
$radiusOk = 0, 8, 10, 12, 14
# deliberate looks (docs/design/SWEEP.md): class / quality / rank / avatar-palette colours, the Bags slot grid, scaled artwork inside AvatarView's 100-unit Viewbox,
# the viewer stage margin, game-overlay rank frame. Hidden unless -ShowDeliberate.
$deliberate = 'ClassColors|"HUNTER"|"PALADIN"|QualityHex|EdgeHex|Palette =|RoleColor|AccentHex|SlotBg|EmptyEdge|SlotColor|Teal =|Margin="64,56,64,64"|new Thickness(64, 56, 64, 64)|"#8A94A3"|rank frame|GradientStop Color="#(FF8000|FFD27A|B85800)"'
$artFiles = 'AvatarView.xaml'
$hits = New-Object System.Collections.Generic.List[object]
foreach ($f in $files) {
    $isTheme = $skipFiles -contains $f.Name
    $n = 0
    foreach ($line in [IO.File]::ReadLines($f.FullName)) {
        $n++
        $t = $line.Trim()
        if ($t.StartsWith('//') -or $t.StartsWith('<!--')) { continue }
        if (-not $ShowDeliberate -and ($line -match $deliberate -or $artFiles -contains $f.Name)) { continue }
        if (-not $isTheme -and $Rule -in 'all', 'hex') {
            foreach ($m in [regex]::Matches($line, '#[0-9A-Fa-f]{8}\b|#[0-9A-Fa-f]{6}\b')) { $hits.Add([pscustomobject]@{ Rule = 'hex'; File = $f.Name; Line = $n; Value = $m.Value; Text = $t }) }
        }
        if ($f.Name -in 'AddonIcons.xaml', 'Avatars.xaml', 'ReactionIcons.xaml', 'SplashScene.cs') { continue }
        if ($Rule -in 'all', 'font') {
            foreach ($m in [regex]::Matches($line, 'FontSize(?:"|\s*=\s*)\s*"?([0-9.]+)')) { $v = (Num $m.Groups[1].Value); if ($fontOk -notcontains $v) { $hits.Add([pscustomobject]@{ Rule = 'font'; File = $f.Name; Line = $n; Value = $m.Groups[1].Value; Text = $t }) } }
        }
        if ($Rule -in 'all', 'space') {
            foreach ($m in [regex]::Matches($line, '(?:Margin|Padding)="([^"{]+)"')) {
                foreach ($p in ($m.Groups[1].Value -split '[,\s]+' | Where-Object { $_ -ne '' })) {
                    $v = (Num $p); if ($v -lt 0) { continue }   # negative margins are geometry (overlap), not spacing
                    if ($spaceOk -notcontains $v) { $hits.Add([pscustomobject]@{ Rule = 'space'; File = $f.Name; Line = $n; Value = $m.Groups[1].Value; Text = $t }); break }
                }
            }
            foreach ($m in [regex]::Matches($line, 'new Thickness\(([^)]*)\)')) {
                foreach ($p in ($m.Groups[1].Value -split '[,\s]+' | Where-Object { $_ -match '^[0-9.]+$' })) {
                    if ($spaceOk -notcontains (Num $p)) { $hits.Add([pscustomobject]@{ Rule = 'space'; File = $f.Name; Line = $n; Value = $m.Groups[1].Value; Text = $t }); break }
                }
            }
        }
        if ($Rule -in 'all', 'radius') {
            foreach ($m in [regex]::Matches($line, 'CornerRadius="([0-9.,\s]+)"|new CornerRadius\(([0-9.,\s]+)\)')) {
                $val = if ($m.Groups[1].Success) { $m.Groups[1].Value } else { $m.Groups[2].Value }
                foreach ($p in ($val -split '[,\s]+' | Where-Object { $_ -ne '' })) {
                    $hh = [regex]::Match($line, 'Height="([0-9.]+)"'); $ww = [regex]::Match($line, 'Width="([0-9.]+)"')
                    if ($hh.Success -and ((Num $hh.Groups[1].Value) -le 2 * (Num $p) -or ($ww.Success -and $hh.Groups[1].Value -eq $ww.Groups[1].Value))) { continue }   # a pill / circle: radius = half the height
                    if ($radiusOk -notcontains (Num $p)) { $hits.Add([pscustomobject]@{ Rule = 'radius'; File = $f.Name; Line = $n; Value = $val; Text = $t }); break }
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
