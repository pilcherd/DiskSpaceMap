$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing.Common
$assets = Join-Path $PSScriptRoot '../DiskSpaceMap/Assets'
$assets = [System.IO.Path]::GetFullPath($assets)
[System.IO.Directory]::CreateDirectory($assets) | Out-Null

# Match AppIcon.svg, drawing at 4x resolution before downsampling each ICO frame.
function Add-RoundedRectangle($graphics, $brush, [single]$x, [single]$y, [single]$w, [single]$h, [single]$radius) {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    try {
        $diameter = $radius * 2
        $path.AddArc($x, $y, $diameter, $diameter, 180, 90)
        $path.AddArc($x + $w - $diameter, $y, $diameter, $diameter, 270, 90)
        $path.AddArc($x + $w - $diameter, $y + $h - $diameter, $diameter, $diameter, 0, 90)
        $path.AddArc($x, $y + $h - $diameter, $diameter, $diameter, 90, 90)
        $path.CloseFigure()
        $graphics.FillPath($brush, $path)
    } finally { $path.Dispose() }
}

$canvas = [System.Drawing.Bitmap]::new(1024, 1024)
$graphics = [System.Drawing.Graphics]::FromImage($canvas)
try {
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.ScaleTransform(4, 4)
    foreach ($shape in @(
        @{ Color = '#344358'; X = 1; Y = 1; W = 254; H = 254; R = 45 },
        @{ Color = '#151E29'; X = 3; Y = 3; W = 250; H = 250; R = 43 },
        @{ Color = '#72E2BF'; X = 22; Y = 22; W = 124; H = 212; R = 8 },
        @{ Color = '#5699CF'; X = 156; Y = 22; W = 78; H = 92; R = 8 },
        @{ Color = '#B49AEA'; X = 156; Y = 124; W = 78; H = 110; R = 8 }
    )) {
        $brush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($shape.Color))
        try { Add-RoundedRectangle $graphics $brush $shape.X $shape.Y $shape.W $shape.H $shape.R }
        finally { $brush.Dispose() }
    }
    $sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
    $frames = [System.Collections.Generic.List[byte[]]]::new()
    foreach ($size in $sizes) {
        $bitmap = [System.Drawing.Bitmap]::new($size, $size)
        $scaled = [System.Drawing.Graphics]::FromImage($bitmap)
        $stream = [System.IO.MemoryStream]::new()
        try {
            $scaled.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $scaled.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $scaled.DrawImage($canvas, 0, 0, $size, $size)
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $frames.Add($stream.ToArray())
            if ($size -eq 256) { $bitmap.Save((Join-Path $assets 'AppIcon.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
        } finally { $stream.Dispose(); $scaled.Dispose(); $bitmap.Dispose() }
    }
    $file = [System.IO.File]::Create((Join-Path $assets 'AppIcon.ico'))
    $writer = [System.IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
            $offset += $frames[$i].Length
        }
        foreach ($frame in $frames) { $writer.Write($frame) }
    } finally { $writer.Dispose(); $file.Dispose() }
} finally { $graphics.Dispose(); $canvas.Dispose() }
Write-Output 'Created AppIcon.ico (9 sizes) and AppIcon.png.'
