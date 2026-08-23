param([string]$OutDir, [string]$AppAssets, [string]$PackageImages)

Add-Type -AssemblyName System.Drawing

# Claret mark: a yellow shell prompt (">_") on a burgundy plate.
# Drawn as geometry rather than text so it stays crisp from 16px to 256px.
$Plate    = [System.Drawing.Color]::FromArgb(255, 140, 35,  50)   # #8C2332, the app accent
$PlateTop = [System.Drawing.Color]::FromArgb(255, 162, 44,  62)   # #A22C3E
$PlateBot = [System.Drawing.Color]::FromArgb(255, 117, 27,  40)   # #751B28
$Mark     = [System.Drawing.Color]::FromArgb(255, 255, 200,  69)  # #FFC845, chevron and cursor

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-Icon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [single]$size

    # Full-bleed rounded plate on every asset, the taskbar "unplated" variant included: the mark
    # always sits on burgundy rather than on whatever the shell happens to put behind it.
    # A slight vertical gradient keeps the plate from looking flat at large sizes without
    # muddying the 16px rendering.
    $inset = $s * 0.02
    $rect = New-Object System.Drawing.RectangleF($inset, $inset, ($s - 2 * $inset), ($s - 2 * $inset))
    $radius = $s * 0.20
    $path = New-RoundedPath $rect.X $rect.Y $rect.Width $rect.Height $radius

    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, $inset)),
        (New-Object System.Drawing.PointF(0, $s)),
        $PlateTop, $PlateBot)
    $g.FillPath($brush, $path)
    $brush.Dispose()

    # Hairline highlight so the plate keeps an edge on dark backgrounds.
    if ($size -ge 32) {
        $penWidth = [Math]::Max(1.0, $s * 0.012)
        $edge = New-Object System.Drawing.Pen(
            [System.Drawing.Color]::FromArgb(46, 255, 255, 255), $penWidth)
        $g.DrawPath($edge, $path)
        $edge.Dispose()
    }

    $path.Dispose()
    $ink = $Mark

    # Below ~24px the caret turns to mush, so small icons carry a bigger chevron alone.
    $tiny = $size -le 20

    $stroke = [single]($s * $(if ($tiny) { 0.13 } else { 0.095 }))
    $pen = New-Object System.Drawing.Pen($ink, $stroke)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    if ($tiny) {
        # Chevron only, centred and enlarged.
        $chevron = @(
            (New-Object System.Drawing.PointF(($s * 0.36), ($s * 0.28))),
            (New-Object System.Drawing.PointF(($s * 0.62), ($s * 0.50))),
            (New-Object System.Drawing.PointF(($s * 0.36), ($s * 0.72)))
        )
        $g.DrawLines($pen, $chevron)
    }
    else {
        # Chevron: ">"
        $chevron = @(
            (New-Object System.Drawing.PointF(($s * 0.29), ($s * 0.32))),
            (New-Object System.Drawing.PointF(($s * 0.47), ($s * 0.50))),
            (New-Object System.Drawing.PointF(($s * 0.29), ($s * 0.68)))
        )
        $g.DrawLines($pen, $chevron)

        # Cursor: "_"
        $g.DrawLine($pen, ($s * 0.56), ($s * 0.68), ($s * 0.74), ($s * 0.68))
    }

    $pen.Dispose()
    $g.Dispose()
    return $bmp
}

function Save-Png([int]$size, [string]$path) {
    $bmp = New-Icon $size
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    "  {0,-52} {1}x{1}" -f (Split-Path $path -Leaf), $size
}

function Save-WideMark([int]$w, [int]$h, [string]$path) {
    # Wide tile / splash: the square mark centred on a transparent canvas.
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    $side = [int]([Math]::Min($w, $h) * 0.62)
    $mark = New-Icon $side
    $g.DrawImage($mark, [int](($w - $side) / 2), [int](($h - $side) / 2), $side, $side)
    $mark.Dispose()
    $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    "  {0,-52} {1}x{2}" -f (Split-Path $path -Leaf), $w, $h
}

