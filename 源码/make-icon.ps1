# 生成应用图标（纯 System.Drawing，零依赖）
# 输出：icon.ico（10 个尺寸，32bpp）+ icon-256.png + icon-sheet.png
# 小尺寸(16/20/24)使用无徽章的简化版，保证任务栏/资源管理器里清晰
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$here = $PSScriptRoot
$S = 4

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}
function New-Grad([single]$x1, [single]$y1, [single]$x2, [single]$y2, [string]$c1, [string]$c2) {
    $b = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF($x1, $y1)), (New-Object System.Drawing.PointF($x2, $y2)),
        [System.Drawing.ColorTranslator]::FromHtml($c1), [System.Drawing.ColorTranslator]::FromHtml($c2))
    $b.GammaCorrection = $true
    return $b
}

function New-Art([bool]$withBadge) {
    $N = 256 * $S
    $bmp = New-Object System.Drawing.Bitmap($N, $N, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # 圆角深色底 + 顶部柔光
    $g.FillPath((New-Grad 0 0 0 $N '#243244' '#090C11'), (New-RoundedPath 0 0 $N $N 224))
    $glow = New-Object System.Drawing.Drawing2D.GraphicsPath
    $glow.AddEllipse(-300, -700, $N + 600, $N + 300)
    $gb = New-Object System.Drawing.Drawing2D.PathGradientBrush($glow)
    $gb.CenterColor = [System.Drawing.Color]::FromArgb(40, 130, 205, 255)
    $gb.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 130, 205, 255))
    $g.FillPath($gb, $glow)

    # 电池外壳
    $stroke = if ($withBadge) { 44 } else { 64 }
    $bodyX = 176; $bodyY = 396; $bodyW = 612; $bodyH = 268
    $ink = [System.Drawing.Color]::FromArgb(242, 236, 243, 251)
    $pen = New-Object System.Drawing.Pen($ink, $stroke)
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawPath($pen, (New-RoundedPath $bodyX $bodyY $bodyW $bodyH 76))
    $capPen = New-Object System.Drawing.Pen($ink, $stroke)
    $capPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawPath($capPen, (New-RoundedPath ($bodyX + $bodyW + 10) 480 58 100 28))

    # 电量 90% 填充
    $pad = $stroke + 16
    $ix = $bodyX + $pad; $iy = $bodyY + $pad
    $iw = $bodyW - $pad * 2; $ih = $bodyH - $pad * 2
    $fw = [single]($iw * 0.90)
    $g.FillPath((New-Grad 0 $iy 0 ($iy + $ih) '#A6E6FF' '#2B6FE3'), (New-RoundedPath $ix $iy $fw $ih 40))
    $hiB = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, $iy)), (New-Object System.Drawing.PointF(0, ($iy + $ih * 0.5))),
        [System.Drawing.Color]::FromArgb(86, 255, 255, 255), [System.Drawing.Color]::FromArgb(0, 255, 255, 255))
    $hiB.GammaCorrection = $true
    $g.FillPath($hiB, (New-RoundedPath $ix $iy $fw ($ih * 0.5) 34))

    if ($withBadge) {
        $bx = 872; $by = 856; $br = 142
        $g.FillEllipse((New-Grad ($bx - 120) ($by - 120) ($bx + 120) ($by + 120) '#4CF5A2' '#0B9152'),
            ($bx - $br), ($by - $br), ($br * 2), ($br * 2))
        $ring = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(110, 255, 255, 255), 9)
        $g.DrawEllipse($ring, ($bx - $br), ($by - $br), ($br * 2), ($br * 2))
        $cp = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 40)
        $cp.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $cp.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $cp.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $g.DrawLines($cp, @(
            (New-Object System.Drawing.PointF(($bx - 74), ($by + 2))),
            (New-Object System.Drawing.PointF($bx, ($by + 62))),
            (New-Object System.Drawing.PointF(($bx + 98), ($by - 62)))
        ))
    }

    $g.DrawPath((New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(34, 255, 255, 255), 6)),
        (New-RoundedPath 3 3 ($N - 6) ($N - 6) 222))
    $g.Dispose()
    return $bmp
}

