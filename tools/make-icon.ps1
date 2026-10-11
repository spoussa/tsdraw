# Regenerates assets\tsdraw.ico (multi-size, PNG-compressed entries): a blue disc with a
# white pen stroke, the same design as the tray icon.
Add-Type -AssemblyName System.Drawing
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = "AntiAlias"
    $g.PixelOffsetMode = "HighQuality"
    $k = $s / 32.0
    $rect = New-Object System.Drawing.RectangleF (0.5 * $k), (0.5 * $k), (31 * $k), (31 * $k)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(64, 156, 255)), ([System.Drawing.Color]::FromArgb(0, 102, 214)), 90
    $g.FillEllipse($grad, $rect)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(1.6, 3.6 * $k))
    $pen.StartCap = "Round"; $pen.EndCap = "Round"; $pen.LineJoin = "Round"
    $pts = @((New-Object System.Drawing.PointF (7.5 * $k), (21 * $k)), (New-Object System.Drawing.PointF (12.5 * $k), (11.5 * $k)),
             (New-Object System.Drawing.PointF (17.5 * $k), (20 * $k)), (New-Object System.Drawing.PointF (24.5 * $k), (10 * $k)))
    $g.DrawCurve($pen, $pts)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    if ($s -ge 128) {
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)   # large sizes: PNG, as Windows expects
    } else {
        # small sizes: classic 32-bit DIB (bottom-up BGRA + empty AND mask), readable by every icon loader
        $bw = New-Object System.IO.BinaryWriter $ms
        $bw.Write([uint32]40); $bw.Write([int32]$s); $bw.Write([int32](2 * $s)); $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]0); $bw.Write([uint32]0); $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
        for ($y = $s - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $s; $x++) {
                $c = $bmp.GetPixel($x, $y)
                $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
            }
        }
        $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)
        $bw.Write((New-Object byte[] ($maskRow * $s)))
        $bw.Flush()
    }
    $bmp.Dispose()
    , @($s, $ms.ToArray())
}
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $dim = if ($img[0] -ge 256) { 0 } else { $img[0] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$img[1].Length); $w.Write([uint32]$offset)
    $offset += $img[1].Length
}
foreach ($img in $images) { $w.Write($img[1]) }
$w.Flush()
$dir = Join-Path $PSScriptRoot "..\assets"
New-Item -ItemType Directory -Force $dir | Out-Null
[IO.File]::WriteAllBytes((Join-Path $dir "tsdraw.ico"), $out.ToArray())
"wrote assets\tsdraw.ico ($($out.Length) bytes, sizes $($sizes -join ', '))"
