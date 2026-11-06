# 日本一周ドライブ3D のアイコンを作る（富士山・赤い太陽・まっすぐ延びる高速道路）。
# 各サイズを直接描くので、小さいアイコンでもぼやけにくい。
# 使い方: powershell -ExecutionPolicy Bypass -File tools/make-icon.ps1
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
$outIco = Join-Path $root "JapanDrive3D\icon.ico"
$outPng = Join-Path $root "JapanDrive3D\icon.png"
$sizes = 256, 128, 64, 48, 32, 24, 16

function Draw-Icon([int]$S, [bool]$Square = $false) {
    $bmp = New-Object System.Drawing.Bitmap $S, $S, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $k = $S / 256.0
    function P([double]$x, [double]$y) { New-Object System.Drawing.PointF ([float]($x * $k)), ([float]($y * $k)) }

    # 角の丸い四角（空）
    $r = 46 * $k
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    if ($Square) { $path.AddRectangle((New-Object System.Drawing.RectangleF 0, 0, $S, $S)) } else {
    $path.AddArc(0, 0, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($S - 2 * $r - 1, 0, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($S - 2 * $r - 1, $S - 2 * $r - 1, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc(0, $S - 2 * $r - 1, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure() }
    $sky = New-Object System.Drawing.Drawing2D.LinearGradientBrush (P 0 0), (P 0 170), ([System.Drawing.Color]::FromArgb(255, 32, 92, 196)), ([System.Drawing.Color]::FromArgb(255, 150, 205, 245))
    $g.FillPath($sky, $path)
    $g.SetClip($path)

    # 赤い太陽
    $sun = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 230, 40, 45))
    $g.FillEllipse($sun, [float](160 * $k), [float](28 * $k), [float](64 * $k), [float](64 * $k))

    # 富士山と雪
    $fuji = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 44, 62, 120))
    $g.FillPolygon($fuji, [System.Drawing.PointF[]]@((P -10 180), (P 98 70), (P 128 66), (P 158 70), (P 266 180)))
    $snow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $g.FillPolygon($snow, [System.Drawing.PointF[]]@((P 98 70), (P 128 66), (P 158 70), (P 180 92), (P 162 98), (P 148 90), (P 134 100), (P 120 90), (P 104 99), (P 76 92)))

    # 地面
    $grass = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 72, 160, 70))
    $g.FillRectangle($grass, 0, [float](172 * $k), $S, $S)

    # 高速道路（遠近感のある台形）と白線
    $road = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 70, 72, 80))
    $g.FillPolygon($road, [System.Drawing.PointF[]]@((P 116 172), (P 140 172), (P 230 260), (P 26 260)))
    $edge = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([float]([Math]::Max(1, 4 * $k)))
    $g.DrawLine($edge, (P 118 173), (P 40 260))
    $g.DrawLine($edge, (P 138 173), (P 216 260))
    if ($S -ge 32) {
        $dash = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
        foreach ($d in @(@(178, 186, 2.2), @(196, 210, 3.5), @(222, 242, 5))) {
            $y0 = $d[0]; $y1 = $d[1]; $w = $d[2]
            $g.FillPolygon($dash, [System.Drawing.PointF[]]@((P (128 - $w / 2) $y0), (P (128 + $w / 2) $y0), (P (128 + $w) $y1), (P (128 - $w) $y1)))
        }
    }

    # 赤いスポーツカー（後ろ姿）
    if ($S -ge 48) {
        $car = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 210, 30, 40))
        $dark = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 30, 30, 36))
        $glass = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 120, 160, 200))
        $tail = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 210, 80))
        $g.FillRectangle($dark, [float](92 * $k), [float](232 * $k), [float](16 * $k), [float](14 * $k))
        $g.FillRectangle($dark, [float](148 * $k), [float](232 * $k), [float](16 * $k), [float](14 * $k))
        $g.FillPolygon($car, [System.Drawing.PointF[]]@((P 104 196), (P 152 196), (P 166 214), (P 166 238), (P 90 238), (P 90 214)))
        $g.FillPolygon($glass, [System.Drawing.PointF[]]@((P 108 200), (P 148 200), (P 158 213), (P 98 213)))
        $g.FillRectangle($tail, [float](94 * $k), [float](220 * $k), [float](16 * $k), [float](6 * $k))
        $g.FillRectangle($tail, [float](146 * $k), [float](220 * $k), [float](16 * $k), [float](6 * $k))
        $g.FillRectangle($dark, [float](88 * $k), [float](198 * $k), [float](80 * $k), [float](5 * $k))   # スポイラー
    }

    $g.ResetClip()
    $g.Dispose()
    return $bmp
}

# 各サイズを PNG にして .ico にまとめる（Windows Vista 以降は PNG 入りの ico を読める）
# 256 は PNG、それより小さいサイズは昔ながらのビットマップ（DIB）で入れる（どのソフトでも読めるように）
function To-Dib([System.Drawing.Bitmap]$bmp) {
    $s = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)
    $bw.Write([UInt32]40); $bw.Write([Int32]$s); $bw.Write([Int32]($s * 2))
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]0)
    $bw.Write([UInt32]($s * $s * 4 + $maskRow * $s))
    $bw.Write([Int32]0); $bw.Write([Int32]0); $bw.Write([UInt32]0); $bw.Write([UInt32]0)
    for ($y = $s - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $s; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }
    $bw.Write((New-Object byte[] ($maskRow * $s)))
    $bw.Flush()
    return $ms.ToArray()
}

$pngs = @()
foreach ($s in $sizes) {
    $bmp = Draw-Icon $s
    if ($s -eq 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Save($outPng, [System.Drawing.Imaging.ImageFormat]::Png)
        $data = $ms.ToArray()
    }
    else { $data = To-Dib $bmp }
    $bmp.Dispose()
    $pngs += , @($s, $data)
}
$fs = [System.IO.File]::Create($outIco)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $s = $p[0]; $data = $p[1]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$data.Length); $w.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($p in $pngs) { $w.Write([byte[]]$p[1]) }
$w.Close()
# インストーラーの左側の画像（164x314 の BMP）：同じ絵を縦長に切り出して、下に名前を入れる
$outBmp = Join-Path $root "installer\welcome.bmp"
$scene = Draw-Icon 314 $true
$banner = New-Object System.Drawing.Bitmap 164, 314, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
$bg = [System.Drawing.Graphics]::FromImage($banner)
$bg.SmoothingMode = 'AntiAlias'; $bg.TextRenderingHint = 'AntiAliasGridFit'
$bg.DrawImage($scene, (New-Object System.Drawing.Rectangle 0, 0, 164, 314), (New-Object System.Drawing.Rectangle 75, 0, 164, 314), [System.Drawing.GraphicsUnit]::Pixel)
$shade = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(150, 10, 20, 40))
$bg.FillRectangle($shade, 0, 0, 164, 74)
$font = New-Object System.Drawing.Font "Yu Gothic UI", 17, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
$small = New-Object System.Drawing.Font "Yu Gothic UI", 11, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
$white = [System.Drawing.Brushes]::White
$bg.DrawString("日本一周", $font, $white, 12, 12)
$bg.DrawString("ドライブ 3D", $font, $white, 12, 34)
$bg.DrawString("Japan Drive 3D", $small, (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(220, 255, 220, 150))), 13, 56)
$banner.Save($outBmp, [System.Drawing.Imaging.ImageFormat]::Bmp)
$bg.Dispose(); $banner.Dispose(); $scene.Dispose()

Write-Output "icon: $outIco ($((Get-Item $outIco).Length) bytes), $outPng"