$artBig = New-Art $true
$artSmall = New-Art $false

function Downscale($src, [int]$s) {
    $b = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $gg = [System.Drawing.Graphics]::FromImage($b)
    $gg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $gg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $gg.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $gg.Clear([System.Drawing.Color]::Transparent)
    $gg.DrawImage($src, (New-Object System.Drawing.Rectangle(0, 0, $s, $s)))
    $gg.Dispose()
    return $b
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
$imgs = @{}
foreach ($s in $sizes) {
    $imgs[$s] = if ($s -le 24) { Downscale $artSmall $s } else { Downscale $artBig $s }
}

# ICO：全部 32bpp BMP 条目（兼容性最好）
$icoPath = Join-Path $here 'icon.ico'
$fs = [IO.File]::Create($icoPath)
$bw = New-Object IO.BinaryWriter($fs)
$blobs = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($s in $sizes) {
    $b = $imgs[$s]
    if ($s -ge 128) {
        # 大尺寸用 PNG 压缩存储，减小 exe 体积（Windows Vista+ 支持）
        $pms = New-Object IO.MemoryStream
        $b.Save($pms, [System.Drawing.Imaging.ImageFormat]::Png)
        $blobs.Add($pms.ToArray())
        $pms.Dispose()
        continue
    }
    $rect = New-Object System.Drawing.Rectangle(0, 0, $s, $s)
    $data = $b.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $raw = New-Object byte[] ($stride * $s)
    [Runtime.InteropServices.Marshal]::Copy($data.Scan0, $raw, 0, $raw.Length)
    $b.UnlockBits($data)
    $maskStride = [int]([math]::Ceiling($s / 32.0) * 4)
    $ms = New-Object IO.MemoryStream
    $mw = New-Object IO.BinaryWriter($ms)
    $mw.Write([int]40); $mw.Write([int]$s); $mw.Write([int]($s * 2)); $mw.Write([int16]1); $mw.Write([int16]32)
    $mw.Write([int]0); $mw.Write([int]($s * $s * 4)); $mw.Write([int]0); $mw.Write([int]0); $mw.Write([int]0); $mw.Write([int]0)
    for ($y = $s - 1; $y -ge 0; $y--) { $mw.Write($raw, $y * $stride, $s * 4) }
    $mw.Write((New-Object byte[] ($maskStride * $s)))
    $mw.Flush()
    $blobs.Add($ms.ToArray())
}
$bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $wb = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$wb); $bw.Write([byte]$wb); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int]$blobs[$i].Length); $bw.Write([int]$offset)
    $offset += $blobs[$i].Length
}
foreach ($blob in $blobs) { $bw.Write($blob) }
$bw.Flush(); $bw.Dispose(); $fs.Dispose()

$imgs[256].Save((Join-Path $here 'icon-256.png'), [System.Drawing.Imaging.ImageFormat]::Png)

# 预览：白/深两种背景，含 256 与各小尺寸
$sheet = New-Object System.Drawing.Bitmap(780, 360)
$sg = [System.Drawing.Graphics]::FromImage($sheet)
$sg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$sg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$sg.Clear([System.Drawing.Color]::FromArgb(246, 247, 250))
$sg.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(22, 25, 31))), 390, 0, 390, 360)
foreach ($left in @(30, 420)) {
    $sg.DrawImage($imgs[256], $left, 26, 256, 256)
    $x = $left
    foreach ($s in @(64, 48, 32, 24, 16)) {
        $sg.DrawImage($imgs[$s], $x, 330 - $s, $s, $s)
        $x += $s + 12
    }
}
$sg.Dispose()
$sheet.Save((Join-Path $here 'icon-sheet.png'), [System.Drawing.Imaging.ImageFormat]::Png)

foreach ($k in $imgs.Keys) { $imgs[$k].Dispose() }
$artBig.Dispose(); $artSmall.Dispose(); $sheet.Dispose()

"icon.ico   : " + (Get-Item $icoPath).Length + ' 字节 (' + $sizes.Count + ' 个尺寸)'
