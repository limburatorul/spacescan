# Draws SpaceScan.ico: a disk-usage donut in the brand accent on the brand ground.
Add-Type -AssemblyName System.Drawing

function New-IconBitmap([int]$s) {
    $bmp = New-Object Drawing.Bitmap $s, $s
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'

    $r = [Math]::Max(2, $s * 0.22)
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $r * 2, $r * 2, 180, 90)
    $path.AddArc($s - $r * 2 - 1, 0, $r * 2, $r * 2, 270, 90)
    $path.AddArc($s - $r * 2 - 1, $s - $r * 2 - 1, $r * 2, $r * 2, 0, 90)
    $path.AddArc(0, $s - $r * 2 - 1, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $tile = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.Point 0, 0), (New-Object Drawing.Point $s, $s), ([Drawing.Color]::FromArgb(19, 26, 41)), ([Drawing.Color]::FromArgb(10, 13, 19))
    $g.FillPath($tile, $path)
    if ($s -ge 32) { $g.DrawPath((New-Object Drawing.Pen ([Drawing.Color]::FromArgb(70, 255, 255, 255)), ($s / 128)), $path) }

    # Donut: dim track, accent sweep = "space used".
    $thick = $s * 0.145
    $box = New-Object Drawing.RectangleF ($s * 0.215), ($s * 0.215), ($s * 0.57), ($s * 0.57)
    $track = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(34, 48, 73)), $thick
    $g.DrawArc($track, $box, 0, 360)
    $arc = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.PointF 0, 0), (New-Object Drawing.PointF $s, $s), ([Drawing.Color]::FromArgb(124, 176, 255)), ([Drawing.Color]::FromArgb(58, 124, 240))
    $pen = New-Object Drawing.Pen $arc, $thick
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $g.DrawArc($pen, $box, -80, 250)
    if ($s -ge 48) { $g.FillEllipse((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(76, 141, 255))), ($s * 0.46), ($s * 0.46), ($s * 0.08), ($s * 0.08)) }
    $g.Dispose()
    return $bmp
}

# Frames: classic 32-bit DIB up to 64px (what older shells read), PNG for the big ones.
function New-Frame([Drawing.Bitmap]$bmp) {
    $s = $bmp.Width
    if ($s -gt 64) { $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); return , [byte[]]$ms.ToArray() }
    $ms = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $ms
    $w.Write([uint32]40); $w.Write([int32]$s); $w.Write([int32]($s * 2)); $w.Write([uint16]1); $w.Write([uint16]32)
    0, 0, 0, 0, 0, 0 | ForEach-Object { $w.Write([uint32]$_) }
    for ($y = $s - 1; $y -ge 0; $y--) { for ($x = 0; $x -lt $s; $x++) { $c = $bmp.GetPixel($x, $y); $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A) } }
    $stride = [Math]::Ceiling($s / 32) * 4              # AND mask: fully transparent, rows padded to 4 bytes
    for ($y = 0; $y -lt $s; $y++) { $w.Write((New-Object byte[] $stride)) }
    $w.Flush(); return , [byte[]]$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 48, 64, 128, 256
$frames = New-Object "System.Collections.Generic.List[byte[]]"
foreach ($s in $sizes) { $frames.Add([byte[]](New-Frame (New-IconBitmap $s))) }

# ICO container: 6-byte header, 16 bytes per entry, then the frames.
$out = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256))   # 256 is stored as 0
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$frames[$i].Length); $w.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($f in $frames) { $w.Write($f) }
$w.Flush()
[IO.File]::WriteAllBytes("$PSScriptRoot\SpaceScan.ico", $out.ToArray())
"SpaceScan.ico: $($sizes.Count) sizes, $((Get-Item "$PSScriptRoot\SpaceScan.ico").Length) bytes"
