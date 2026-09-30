# 由 Assets/app-icon.png 生成多尺寸 Assets/app.ico
#
# 处理流程：
#   1. 读入源图，按 alpha 通道裁掉四周透明留白（源图图形常常不居中、留白不均）
#   2. 把图形等比放进正方形画布并居中，四周留出统一边距（-Padding）
#   3. 依次渲染 16/24/32/48/64/128/256 七个尺寸；小尺寸先 4 倍超采样再缩小，
#      避免从 512px 一步缩到 16px 时笔画糊掉
#   4. 按 Windows ICO 容器格式写出（各尺寸均为 PNG 压缩，与仓库原图标一致）
#
# 用法（在仓库根目录）：
#   pwsh -File tools/make_icon.ps1
#   pwsh -File tools/make_icon.ps1 -Padding 0.04      # 图形更大、边距更小

param(
    [string]$Source = (Join-Path $PSScriptRoot '..\Assets\app-icon.png'),
    [string]$Output = (Join-Path $PSScriptRoot '..\Assets\app.ico'),
    [int[]]$Sizes = @(16, 24, 32, 48, 64, 128, 256),
    [double]$Padding = 0.07,
    # 小于该尺寸时先 4 倍超采样再缩小
    [int]$SuperSampleBelow = 96
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$Source = [System.IO.Path]::GetFullPath($Source)
$Output = [System.IO.Path]::GetFullPath($Output)
if (-not (Test-Path -LiteralPath $Source)) { throw "找不到源图：$Source" }

# ---------- 读取源图并定位不透明内容 ----------
$stream = [System.IO.File]::OpenRead($Source)
try {
    # BitmapDecoder.Create 而不写死 PngBitmapDecoder：换成 jpg/webp 等格式也能用
    $decoder = [System.Windows.Media.Imaging.BitmapDecoder]::Create(
        $stream,
        [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
        [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
    $frame = $decoder.Frames[0]
}
finally { $stream.Dispose() }

$bgra = New-Object System.Windows.Media.Imaging.FormatConvertedBitmap -ArgumentList @(
    $frame, [System.Windows.Media.PixelFormats]::Bgra32, $null, [double]0)

$width = $bgra.PixelWidth
$height = $bgra.PixelHeight
$stride = $width * 4
$pixels = New-Object byte[] ($stride * $height)
$bgra.CopyPixels($pixels, $stride, 0)

$minX = $width; $minY = $height; $maxX = -1; $maxY = -1
for ($y = 0; $y -lt $height; $y++) {
    $row = $y * $stride
    for ($x = 0; $x -lt $width; $x++) {
        if ($pixels[$row + $x * 4 + 3] -ne 0) {
            if ($x -lt $minX) { $minX = $x }
            if ($x -gt $maxX) { $maxX = $x }
            if ($y -lt $minY) { $minY = $y }
            if ($y -gt $maxY) { $maxY = $y }
        }
    }
}
if ($maxX -lt 0) { throw "源图完全透明：$Source" }

$contentW = $maxX - $minX + 1
$contentH = $maxY - $minY + 1
Write-Host ("源图 {0}x{1}，不透明内容 {2}x{3}（左 {4} 上 {5} 右 {6} 下 {7}），已裁掉留白并居中" -f `
    $width, $height, $contentW, $contentH, $minX, $minY, ($width - 1 - $maxX), ($height - 1 - $maxY))

$cropped = New-Object System.Windows.Media.Imaging.CroppedBitmap -ArgumentList @(
    $bgra, (New-Object System.Windows.Int32Rect -ArgumentList $minX, $minY, $contentW, $contentH))

# ---------- 渲染 ----------

# 计算图形在正方形画布里的落点，取整到像素：
# 小尺寸下如果按小数坐标绘制，图形会被糊在两三个像素行上，16x16 尤其明显。
function Get-IconRect {
    param([int]$Size, [int]$ContentWidth, [int]$ContentHeight, [double]$Margin)

    $inner = $Size * (1.0 - 2.0 * $Margin)
    $scale = [Math]::Min($inner / $ContentWidth, $inner / $ContentHeight)
    $drawW = [Math]::Max(1, [int][Math]::Round($ContentWidth * $scale))
    $drawH = [Math]::Max(1, [int][Math]::Round($ContentHeight * $scale))

    return @(
        [int][Math]::Floor(($Size - $drawW) / 2.0),
        [int][Math]::Floor(($Size - $drawH) / 2.0),
        $drawW,
        $drawH)
}

function New-IconBitmap {
    param(
        [System.Windows.Media.Imaging.BitmapSource]$Image,
        [int]$Size,
        [int]$X,
        [int]$Y,
        [int]$Width,
        [int]$Height
    )

    $visual = New-Object System.Windows.Media.DrawingVisual
    [System.Windows.Media.RenderOptions]::SetBitmapScalingMode(
        $visual, [System.Windows.Media.BitmapScalingMode]::HighQuality)
    $ctx = $visual.RenderOpen()
    $ctx.DrawImage($Image, (New-Object System.Windows.Rect -ArgumentList @(
        [double]$X, [double]$Y, [double]$Width, [double]$Height)))
    $ctx.Close()

    $target = New-Object System.Windows.Media.Imaging.RenderTargetBitmap -ArgumentList @(
        $Size, $Size, [double]96, [double]96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $target.Render($visual)
    return $target
}

function Get-IconPng {
    param([System.Windows.Media.Imaging.BitmapSource]$Bitmap)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($Bitmap))
    $memory = New-Object System.IO.MemoryStream
    $encoder.Save($memory)
    return $memory.ToArray()
}

$entries = @()
foreach ($size in $Sizes) {
    $rect = Get-IconRect -Size $size -ContentWidth $contentW -ContentHeight $contentH -Margin $Padding
    if ($size -lt $SuperSampleBelow) {
        # 先按同一落点的 4 倍尺寸渲染，再整幅缩到目标尺寸：
        # 既拿到 4 倍超采样，落点又正好落在目标像素网格上
        $big = New-IconBitmap -Image $cropped -Size ($size * 4) `
            -X ($rect[0] * 4) -Y ($rect[1] * 4) -Width ($rect[2] * 4) -Height ($rect[3] * 4)
        $bitmap = New-IconBitmap -Image $big -Size $size -X 0 -Y 0 -Width $size -Height $size
    }
    else {
        $bitmap = New-IconBitmap -Image $cropped -Size $size `
            -X $rect[0] -Y $rect[1] -Width $rect[2] -Height $rect[3]
    }
    $png = Get-IconPng -Bitmap $bitmap
    $entries += [pscustomobject]@{ Size = $size; Bytes = $png }
    Write-Host ("  {0,3}x{0,-3} -> 图形 {1}x{2} 落在 ({3},{4})，{5,7} 字节" -f `
        $size, $rect[2], $rect[3], $rect[0], $rect[1], $png.Length)
}

# ---------- 写出 ICO ----------
$memory = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter -ArgumentList $memory
$writer.Write([uint16]0)                  # reserved
$writer.Write([uint16]1)                  # type: icon
$writer.Write([uint16]$entries.Count)

$offset = 6 + 16 * $entries.Count
foreach ($entry in $entries) {
    $dimension = if ($entry.Size -ge 256) { 0 } else { $entry.Size }   # 0 表示 256
    $writer.Write([byte]$dimension)
    $writer.Write([byte]$dimension)
    $writer.Write([byte]0)                # 调色板数量
    $writer.Write([byte]0)                # reserved
    $writer.Write([uint16]1)              # color planes
    $writer.Write([uint16]32)             # bits per pixel
    $writer.Write([uint32]$entry.Bytes.Length)
    $writer.Write([uint32]$offset)
    $offset += $entry.Bytes.Length
}
foreach ($entry in $entries) {
    # 注意：必须用 Write(byte[], int, int) 三参数重载。
    # BinaryWriter.Write(byte[]) 在 PowerShell 里会被重载解析到 Write(byte)，
    # 结果每个尺寸只写入 1 个字节，生成出来的 ico 是坏的。
    $bytes = [byte[]]$entry.Bytes
    $writer.Write($bytes, 0, $bytes.Length)
}
$writer.Flush()

$data = $memory.ToArray()
[System.IO.File]::WriteAllBytes($Output, $data)

# ---------- 自检：确认目录与数据都写全了 ----------
$expected = 6 + 16 * $entries.Count + ($entries | Measure-Object -Property { $_.Bytes.Length } -Sum).Sum
if ($data.Length -ne $expected) {
    throw "生成的 ico 长度异常：期望 $expected 字节，实际 $($data.Length) 字节"
}
for ($i = 0; $i -lt $entries.Count; $i++) {
    $entryOffset = [System.BitConverter]::ToUInt32($data, 6 + $i * 16 + 12)
    if ($data[$entryOffset] -ne 0x89 -or $data[$entryOffset + 1] -ne 0x50) {
        throw "第 $($i + 1) 个尺寸的数据不是 PNG（偏移 $entryOffset）"
    }
}

Write-Host ("已写出 {0}（{1} 个尺寸，{2} 字节）" -f $Output, $entries.Count, $data.Length)
