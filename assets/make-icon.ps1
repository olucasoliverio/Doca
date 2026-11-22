# Draws Doca's icon (the ⊞ logo on a blue→violet squircle, same as the Preferences sidebar) at every size
# Windows asks for and packs them into Assets\doca.ico (PNG entries). Run again after changing the design:
#   powershell -ExecutionPolicy Bypass -File Assets\make-icon.ps1
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

function Render-Size([int]$px) {
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    # Small sizes fill the square (every pixel counts); larger ones keep a little air, like other Windows 11 icons.
    $m = if ($px -le 24) { 0 } else { [math]::Round($px * 0.06) }
    $s = $px - 2 * $m
    $rect = New-Object System.Windows.Rect $m, $m, $s, $s
    $r = $s * 0.24
    $c1 = [System.Windows.Media.Color]::FromRgb(0x4C, 0x9B, 0xFF)
    $c2 = [System.Windows.Media.Color]::FromRgb(0x8A, 0x4D, 0xFF)
    $bg = New-Object System.Windows.Media.LinearGradientBrush $c1, $c2, 45
    $dc.DrawRoundedRectangle($bg, $null, $rect, $r, $r)
    # Soft light from the top (skipped where it would only blur a 16 px icon).
    if ($px -ge 32) {
        $hl = New-Object System.Windows.Media.LinearGradientBrush ([System.Windows.Media.Color]::FromArgb(0x38, 255, 255, 255)), ([System.Windows.Media.Color]::FromArgb(0, 255, 255, 255)), 90
        $dc.DrawRoundedRectangle($hl, $null, $rect, $r, $r)
    }
    # The four squares: 44% of the tile, centered.
    $g = $s * 0.44
    $gap = $g * 0.08
    $q = ($g - $gap) / 2
    $x0 = $m + ($s - $g) / 2
    $y0 = $m + ($s - $g) / 2
    $qr = if ($px -ge 48) { $q * 0.12 } else { 0 }
    foreach ($i in 0, 1) { foreach ($j in 0, 1) {
        $qrect = New-Object System.Windows.Rect ($x0 + $i * ($q + $gap)), ($y0 + $j * ($q + $gap)), $q, $q
        $dc.DrawRoundedRectangle([System.Windows.Media.Brushes]::White, $null, $qrect, $qr, $qr)
    } }
    $dc.Close()
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $px, $px, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($dv)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object System.IO.MemoryStream
    $enc.Save($ms)
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($px in $sizes) { , (Render-Size $px) }

# ICO: header, one 16-byte entry per image, then the PNG data.
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($k = 0; $k -lt $sizes.Count; $k++) {
    $px = $sizes[$k]; $data = $pngs[$k]
    $w.Write([byte]($px % 256)); $w.Write([byte]($px % 256)) # 256 is written as 0
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$data.Length); $w.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($data in $pngs) { $w.Write($data) }
$w.Flush()
$path = Join-Path $PSScriptRoot 'doca.ico'
[System.IO.File]::WriteAllBytes($path, $out.ToArray())
"ok: $path ($($out.Length) bytes)"
