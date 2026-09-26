# 生成中性应用图标：蓝色圆角方块 + 白色"切"字，包装成 PNG-in-ICO
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$size = 256
$fill = (New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(0x2B, 0x6C, 0xDE)))
$typeface = New-Object System.Windows.Media.Typeface -ArgumentList "Microsoft YaHei UI"
$text = New-Object System.Windows.Media.FormattedText -ArgumentList @(
    "切",
    [System.Globalization.CultureInfo]::CurrentCulture,
    [System.Windows.FlowDirection]::LeftToRight,
    $typeface,
    [double]150,
    [System.Windows.Media.Brushes]::White,
    [double]1.0)
$text.TextAlignment = [System.Windows.TextAlignment]::Center

$dv = New-Object System.Windows.Media.DrawingVisual
$ctx = $dv.RenderOpen()
$ctx.DrawRectangle($fill, $null, (New-Object System.Windows.Rect -ArgumentList 0, 0, $size, $size))
$ctx.DrawText($text, (New-Object System.Windows.Point -ArgumentList ([double]($size / 2)), ((($size - $text.Height) / 2) - 6)))
$ctx.Close()

$pf = [System.Windows.Media.PixelFormats]::Pbgra32
$rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap -ArgumentList @($size, $size, [double]96, [double]96, $pf)
$rtb.Render($dv)
$enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
$enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rtb))
$ms = New-Object System.IO.MemoryStream
$enc.Save($ms)
$png = $ms.ToArray()

$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter -ArgumentList $out
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]1)
$bw.Write([byte]0); $bw.Write([byte]0)
$bw.Write([byte]0); $bw.Write([byte]0)
$bw.Write([uint16]1); $bw.Write([uint16]32)
$bw.Write([uint32]$png.Length); $bw.Write([uint32]22)
$bw.Write($png)
$bw.Flush()
[System.IO.File]::WriteAllBytes("C:\Users\wkrs14\Desktop\arkswitch\ArkSwitch\Assets\app.ico", $out.ToArray())
Write-Host "icon written:" $out.Length "bytes"
