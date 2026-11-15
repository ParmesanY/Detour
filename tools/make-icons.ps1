param([string]$OutDir)
# Placeholder Detour logo: a route that goes around an obstacle, on a graphite tile.
Add-Type -AssemblyName System.Drawing

function New-Frame([int]$s, [string]$routeHex, [bool]$tile) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $route = [System.Drawing.ColorTranslator]::FromHtml($routeHex)

    if ($tile) {
        $r = [Math]::Max(2, $s * 0.22)
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $w = $s - 1
        $path.AddArc(0, 0, 2*$r, 2*$r, 180, 90)
        $path.AddArc($w - 2*$r, 0, 2*$r, 2*$r, 270, 90)
        $path.AddArc($w - 2*$r, $w - 2*$r, 2*$r, 2*$r, 0, 90)
        $path.AddArc(0, $w - 2*$r, 2*$r, 2*$r, 90, 90)
        $path.CloseFigure()
        $g.FillPath((New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#14181E'))), $path)
        if ($s -ge 32) {
            $g.DrawPath((New-Object System.Drawing.Pen([System.Drawing.ColorTranslator]::FromHtml('#2C333D'), [Math]::Max(1, $s / 64))), $path)
        }
    }

    # obstacle
    $ob = New-Object System.Drawing.RectangleF(($s*0.40), ($s*0.52), ($s*0.20), ($s*0.20))
    $g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#4A5260'))), $ob)

    # route around it
    $pen = New-Object System.Drawing.Pen($route, [Math]::Max(1.6, $s * 0.10))
    $pen.LineJoin = 'Miter'
    $pen.StartCap = 'Flat'; $pen.EndCap = 'Flat'
    $pts = @(
        (New-Object System.Drawing.PointF(($s*0.20), ($s*0.62))),
        (New-Object System.Drawing.PointF(($s*0.31), ($s*0.62))),
        (New-Object System.Drawing.PointF(($s*0.31), ($s*0.36))),
        (New-Object System.Drawing.PointF(($s*0.69), ($s*0.36))),
        (New-Object System.Drawing.PointF(($s*0.69), ($s*0.62))),
        (New-Object System.Drawing.PointF(($s*0.80), ($s*0.62)))
    )
    $g.DrawLines($pen, $pts)

    # end nodes
    $n = [Math]::Max(2, $s * 0.13)
    $b = New-Object System.Drawing.SolidBrush($route)
    $g.FillRectangle($b, ($s*0.20 - $n/2), ($s*0.62 - $n/2), $n, $n)
    $g.FillRectangle($b, ($s*0.80 - $n/2), ($s*0.62 - $n/2), $n, $n)
    $g.Dispose()
    return $bmp
}

function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $s = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([int]40); $bw.Write([int]$s); $bw.Write([int]($s*2)); $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $s - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $s; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }
    $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)
    $bw.Write((New-Object byte[] ($maskRow * $s)))
    $bw.Flush()
    return $ms.ToArray()
}

function Write-Ico([string]$path, [string]$routeHex, [bool]$tile, [int[]]$sizes) {
    $frames = @()
    foreach ($s in $sizes) {
        $bmp = New-Frame $s $routeHex $tile
        if ($s -ge 256) {
            $ms = New-Object System.IO.MemoryStream
            $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
            $frames += ,@($s, $ms.ToArray())
        } else {
            $frames += ,@($s, (Get-DibBytes $bmp))
        }
        $bmp.Dispose()
    }
    $fs = [System.IO.File]::Create($path)
    $bw = New-Object System.IO.BinaryWriter($fs)
    $bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($f in $frames) {
        $s = $f[0]; $data = $f[1]
        $dim = if ($s -ge 256) { 0 } else { $s }
        $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]$data.Length); $bw.Write([int]$offset)
        $offset += $data.Length
    }
    foreach ($f in $frames) { $bw.Write([byte[]]$f[1]) }
    $bw.Close()
}

$accent = '#4F8CFF'
Write-Ico (Join-Path $OutDir 'app.ico') $accent $true @(16, 20, 24, 32, 40, 48, 64, 256)
# Tray: index = SysProxyType + 1 (1 off, 2 on, 3 unchanged, 4 PAC)
Write-Ico (Join-Path $OutDir 'NotifyIcon1.ico') '#7C8592' $true @(16, 20, 24, 32, 48)
Write-Ico (Join-Path $OutDir 'NotifyIcon2.ico') $accent $true @(16, 20, 24, 32, 48)
Write-Ico (Join-Path $OutDir 'NotifyIcon3.ico') '#7C8592' $true @(16, 20, 24, 32, 48)
Write-Ico (Join-Path $OutDir 'NotifyIcon4.ico') '#E0A040' $true @(16, 20, 24, 32, 48)

# Preview PNG to look at
$p = New-Frame 256 $accent $true
$p.Save((Join-Path $OutDir 'logo-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$p.Dispose()