function Save-Ico([int[]]$sizes, [string]$path) {
    # ICO with PNG-compressed entries: supported by Windows Vista and later.
    $streams = @()
    foreach ($size in $sizes) {
        $bmp = New-Icon $size
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        $streams += , @($size, $ms.ToArray())
        $ms.Dispose()
    }

    $out = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($out)
    $writer.Write([UInt16]0)                  # reserved
    $writer.Write([UInt16]1)                  # type: icon
    $writer.Write([UInt16]$streams.Count)

    $offset = 6 + 16 * $streams.Count
    foreach ($entry in $streams) {
        $size = $entry[0]
        $bytes = $entry[1]
        $writer.Write([Byte]($(if ($size -ge 256) { 0 } else { $size })))
        $writer.Write([Byte]($(if ($size -ge 256) { 0 } else { $size })))
        $writer.Write([Byte]0)                # palette entries
        $writer.Write([Byte]0)                # reserved
        $writer.Write([UInt16]1)              # colour planes
        $writer.Write([UInt16]32)             # bits per pixel
        $writer.Write([UInt32]$bytes.Length)
        $writer.Write([UInt32]$offset)
        $offset += $bytes.Length
    }

    foreach ($entry in $streams) { $writer.Write($entry[1]) }

    $writer.Flush()
    [System.IO.File]::WriteAllBytes($path, $out.ToArray())
    $writer.Dispose()
    $out.Dispose()
    "  {0,-52} {1} sizes" -f (Split-Path $path -Leaf), $streams.Count
}

if ($AppAssets) {
    New-Item -ItemType Directory -Force -Path $AppAssets | Out-Null
    "app assets -> $AppAssets"
    Save-Ico @(16, 20, 24, 32, 40, 48, 64, 128, 256) (Join-Path $AppAssets 'AppIcon.ico')
}

if ($PackageImages) {
    New-Item -ItemType Directory -Force -Path $PackageImages | Out-Null
    "package images -> $PackageImages"

    # The Store wants the whole scale ladder, not just scale-200: a 200% asset downscaled to
    # 125% or 150% — the scale most laptops actually run at — comes out soft. The mark is drawn
    # as geometry at each size instead, so every asset is sharp at its own resolution.
    # Base sizes are fixed by the MSIX manifest; 125/150/200/400 are 1.25x/1.5x/2x/4x of them.
    $Scales = @(100, 125, 150, 200, 400)

    function Save-Ladder([int]$base, [string]$name) {
        foreach ($scale in $Scales) {
            $size = [int][Math]::Ceiling($base * $scale / 100.0)
            Save-Png $size (Join-Path $PackageImages "$name.scale-$scale.png")
        }
    }

    function Save-WideLadder([int]$w, [int]$h, [string]$name) {
        foreach ($scale in $Scales) {
            $sw = [int][Math]::Ceiling($w * $scale / 100.0)
            $sh = [int][Math]::Ceiling($h * $scale / 100.0)
            Save-WideMark $sw $sh (Join-Path $PackageImages "$name.scale-$scale.png")
        }
    }

    Save-Ladder 50  'StoreLogo'
    Save-Ladder 44  'Square44x44Logo'
    Save-Ladder 150 'Square150x150Logo'
    Save-WideLadder 310 150 'Wide310x150Logo'
    Save-WideLadder 620 300 'SplashScreen'

    # Taskbar and jump lists ask for an exact pixel size rather than a scale. The unplated
    # variants keep the burgundy plate on purpose (see New-Icon) — only the name is "unplated".
    foreach ($target in @(16, 24, 32, 48, 256)) {
        Save-Png $target (Join-Path $PackageImages "Square44x44Logo.targetsize-$target.png")
        Save-Png $target (Join-Path $PackageImages "Square44x44Logo.targetsize-${target}_altform-unplated.png")
    }
}

if (-not $OutDir) { return }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
"previews -> $OutDir"
Save-Ico @(16, 20, 24, 32, 40, 48, 64, 128, 256) (Join-Path $OutDir 'AppIcon.ico')
Save-Png 256 (Join-Path $OutDir 'preview-256.png')
Save-Png 48 (Join-Path $OutDir 'preview-48.png')
Save-Png 20 (Join-Path $OutDir 'preview-20.png')
Save-Png 16 (Join-Path $OutDir 'preview-16.png')
Save-Png 24 (Join-Path $OutDir 'preview-24.png')
