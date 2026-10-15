# Regenerates src/app.ico (exe, tray and installer icon). Run with Windows PowerShell or pwsh.
# 256 px is stored as PNG, smaller sizes as 32-bit DIBs: some icon loaders (including the
# tray at small sizes) don't handle PNG frames.
param([string]$Out = (Join-Path $PSScriptRoot '..\src\app.ico'))

Add-Type -AssemblyName System.Drawing
$sizes = 16, 20, 24, 32, 40, 48, 64, 256

function Draw([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 32.0
    $g.ScaleTransform($s, $s)
    $blue = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(46, 125, 220))
    $dark = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(30, 41, 59))
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    # bezel, screen, stand
    $g.FillRectangle($dark, 1, 3, 30, 21)
    $g.FillRectangle($blue, 3, 5, 26, 17)
    $g.FillRectangle($dark, 13, 24, 6, 3)
    $g.FillRectangle($dark, 8, 27, 16, 3)
    # two arrows: switch between layouts
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 2.2
    $g.DrawLine($pen, 8, 10.5, 22, 10.5)
    $g.FillPolygon($white, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF 25, 10.5), (New-Object System.Drawing.PointF 20.5, 6.5), (New-Object System.Drawing.PointF 20.5, 14.5)))
    $g.DrawLine($pen, 10, 16.5, 24, 16.5)
    $g.FillPolygon($white, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF 7, 16.5), (New-Object System.Drawing.PointF 11.5, 12.5), (New-Object System.Drawing.PointF 11.5, 20.5)))
    $g.Dispose()
    return $bmp
}

function Dib($bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    # BITMAPINFOHEADER; height is doubled for the XOR + AND masks
    $bw.Write([int]40); $bw.Write([int]$w); $bw.Write([int]($h * 2)); $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $h - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $w; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }
    $maskRow = [int]([math]::Ceiling($w / 32.0) * 4)
    $bw.Write((New-Object byte[] ($maskRow * $h)))
    $bw.Flush()
    return ,$ms.ToArray()
}

$images = foreach ($size in $sizes) {
    $bmp = Draw $size
    if ($size -ge 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $data = $ms.ToArray()
    } else {
        $data = Dib $bmp
    }
    $bmp.Dispose()
    [pscustomobject]@{ Size = $size; Data = $data }
}

$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $d = if ($img.Size -ge 256) { 0 } else { $img.Size }
    $bw.Write([byte]$d); $bw.Write([byte]$d); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]$img.Data.Length); $bw.Write([int]$offset)
    $offset += $img.Data.Length
}
foreach ($img in $images) { $bw.Write($img.Data) }
$bw.Dispose()
"Wrote $Out"
