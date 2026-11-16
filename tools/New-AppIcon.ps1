<#
.SYNOPSIS
    Generates src\MediaVerifier\Assets\MediaVerifier.ico from the app icon geometry (subplan 08, App icon).

.DESCRIPTION
    The icon is a shield holding a play triangle on a 256 x 256 grid:
      Shield    M128 18 L226 54 V124 C226 182 185 224 128 242 C71 224 30 182 30 124 V54 Z
      Standard  (32 px and larger) vertical gradient #6ED3FF -> #1F6FE0, triangle (104,86) (180,128) (104,170)
      Small     (24 px and smaller) flat #2B8CEB, triangle (98,80) (186,128) (98,176)
      Triangle  white fill with a 14-unit white round-join stroke
    Each size is rasterised from the geometry with anti-aliasing; nothing is hand-edited.
    Sizes 16, 20, 24 use the small version; 32, 40, 48, 64, 128, 256 the standard one.
    Entries below 64 px are stored as 32-bit DIBs, larger ones as PNG.

.PARAMETER OutFile
    Destination .ico. Defaults to src\MediaVerifier\Assets\MediaVerifier.ico.

.PARAMETER PreviewFolder
    Optional folder that receives one PNG per size, for checking against docs\design\png\icons-dark.png.
#>
[CmdletBinding()]
param(
    [string]$OutFile,
    [string]$PreviewFolder
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
if (-not $OutFile) {
    $scriptFolder = Split-Path -Parent $MyInvocation.MyCommand.Path
    $OutFile = Join-Path $scriptFolder '..\src\MediaVerifier\Assets\MediaVerifier.ico'
}

$smallSizes = @(16, 20, 24)
$standardSizes = @(32, 40, 48, 64, 128, 256)

function New-ShieldPath {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddLine(128, 18, 226, 54)
    $path.AddLine(226, 54, 226, 124)
    $path.AddBezier(226, 124, 226, 182, 185, 224, 128, 242)
    $path.AddBezier(128, 242, 71, 224, 30, 182, 30, 124)
    $path.AddLine(30, 124, 30, 54)
    $path.CloseFigure()
    return $path
}

function New-AppIconBitmap([int]$size) {
    $small = $size -le 24
    $bitmap = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)
        $scale = $size / 256.0
        $g.ScaleTransform($scale, $scale)

        $shield = New-ShieldPath
        if ($small) {
            $fill = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#2B8CEB'))
        } else {
            $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
                (New-Object System.Drawing.PointF(0, 18)),
                (New-Object System.Drawing.PointF(0, 242)),
                [System.Drawing.ColorTranslator]::FromHtml('#6ED3FF'),
                [System.Drawing.ColorTranslator]::FromHtml('#1F6FE0'))
        }
        $g.FillPath($fill, $shield)
        $fill.Dispose()
        $shield.Dispose()

        if ($small) {
            $points = @((New-Object System.Drawing.PointF(98, 80)), (New-Object System.Drawing.PointF(186, 128)), (New-Object System.Drawing.PointF(98, 176)))
        } else {
            $points = @((New-Object System.Drawing.PointF(104, 86)), (New-Object System.Drawing.PointF(180, 128)), (New-Object System.Drawing.PointF(104, 170)))
        }
        $white = [System.Drawing.Color]::White
        $brush = New-Object System.Drawing.SolidBrush($white)
        $pen = New-Object System.Drawing.Pen($white, 14)
        $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $g.FillPolygon($brush, [System.Drawing.PointF[]]$points)
        $g.DrawPolygon($pen, [System.Drawing.PointF[]]$points)
        $brush.Dispose()
        $pen.Dispose()
    } finally {
        $g.Dispose()
    }
    return $bitmap
}

function Get-PngBytes([System.Drawing.Bitmap]$bitmap) {
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    return , $stream.ToArray()
}

# 32-bit DIB as stored inside .ico files: BITMAPINFOHEADER with doubled height, bottom-up BGRA rows,
# then a 1-bit AND mask (all zero: the alpha channel carries transparency).
function Get-DibBytes([System.Drawing.Bitmap]$bitmap) {
    $size = $bitmap.Width
    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($stream)
    $writer.Write([int32]40)
    $writer.Write([int32]$size)
    $writer.Write([int32]($size * 2))
    $writer.Write([int16]1)
    $writer.Write([int16]32)
    $writer.Write([int32]0)
    $writer.Write([int32]($size * $size * 4))
    $writer.Write([int32]0)
    $writer.Write([int32]0)
    $writer.Write([int32]0)
    $writer.Write([int32]0)
    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $c = $bitmap.GetPixel($x, $y)
            $writer.Write([byte]$c.B)
            $writer.Write([byte]$c.G)
            $writer.Write([byte]$c.R)
            $writer.Write([byte]$c.A)
        }
    }
    $maskRowBytes = [int]([math]::Ceiling($size / 32.0) * 4)
    $writer.Write((New-Object byte[] ($maskRowBytes * $size)))
    $writer.Flush()
    return , $stream.ToArray()
}

$sizes = $smallSizes + $standardSizes
$images = @()
foreach ($size in $sizes) {
    $bitmap = New-AppIconBitmap $size
    if ($PreviewFolder) {
        New-Item -ItemType Directory -Force -Path $PreviewFolder | Out-Null
        $bitmap.Save((Join-Path $PreviewFolder ("app-icon-{0}.png" -f $size)), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    if ($size -ge 64) { $bytes = Get-PngBytes $bitmap } else { $bytes = Get-DibBytes $bitmap }
    $images += , @($size, $bytes)
    $bitmap.Dispose()
}

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)
$w.Write([int16]0)              # reserved
$w.Write([int16]1)              # type: icon
$w.Write([int16]$images.Count)
$offset = 6 + (16 * $images.Count)
foreach ($image in $images) {
    $size = $image[0]
    $bytes = $image[1]
    $dimension = if ($size -ge 256) { 0 } else { $size }
    $w.Write([byte]$dimension)
    $w.Write([byte]$dimension)
    $w.Write([byte]0)           # palette colours
    $w.Write([byte]0)           # reserved
    $w.Write([int16]1)          # planes
    $w.Write([int16]32)         # bits per pixel
    $w.Write([int32]$bytes.Length)
    $w.Write([int32]$offset)
    $offset += $bytes.Length
}
foreach ($image in $images) {
    $w.Write([byte[]]$image[1])
}
$w.Flush()

$target = [System.IO.Path]::GetFullPath($OutFile)
New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
[System.IO.File]::WriteAllBytes($target, $out.ToArray())
Write-Host ("Wrote {0} ({1:N0} bytes, sizes {2})" -f $target, $out.Length, ($sizes -join ', '))
