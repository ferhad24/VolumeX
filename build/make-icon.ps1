# Builds the app icon from the original VolumeX logo (assets\volumex-logo.png,
# 1254 px, transparent). Every size is resampled straight from that master, so
# the small sizes are not a blurred copy of a copy.
#
#   assets\volumex.ico       16/24/32/48/64/128/256, for the exe, window and setup
#   assets\volumex-256.png   for the title bar and the tray icon

param(
    [string]$Source = (Join-Path (Split-Path $PSScriptRoot -Parent) "assets\volumex-logo.png"),
    [string]$OutIco = (Join-Path (Split-Path $PSScriptRoot -Parent) "assets\volumex.ico"),
    [string]$OutPng = (Join-Path (Split-Path $PSScriptRoot -Parent) "assets\volumex-256.png")
)

Add-Type -AssemblyName System.Drawing

$master = [System.Drawing.Bitmap]::FromFile($Source)

function New-Size([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality

    # Clamp the edges so resampling does not bleed transparent black into the rim.
    $attributes = New-Object System.Drawing.Imaging.ImageAttributes
    $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
    $g.DrawImage($master, (New-Object System.Drawing.Rectangle 0, 0, $size, $size),
        0, 0, $master.Width, $master.Height, [System.Drawing.GraphicsUnit]::Pixel, $attributes)

    $attributes.Dispose(); $g.Dispose()
    return $bmp
}

$big = New-Size 256
$big.Save($OutPng, [System.Drawing.Imaging.ImageFormat]::Png)
$big.Dispose()

# ICO container of PNG-compressed entries (accepted by Windows since Vista).
$images = @()
foreach ($size in 16, 24, 32, 48, 64, 128, 256) {
    $bmp = New-Size $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $images += , @{ Size = $size; Bytes = $ms.ToArray() }
    $ms.Dispose(); $bmp.Dispose()
}
$master.Dispose()

$fs = [System.IO.File]::Create($OutIco)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $dim = if ($img.Size -ge 256) { 0 } else { $img.Size }
    $bw.Write([Byte]$dim); $bw.Write([Byte]$dim); $bw.Write([Byte]0); $bw.Write([Byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$img.Bytes.Length); $bw.Write([UInt32]$offset)
    $offset += $img.Bytes.Length
}
foreach ($img in $images) { $bw.Write($img.Bytes) }
$bw.Flush(); $bw.Dispose(); $fs.Dispose()

Write-Output "Wrote $OutIco and $OutPng"
