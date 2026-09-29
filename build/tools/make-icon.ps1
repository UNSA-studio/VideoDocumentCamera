<#
.SYNOPSIS
    生成「视频展台」应用程序图标（多尺寸 ICO + 标题栏 PNG）。

.DESCRIPTION
    ⚠ 图标【已经入库】，正常情况下**不需要**运行本脚本。

    仓库中可直接使用的图标：
        src/VideoPresenter.App/Assets/VideoPresenter.ico   多尺寸 ICO（16~256）
        src/VideoPresenter.App/Assets/AppIcon.png          标题栏用 64×64 PNG
        src/VideoPresenter.Launcher/Assets/VideoPresenter.ico

    什么时候才需要它：
        · 更换品牌色 / 重新设计图标；
        · 或者你更喜欢"构建时生成图标"的纯文本仓库策略。

    用法：
        ./build/tools/make-icon.ps1 -Force      # 覆盖现有图标
        然后用设计稿覆盖 Assets 目录即可。

.NOTES
    开发商：UNSA Studio
    （另一份等价的 Python 实现见 tools/make_icon.py，用于非 Windows 环境。）
#>

[CmdletBinding()]
param(
    [string[]]$Targets = @(
        (Join-Path $PSScriptRoot '..\src\VideoPresenter.App\Assets\VideoPresenter.ico'),
        (Join-Path $PSScriptRoot '..\src\VideoPresenter.Launcher\Assets\VideoPresenter.ico')
    ),
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

function New-IconBitmap {
    param([int]$Size, [int]$Padding)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)

    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.Clear([System.Drawing.Color]::Transparent)

        # ── 底：Win11 强调色渐变（#0067C0 → #4CC2FF）─────────────
        $rect = New-Object System.Drawing.Rectangle(0, 0, $Size, $Size)
        $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            $rect,
            [System.Drawing.Color]::FromArgb(255, 0, 103, 192),
            [System.Drawing.Color]::FromArgb(255, 76, 194, 255),
            45.0)
        $radius = [Math]::Max(2, [int]($Size * 0.20))
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $d = $radius * 2
        $path.AddArc(0, 0, $d, $d, 180, 90)
        $path.AddArc($Size - $d - 1, 0, $d, $d, 270, 90)
        $path.AddArc($Size - $d - 1, $Size - $d - 1, $d, $d, 0, 90)
        $path.AddArc(0, $Size - $d - 1, $d, $d, 90, 90)
        $path.CloseFigure()
        $g.FillPath($brush, $path)

        # ── 前：白色摄像机剪影（机身 + 镜头 + 取景框）───────────────
        $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
        $w = $Size
        $bodyX = [int]($w * 0.20)
        $bodyY = [int]($w * 0.34)
        $bodyW = [int]($w * 0.46)
        $bodyH = [int]($w * 0.32)
        $g.FillRectangle($white, $bodyX, $bodyY, $bodyW, $bodyH)

        # 上方的取景/闪光条
        $g.FillRectangle($white, [int]($w * 0.28), [int]($w * 0.26), [int]($w * 0.20), [int]($w * 0.08))

        # 右侧镜头（梯形）
        $lens = New-Object System.Drawing.Drawing2D.GraphicsPath
        $lx = $bodyX + $bodyW
        $ly = $bodyY
        $lh = $bodyH
        $lens.AddPolygon(@(
            (New-Object System.Drawing.Point($lx, $ly + [int]($lh * 0.18))),
            (New-Object System.Drawing.Point([int]($w * 0.86), $ly)),
            (New-Object System.Drawing.Point([int]($w * 0.86), $ly + $lh)),
            (New-Object System.Drawing.Point($lx, $ly + [int]($lh * 0.82)))
        ))
        $g.FillPath($white, $lens)

        $white.Dispose()
        $brush.Dispose()
        $path.Dispose()
        $lens.Dispose()
    }
    finally {
        $g.Dispose()
    }

    return $bmp
}

function Write-IcoFile {
    param([string]$Path, [int[]]$Sizes)

    $entries = @()
    foreach ($s in $Sizes) {
        $bmp = New-IconBitmap -Size $s -Padding 0
        $ms = New-Object System.IO.MemoryStream
        try {
            $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
            $entries += [pscustomobject]@{ Size = $s; Data = $ms.ToArray() }
        }
        finally {
            $ms.Dispose()
            $bmp.Dispose()
        }
    }

    $dir = Split-Path -Parent $Path
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

    $fs = [System.IO.File]::Create($Path)
    $bw = New-Object System.IO.BinaryWriter($fs)
    try {
        # ICONDIR
        $bw.Write([UInt16]0)                  # reserved
        $bw.Write([UInt16]1)                  # type = icon
        $bw.Write([UInt16]$entries.Count)     # count

        $offset = 6 + (16 * $entries.Count)

        # ICONDIRENTRY × N
        foreach ($e in $entries) {
            $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
            $bw.Write([Byte]$dim)
            $bw.Write([Byte]$dim)
            $bw.Write([Byte]0)                # color count
            $bw.Write([Byte]0)                # reserved
            $bw.Write([UInt16]1)              # planes
            $bw.Write([UInt16]32)             # bit count
            $bw.Write([UInt32]$e.Data.Length)
            $bw.Write([UInt32]$offset)
            $offset += $e.Data.Length
        }

        # PNG 数据
        foreach ($e in $entries) { $bw.Write($e.Data) }
    }
    finally {
        $bw.Dispose()
        $fs.Dispose()
    }

    Write-Host ("  已生成 {0} （{1} 个尺寸）" -f (Resolve-Path $Path).Path, $entries.Count)
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)

foreach ($target in $Targets) {
    $full = [System.IO.Path]::GetFullPath($target)
    if ((Test-Path $full) -and -not $Force) {
        Write-Host ("  已存在，跳过：{0}" -f $full)
        continue
    }
    Write-IcoFile -Path $full -Sizes $sizes
}

Write-Host '图标生成完成。'