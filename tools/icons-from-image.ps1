param(
    [Parameter(Mandatory)] [string]$Source,     # logo image (png / webp / jpg), tile on a light background
    [Parameter(Mandatory)] [string]$ResDir,     # v2rayN\v2rayN\Resources
    [string]$CleanOut                           # optional: where to save the cleaned 1024px PNG
)
# Builds app.ico, NotifyIcon1-4.ico and logo.png from a logo picture:
# light background around the rounded tile becomes transparent, then the blue route is
# recoloured per tray state (1 off, 2 on, 3 unchanged, 4 PAC).
Add-Type -AssemblyName PresentationCore, WindowsBase, System.Drawing

# --- decode (WIC handles webp) to BGRA bytes
$dec = [System.Windows.Media.Imaging.BitmapDecoder]::Create([Uri](Resolve-Path $Source).Path, 'PreservePixelFormat', 'OnLoad')
$conv = New-Object System.Windows.Media.Imaging.FormatConvertedBitmap($dec.Frames[0], [System.Windows.Media.PixelFormats]::Bgra32, $null, 0)
$W = $conv.PixelWidth; $H = $conv.PixelHeight
$px = New-Object byte[] ($W * $H * 4)
$conv.CopyPixels($px, $W * 4, 0)

# --- background removal and recolouring in C# (PowerShell loops are far too slow for 1M pixels)
Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
public static class LogoImg {
    // Light pixels connected to the image border are the background: transparent, antialiased
    // edge kept as partial alpha in the tile border colour. Returns bbox {minX,minY,maxX,maxY} of what is left.
    public static int[] Clean(byte[] px, int w, int h) {
        var bg = new bool[w * h];
        var q = new Queue<int>();
        for (int x = 0; x < w; x++) { q.Enqueue(x); q.Enqueue((h - 1) * w + x); }
        for (int y = 0; y < h; y++) { q.Enqueue(y * w); q.Enqueue(y * w + w - 1); }
        while (q.Count > 0) {
            int i = q.Dequeue();
            if (bg[i]) continue;
            int o = i * 4;
            if (Math.Min(Math.Min(px[o], px[o + 1]), px[o + 2]) < 120) continue;
            bg[i] = true;
            int x = i % w, y = i / w;
            if (x > 0) q.Enqueue(i - 1);
            if (x < w - 1) q.Enqueue(i + 1);
            if (y > 0) q.Enqueue(i - w);
            if (y < h - 1) q.Enqueue(i + w);
        }
        int minX = w, minY = h, maxX = 0, maxY = 0;
        for (int i = 0; i < w * h; i++) {
            int o = i * 4;
            if (bg[i]) {
                double avg = (px[o] + px[o + 1] + px[o + 2]) / 3.0;
                double a = Math.Max(0, Math.Min(255, (255 - avg) / (255 - 60) * 255));
                if (a < 8) a = 0;
                px[o] = 64; px[o + 1] = 52; px[o + 2] = 46; px[o + 3] = (byte)a;
            }
            if (px[o + 3] > 0) {
                int x = i % w, y = i / w;
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
            }
        }
        return new[] { minX, minY, maxX, maxY };
    }

