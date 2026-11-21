param([string]$Out = "$PSScriptRoot\..\src\MixDock\MixDock.ico")
Add-Type -AssemblyName System.Drawing
function New-Frame([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
    $r = [int]($s * 0.22)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2; $w = $s - 1
    $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($w - $d, 0, $d, $d, 270, 90)
    $path.AddArc($w - $d, $w - $d, $d, $d, 0, 90); $path.AddArc(0, $w - $d, $d, $d, 90, 90); $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 13, 19, 30))), $path)
    $teal = [System.Drawing.Color]::FromArgb(255, 50, 204, 195); $orange = [System.Drawing.Color]::FromArgb(255, 255, 145, 91)
    $rad = $s * 0.2; $cy = $s * 0.42
    foreach ($pair in @(@(($s * 0.29), $teal), @(($s * 0.71), $orange))) {
        $cx = $pair[0]; $col = $pair[1]
        $pen = New-Object System.Drawing.Pen $col, ([Math]::Max(1.5, $s * 0.055))
        $g.DrawEllipse($pen, $cx - $rad, $cy - $rad, 2 * $rad, 2 * $rad)
        $g.FillEllipse((New-Object System.Drawing.SolidBrush $col), $cx - $s * 0.045, $cy - $s * 0.045, $s * 0.09, $s * 0.09)
        $g.DrawLine($pen, $cx, $cy, $cx + $rad * 0.55, $cy - $rad * 0.55)
    }
    # crossfader
    $y = $s * 0.78
    $track = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 70, 82, 104)), ([Math]::Max(1.5, $s * 0.06))
    $track.StartCap = 'Round'; $track.EndCap = 'Round'
    $g.DrawLine($track, $s * 0.16, $y, $s * 0.84, $y)
    $g.FillRectangle([System.Drawing.Brushes]::White, $s * 0.44, $y - $s * 0.09, $s * 0.12, $s * 0.18)
    $g.Dispose(); return $bmp
}
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $b = New-Frame $s; $ms = New-Object System.IO.MemoryStream; $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); ,$ms.ToArray()
}
$fs = [System.IO.File]::Create($Out); $bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$off = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$len); $bw.Write([uint32]$off)
    $off += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close(); $fs.Close()
"wrote $Out"
