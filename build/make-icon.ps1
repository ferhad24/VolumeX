# Renders the Crescendo mark and packs it into a multi-resolution .ico.
#
# The mark: a deep blue disc with an orbiting highlight, a light blue speaker
# cone and five rounded level bars. Drawn in code so every size is rendered at
# its own resolution instead of being downsampled from one bitmap — a 16 px
# favicon made by shrinking a 256 px one turns to mush.

param(
    [string]$OutIco = "D:\Crescendo\assets\crescendo.ico",
    [string]$OutPng = "D:\Crescendo\assets\crescendo-256.png"
)

Add-Type -AssemblyName System.Drawing

function New-Mark([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = $size / 256.0          # everything below is authored at 256 px
    $cx = $size / 2.0
    $cy = $size / 2.0
    $r = 118 * $s

    # --- disc -------------------------------------------------------------
    $discRect = New-Object System.Drawing.RectangleF (($cx - $r), ($cy - $r), (2 * $r), (2 * $r))
    $discPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $discPath.AddEllipse($discRect)
    $discBrush = New-Object System.Drawing.Drawing2D.PathGradientBrush $discPath
    $discBrush.CenterColor = [System.Drawing.Color]::FromArgb(255, 12, 44, 96)
    $discBrush.SurroundColors = @([System.Drawing.Color]::FromArgb(255, 4, 18, 46))
    $g.FillEllipse($discBrush, $discRect)

    # --- orbit: a bright arc over the top right, a dimmer one under the left
    $orbitPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 41, 168, 255)), (13 * $s)
    $orbitPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $orbitPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $arcRect = New-Object System.Drawing.RectangleF (($cx - $r + 7 * $s), ($cy - $r + 7 * $s), (2 * ($r - 7 * $s)), (2 * ($r - 7 * $s)))
    $g.DrawArc($orbitPen, $arcRect, 196, 148)

    $lowerPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 21, 101, 224)), (15 * $s)
    $lowerPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $lowerPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($lowerPen, $arcRect, 20, 130)

    # orbit node
    $nodeR = 15 * $s
    $nodeX = $cx + $r * 0.70
    $nodeY = $cy - $r * 0.70
    $nodeRect = New-Object System.Drawing.RectangleF (($nodeX - $nodeR), ($nodeY - $nodeR), (2 * $nodeR), (2 * $nodeR))
    $nodeBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 82, 194, 255))
    $g.FillEllipse($nodeBrush, $nodeRect)

    # --- speaker ----------------------------------------------------------
    $coneTop = [System.Drawing.Color]::FromArgb(255, 173, 224, 255)
    $coneBottom = [System.Drawing.Color]::FromArgb(255, 30, 144, 255)
    $coneRect = New-Object System.Drawing.RectangleF (50 * $s), (60 * $s), (66 * $s), (136 * $s)
    $coneBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $coneRect, $coneTop, $coneBottom, 60.0

    # One closed outline rather than a box plus an ellipse: at 16 px an ellipse
    # overlapping the cone turns into a smudge.
    $cone = New-Object System.Drawing.Drawing2D.GraphicsPath
    $pts = @(
        (New-Object System.Drawing.PointF (50 * $s), (108 * $s)),
        (New-Object System.Drawing.PointF (78 * $s), (108 * $s)),
        (New-Object System.Drawing.PointF (116 * $s), (60 * $s)),
        (New-Object System.Drawing.PointF (116 * $s), (196 * $s)),
        (New-Object System.Drawing.PointF (78 * $s), (148 * $s)),
        (New-Object System.Drawing.PointF (50 * $s), (148 * $s))
    )
    $cone.AddPolygon($pts)
    $g.FillPath($coneBrush, $cone)

    # --- level bars -------------------------------------------------------
    $barBrushTop = [System.Drawing.Color]::FromArgb(255, 150, 214, 255)
    $barBrushBottom = [System.Drawing.Color]::FromArgb(255, 45, 156, 255)
    # Sized so the tallest bar clears the cone and the last one stays inside the
    # disc: at y = centre the disc edge is at 246, the bars end at 220.
    $heights = @(40, 76, 118, 76, 40)
    $x = 134 * $s
    $barW = 12 * $s
    $gap = 7 * $s

    for ($i = 0; $i -lt $heights.Length; $i++) {
        $h = $heights[$i] * $s
        $rect = New-Object System.Drawing.RectangleF $x, ($cy - $h / 2), $barW, $h
        $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $barBrushTop, $barBrushBottom, 90.0

        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $d = [Math]::Min($barW, $h)
        $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 180)
        $path.AddArc($rect.X, ($rect.Y + $rect.Height - $d), $d, $d, 0, 180)
        $path.CloseFigure()
        $g.FillPath($brush, $path)

        $x += $barW + $gap
    }

    $g.Dispose()
    return $bmp
}

$dir = Split-Path $OutIco -Parent
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

# 256 px PNG for the window icon and any docs.
$big = New-Mark 256
$big.Save($OutPng, [System.Drawing.Imaging.ImageFormat]::Png)

# --- ICO container ---------------------------------------------------------
# Each entry is a PNG-compressed image, which Windows has accepted since Vista
# and which keeps the file small at 256 px.
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = @()
foreach ($sz in $sizes) {
    $bmp = New-Mark $sz
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $images += , @{ Size = $sz; Bytes = $ms.ToArray() }
    $ms.Dispose()
    $bmp.Dispose()
}

$fs = [System.IO.File]::Create($OutIco)
$bw = New-Object System.IO.BinaryWriter $fs

$bw.Write([UInt16]0)               # reserved
$bw.Write([UInt16]1)               # type: icon
$bw.Write([UInt16]$images.Count)

$offset = 6 + (16 * $images.Count)
foreach ($img in $images) {
    $dim = if ($img.Size -ge 256) { 0 } else { $img.Size }
    $bw.Write([Byte]$dim)          # width
    $bw.Write([Byte]$dim)          # height
    $bw.Write([Byte]0)             # palette
    $bw.Write([Byte]0)             # reserved
    $bw.Write([UInt16]1)           # colour planes
    $bw.Write([UInt16]32)          # bits per pixel
    $bw.Write([UInt32]$img.Bytes.Length)
    $bw.Write([UInt32]$offset)
    $offset += $img.Bytes.Length
}
foreach ($img in $images) { $bw.Write($img.Bytes) }

$bw.Flush(); $bw.Dispose(); $fs.Dispose()
$big.Dispose()

Write-Output "Wrote $OutIco and $OutPng"