    // Blue route pixels take the given colour, keeping their brightness.
    public static byte[] Recolor(byte[] src, int r, int g, int b) {
        var d = (byte[])src.Clone();
        for (int o = 0; o < d.Length; o += 4) {
            int pb = d[o], pr = d[o + 2];
            if (pb > pr + 40 && pb > 90) {
                double k = pb / 255.0 / 0.96;
                d[o] = (byte)Math.Min(255, b * k);
                d[o + 1] = (byte)Math.Min(255, g * k);
                d[o + 2] = (byte)Math.Min(255, r * k);
            }
        }
        return d;
    }
}
"@
$box = [LogoImg]::Clean($px, $W, $H)
$minX = $box[0]; $minY = $box[1]; $maxX = $box[2]; $maxY = $box[3]
function To-Bitmap([byte[]]$data) {
    $bmp = New-Object System.Drawing.Bitmap($W, $H, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bd = $bmp.LockBits((New-Object System.Drawing.Rectangle(0, 0, $W, $H)), 'WriteOnly', $bmp.PixelFormat)
    [System.Runtime.InteropServices.Marshal]::Copy($data, 0, $bd.Scan0, $data.Length)
    $bmp.UnlockBits($bd)
    # crop to the tile, square
    $side = [Math]::Max($maxX - $minX + 1, $maxY - $minY + 1)
    $sq = New-Object System.Drawing.Bitmap($side, $side, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($sq)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($bmp, (New-Object System.Drawing.Rectangle(0, 0, ($maxX - $minX + 1), ($maxY - $minY + 1))), $minX, $minY, ($maxX - $minX + 1), ($maxY - $minY + 1), 'Pixel')
    $g.Dispose(); $bmp.Dispose()
    return $sq
}

# Small sizes (tray, 16-24 px): the motif is tiny on the full tile, so use a centre crop of the
# picture on its own rounded tile; the route and the block come out about 1.3x bigger.
function Small-Master([System.Drawing.Bitmap]$src) {
    $n = $src.Width
    $crop = [int]($n * 0.78); $ox = [int](($n - $crop) / 2); $oy = [int](($n - $crop) / 2)
    $b = New-Object System.Drawing.Bitmap($crop, $crop, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'
    $g.Clear([System.Drawing.Color]::Transparent)
    $r = $crop * 0.22; $w = $crop - 1
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, 2 * $r, 2 * $r, 180, 90); $path.AddArc($w - 2 * $r, 0, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($w - 2 * $r, $w - 2 * $r, 2 * $r, 2 * $r, 0, 90); $path.AddArc(0, $w - 2 * $r, 2 * $r, 2 * $r, 90, 90); $path.CloseFigure()
    $g.SetClip($path)
    $g.DrawImage($src, (New-Object System.Drawing.Rectangle(0, 0, $crop, $crop)), $ox, $oy, $crop, $crop, 'Pixel')
    $g.ResetClip()
    $g.DrawPath((New-Object System.Drawing.Pen([System.Drawing.ColorTranslator]::FromHtml('#2E3440'), [Math]::Max(2, $crop / 40))), $path)
    $g.Dispose()
    return $b
}

function Resize([System.Drawing.Bitmap]$src, [int]$s) {
    if ($s -le 24) {
        if (-not $script:smallCache.ContainsKey($src.GetHashCode())) { $script:smallCache[$src.GetHashCode()] = Small-Master $src }
        $src = $script:smallCache[$src.GetHashCode()]
    }
    $b = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'; $g.SmoothingMode = 'AntiAlias'; $g.CompositingQuality = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($src, 0, 0, $s, $s)
    $g.Dispose()
    return $b
}

function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $s = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([int]40); $bw.Write([int]$s); $bw.Write([int]($s * 2)); $bw.Write([int16]1); $bw.Write([int16]32)
    1..6 | ForEach-Object { $bw.Write([int]0) }
    for ($y = $s - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $s; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }
    $bw.Write((New-Object byte[] ([int]([Math]::Ceiling($s / 32.0) * 4) * $s)))
    $bw.Flush()
    return $ms.ToArray()
}

function Write-Ico([System.Drawing.Bitmap]$master, [string]$path, [int[]]$sizes) {
    $frames = @()
    foreach ($s in $sizes) {
        $b = Resize $master $s
        if ($s -ge 256) {
            $ms = New-Object System.IO.MemoryStream
            $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
            $frames += , @($s, $ms.ToArray())
        } else {
            $frames += , @($s, (Get-DibBytes $b))
        }
        $b.Dispose()
    }
    $fs = [System.IO.File]::Create($path)
    $bw = New-Object System.IO.BinaryWriter($fs)
    $bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($f in $frames) {
        $dim = if ($f[0] -ge 256) { 0 } else { $f[0] }
        $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]$f[1].Length); $bw.Write([int]$offset)
        $offset += $f[1].Length
    }
    foreach ($f in $frames) { $bw.Write([byte[]]$f[1]) }
    $bw.Close()
}

$script:smallCache = @{}
$master = To-Bitmap $px
if ($CleanOut) { $master.Save($CleanOut, [System.Drawing.Imaging.ImageFormat]::Png) }
Write-Ico $master (Join-Path $ResDir 'app.ico') @(16, 20, 24, 32, 40, 48, 64, 256)
$logo = Resize $master 256; $logo.Save((Join-Path $ResDir 'logo.png'), [System.Drawing.Imaging.ImageFormat]::Png); $logo.Dispose()

$states = @{ 'NotifyIcon1.ico' = @(124, 133, 146); 'NotifyIcon2.ico' = $null; 'NotifyIcon3.ico' = @(124, 133, 146); 'NotifyIcon4.ico' = @(224, 160, 64) }
foreach ($name in $states.Keys) {
    $bmp = if ($states[$name]) { To-Bitmap ([LogoImg]::Recolor($px, $states[$name][0], $states[$name][1], $states[$name][2])) } else { $master }
    Write-Ico $bmp (Join-Path $ResDir $name) @(16, 20, 24, 32, 48)
    if ($states[$name]) { $bmp.Dispose() }
}
$master.Dispose()
"done: tile $($maxX - $minX + 1)x$($maxY - $minY + 1)"
