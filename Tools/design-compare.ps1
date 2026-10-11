<#
.SYNOPSIS
  Puts a BEFORE and an AFTER screenshot side by side (docs\design\compare-*.png): a slim label bar on top, the two pictures top-aligned with a gap.

.EXAMPLE
  Tools\design-compare.ps1 -Before b\chat.png -After a\chat.png -Out docs\design\compare-lodge-chat.png
  Tools\design-compare.ps1 -Before b\x.png -After a\x.png -Out o.png -Crop 90,60,980,640 -Scale 0.6     # crop = x,y,width,height of both pictures
  Tools\design-compare.ps1 -Rows @(@{B='b1.png';A='a1.png';Crop='0,0,500,300'}, @{B='b2.png';A='a2.png'}) -Out o.png     # several pairs stacked

  The pictures come from the design-shot harness (ELANSHUB_DESIGN_SHOTS=<mode> with --selftest on the hidden desktop, see Tools\selftest.ps1).
#>
param([string]$Before, [string]$After, [Parameter(Mandatory)][string]$Out, [string]$Crop, [double]$Scale = 1, [object[]]$Rows,
      [string]$BeforeLabel = 'BEFORE (main)', [string]$AfterLabel = 'AFTER (design-final)')
Add-Type -AssemblyName System.Drawing
if (-not $Rows) { $Rows = @(@{ B = $Before; A = $After; Crop = $Crop }) }
$gap = 16; $bar = 28; $pad = 12
function Load($path, $crop) {
    $img = [Drawing.Image]::FromFile((Resolve-Path $path).Path)
    if ($crop) {
        $c = $crop -split ',' | ForEach-Object { [int]$_ }
        $bmp = New-Object Drawing.Bitmap $c[2], $c[3]
        $g = [Drawing.Graphics]::FromImage($bmp); $g.DrawImage($img, [Drawing.Rectangle]::new(0, 0, $c[2], $c[3]), [Drawing.Rectangle]::new($c[0], $c[1], $c[2], $c[3]), [Drawing.GraphicsUnit]::Pixel); $g.Dispose(); $img.Dispose(); return $bmp
    }
    return $img
}
$pairs = foreach ($r in $Rows) { [pscustomobject]@{ B = Load $r.B $r.Crop; A = Load $r.A $r.Crop } }
$w = [int](($pairs | ForEach-Object { $_.B.Width + $_.A.Width + $gap } | Measure-Object -Maximum).Maximum + 2 * $pad)
$h = [int]($pad + ($pairs | ForEach-Object { $bar + [Math]::Max($_.B.Height, $_.A.Height) + $pad } | Measure-Object -Sum).Sum)
$sheet = New-Object Drawing.Bitmap $w, $h
$g = [Drawing.Graphics]::FromImage($sheet)
$g.Clear([Drawing.Color]::FromArgb(0x0B, 0x0C, 0x0E)); $g.InterpolationMode = 'HighQualityBicubic'; $g.TextRenderingHint = 'ClearTypeGridFit'
$font = New-Object Drawing.Font 'Segoe UI', 10; $brush = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(0x8A, 0x91, 0x99))
$y = $pad
foreach ($p in $pairs) {
    $g.DrawString($BeforeLabel, $font, $brush, $pad, $y + 4); $g.DrawString($AfterLabel, $font, $brush, $pad + $p.B.Width + $gap, $y + 4)
    $g.DrawImage($p.B, $pad, $y + $bar); $g.DrawImage($p.A, $pad + $p.B.Width + $gap, $y + $bar)
    $y += $bar + [Math]::Max($p.B.Height, $p.A.Height) + $pad
}
$g.Dispose()
if ($Scale -ne 1) {
    $sw = [int]($w * $Scale); $sh = [int]($h * $Scale); $small = New-Object Drawing.Bitmap $sw, $sh
    $g2 = [Drawing.Graphics]::FromImage($small); $g2.InterpolationMode = 'HighQualityBicubic'; $g2.DrawImage($sheet, 0, 0, $sw, $sh); $g2.Dispose(); $sheet.Dispose(); $sheet = $small
}
$full = if ([IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $PWD.Path $Out }
New-Item -ItemType Directory -Force (Split-Path $full -Parent) | Out-Null
$sheet.Save($full, [Drawing.Imaging.ImageFormat]::Png)
foreach ($p in $pairs) { $p.B.Dispose(); $p.A.Dispose() }; $sheet.Dispose()
"wrote $Out ($w x $h)"
