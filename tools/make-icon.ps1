<#
Draws the app icon (a rounded indigo tile with two subtitle lines) and writes src\Hushcue.App\Assets\Hushcue.ico
with 16-256 px images.

    powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
#>
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot "..\src\Hushcue.App\Assets\Hushcue.ico"
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0
    $tile = New-RoundedRect (8 * $s) (8 * $s) (240 * $s) (240 * $s) (56 * $s)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $size, $size),
        [System.Drawing.Color]::FromArgb(255, 124, 140, 255), [System.Drawing.Color]::FromArgb(255, 86, 98, 214))
    $g.FillPath($brush, $tile)
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 255, 255))
    $soft = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(200, 255, 255, 255))
    $g.FillPath($white, (New-RoundedRect (52 * $s) (132 * $s) (152 * $s) (30 * $s) (15 * $s)))
    $g.FillPath($soft, (New-RoundedRect (80 * $s) (176 * $s) (96 * $s) (30 * $s) (15 * $s)))
    # a speech "wave" above the lines
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(230, 255, 255, 255)), ([Math]::Max(1.5, 14 * $s))
    $pen.StartCap = $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    foreach ($bar in @(@(84, 82, 106), @(110, 66, 106), @(136, 74, 106), @(162, 88, 106))) {
        $g.DrawLine($pen, $bar[0] * $s + 4 * $s, $bar[1] * $s, $bar[0] * $s + 4 * $s, $bar[2] * $s)
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($size in $sizes) { , (New-IconPng $size) }
$file = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $file
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Resolve-Path (Split-Path $out)).Path + "\Hushcue.ico", $file.ToArray())
Write-Host "Wrote $out"
