# Draws the Computah icon (dark screen, glowing blue eyes, small smile) and packs it into computah.ico.
# Original artwork drawn with GDI+; re-run after tweaking to regenerate.
param([string]$Out = "$PSScriptRoot\..\computah.ico", [string]$PreviewDir = "")
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function New-RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Min($r * 2, [Math]::Min($w, $h))
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Draw-Face([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)

    $m = [single]($s * 0.03)            # side margin
    $w = [single]($s - 2 * $m)
    # landscape screen: clearly wider than tall. Snap to whole pixels so small sizes stay crisp and flat.
    $h = [single][Math]::Floor($w * 0.62)
    $top = [single][Math]::Floor(($s - $h) / 2)
    $body = New-RoundRect $m $top $w $h ([single]($s * 0.17))

    # dark screen with a soft top-to-bottom gradient
    $rect = New-Object System.Drawing.RectangleF $m, ($top - 1), $w, ($h + 2)
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 44, 50, 74)), ([System.Drawing.Color]::FromArgb(255, 16, 18, 30)), 90
    $g.FillPath($bg, $body)

    # subtle glass highlight on the upper half
    $g.SetClip($body)
    $hl = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.RectangleF $m, ($top - 1), $w, ($h * 0.5 + 1)), ([System.Drawing.Color]::FromArgb(40, 255, 255, 255)), ([System.Drawing.Color]::FromArgb(0, 255, 255, 255)), 90
    $g.FillRectangle($hl, $m, $top, $w, $h * 0.5)
    $g.ResetClip()

    if ($s -ge 24) {
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(90, 120, 140, 200)), ([single][Math]::Max(1, $s / 64))
        $g.DrawPath($pen, $body)
    }

    $eye = [System.Drawing.Color]::FromArgb(255, 92, 225, 255)
    $ew = [single]($s * 0.165); $eh = [single]($h * 0.42)
    $ey = [single]($top + ($h - $eh) / 2)          # vertically centred on the screen
    foreach ($cx in @([single]($s * 0.34), [single]($s * 0.66))) {
        # glow: radial falloff behind each eye
        if ($s -ge 24) {
            $gr = [single]($s * 0.20)
            $gp = New-Object System.Drawing.Drawing2D.GraphicsPath
            $gp.AddEllipse($cx - $gr, $ey + $eh / 2 - $gr * 1.15, $gr * 2, $gr * 2.3)
            $pg = New-Object System.Drawing.Drawing2D.PathGradientBrush $gp
            $pg.CenterColor = [System.Drawing.Color]::FromArgb(150, 60, 190, 255)
            $pg.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 60, 190, 255))
            $g.FillPath($pg, $gp)
        }
        $ep = New-RoundRect ($cx - $ew / 2) $ey $ew $eh ($ew / 2)
        $g.FillPath((New-Object System.Drawing.SolidBrush $eye), $ep)
        # bright core
        if ($s -ge 32) {
            $cp = New-RoundRect ($cx - $ew * 0.22) ($ey + $eh * 0.14) ($ew * 0.44) ($eh * 0.5) ($ew * 0.22)
            $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(200, 225, 250, 255))), $cp)
        }
    }

    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($sz in $sizes) {
    $b = Draw-Face $sz
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($PreviewDir) { $b.Save((Join-Path $PreviewDir "icon-$sz.png")) }
    $b.Dispose()
    , $ms.ToArray()
}

# ICO container with PNG-compressed entries
$fs = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $len = $pngs[$i].Length
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz }))); $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
[System.IO.File]::WriteAllBytes((Resolve-Path (Split-Path $Out)).Path + "\" + (Split-Path $Out -Leaf), $fs.ToArray())
Write-Host "Wrote $Out"
