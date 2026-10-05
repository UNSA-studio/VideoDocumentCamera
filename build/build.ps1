<#
.SYNOPSIS
    「视频展台」构建 / 出包脚本（分阶段）。

.DESCRIPTION
    产出物（dist/）：
        app/                                便携目录（可直接拷走运行）
            VideoPresenter.Launcher.exe     ← 启动器（原生，1 秒内出界面）
            VideoPresenter.exe             ← 主程序（WinUI 3，自包含）
            …（Windows App SDK 运行时）
        视频展台-1.0.0-x64.msi              ← MSI 安装包（WiX v4）
        视频展台-1.0.0-x64-setup.exe        ← EXE 安装包（Inno Setup 6）

.PARAMETER Stage
    分阶段执行。CI 中拆成多个 step 可以精确定位失败点：
        All      完整流程（默认）
        Launcher 仅编译 C++ 启动器
        App      仅发布 WinUI 3 主程序
        Msi      仅打包 MSI（WiX v4）
        Exe      仅打包 EXE（Inno Setup 6）

.PARAMETER Configuration
    Debug 或 Release，默认 Release。

.PARAMETER Sign
    提供 PFX 路径后对产物做数字签名（主体：UNSA Studio）。

.EXAMPLE
    ./build.ps1 -Configuration Release
    ./build.ps1 -Stage App

.NOTES
    开发商：UNSA Studio
#>

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('All', 'Launcher', 'App', 'Msi', 'Exe')]
    [string]$Stage = 'All',

    [string]$Sign = '',
    [string]$SignPassword = '',
    [switch]$SkipIcon,
    [switch]$SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── 让 CI 能"看见"失败原因 ──────────────────────────────────────────────────
#  GitHub 的失败注解（annotations）只会捕获编译器 / MSBuild 的结构化输出；
#  PowerShell 自己 throw 出来的消息不会变成注解 —— 结果是"只看到红叉，看不到原因"。
#  这里把所有终止性错误手动转成 ::error:: 工作流命令。它会成为注解，
#  因此可以直接通过 GitHub REST API 读取（无需日志下载权限）。
trap {
    $msg = ($_.Exception.Message -replace "`r?`n", ' | ')
    Write-Host ('::error::[build.ps1] ' + $msg)
    if ($_.InvocationInfo) {
        Write-Host ('::error::[位置] 行 ' + $_.InvocationInfo.ScriptLineNumber + ' : ' + $_.InvocationInfo.Line.Trim())
    }
    exit 1
}

# ─────────────────────────── 路径 ───────────────────────────

$root         = Split-Path -Parent $PSScriptRoot
$srcDir       = Join-Path $root 'src'
$distDir      = Join-Path $root 'dist'
$appOutDir    = Join-Path $distDir 'app'
$buildOut     = Join-Path $root 'build\out'

$appProj      = Join-Path $srcDir 'VideoPresenter.App\VideoPresenter.App.csproj'
$launcherVcxp = Join-Path $srcDir 'VideoPresenter.Launcher\Launcher.vcxproj'
$msiProj      = Join-Path $root 'installer\wix\VideoPresenter.Msi.wixproj'
$issFile      = Join-Path $root 'installer\exe\setup.iss'
$iconScript   = Join-Path $PSScriptRoot 'tools\make-icon.ps1'

$version = '1.0.0'

# 本阶段需要做哪些事
$doIcon     = $Stage -eq 'All'
$doLauncher = $Stage -in @('All', 'Launcher')
$doApp      = $Stage -in @('All', 'App')
$doMsi      = $Stage -in @('All', 'Msi')
$doExe      = $Stage -in @('All', 'Exe')

# ─────────────────────────── 工具函数 ───────────────────────────

function Write-Step([string]$text) {
    Write-Host ''
    Write-Host ('=' * 72)
    Write-Host ('  ' + $text)
    Write-Host ('=' * 72)
}

function Assert-Command([string]$name, [string]$hint) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw "找不到命令 $name。$hint"
    }
}

# ── UTF-8 文本 → RTF ────────────────────────────────────────────────────────
#  WiX 的许可协议页（WixUILicenseRtf）只接受 RTF 格式，而 RTF 是 ASCII 方言：
#  非 ASCII 字符必须写成 \u<十进制码点>? 的转义形式，否则中文会乱码。
#  这里从 installer/EULA.txt（UTF-8，便于维护）自动转换，
#  供 WiX 使用；Inno Setup 则直接读取 .txt，无需转换。
function ConvertTo-Rtf {
    param(
        [Parameter(Mandatory = $true)][string]$TextPath,
        [Parameter(Mandatory = $true)][string]$RtfPath
    )

    if (-not (Test-Path $TextPath)) {
        Write-Warning "  许可协议源文件不存在，跳过 RTF 生成：$TextPath"
        return $false
    }

    $text = [IO.File]::ReadAllText($TextPath, [Text.Encoding]::UTF8)

    $sb = New-Object System.Text.StringBuilder
    # \ansicpg936（GBK 代码页）+ 中文字体；\uc1 表示每个 \uN 后跟 1 个替代字符
    [void]$sb.Append('{\rtf1\ansi\ansicpg936\deff0{\fonttbl{\f0\fnil\fcharset134 Microsoft YaHei;}}')
    [void]$sb.Append("`r`n")
    [void]$sb.Append('\viewkind4\uc1\pard\f0\fs18')
    [void]$sb.Append("`r`n")

    foreach ($ch in $text.ToCharArray()) {
        $code = [int]$ch

        if ($ch -eq "`r") { continue }
        if ($ch -eq "`n") { [void]$sb.Append("\par`r`n"); continue }
        if ($ch -eq '\') { [void]$sb.Append('\\'); continue }
        if ($ch -eq '{') { [void]$sb.Append('\{'); continue }
        if ($ch -eq '}') { [void]$sb.Append('\}'); continue }

        if ($code -lt 128) {
            [void]$sb.Append($ch)
        }
        elseif ($code -le 65535) {
            [void]$sb.Append('\u' + $code + '?')
        }
    }

    [void]$sb.Append('}')

    [IO.File]::WriteAllText($RtfPath, $sb.ToString(), [Text.Encoding]::ASCII)
    return $true
}

function Find-MSBuild {
    $candidate = $null

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $candidate = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild `
            -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    }

    if (-not $candidate) {
        $cmd = Get-Command msbuild -ErrorAction SilentlyContinue
        if ($cmd) { $candidate = $cmd.Source }
    }

    return $candidate
}

Write-Host ''
Write-Host "视频展台 · 构建 (Stage=$Stage, Configuration=$Configuration)" -ForegroundColor Cyan
Write-Host "  工程根目录 : $root"
Write-Host "  输出目录   : $distDir"

# ═══════════════════════ 阶段 0：图标 ═══════════════════════

if ($doIcon -and -not $SkipIcon) {
    Write-Step '生成应用程序图标'
    & $iconScript
}
elseif ($SkipIcon -or $doIcon) {
    Write-Host ''
    Write-Host '  图标：沿用已入库的 Assets 文件' -ForegroundColor DarkGray
}

# ═══════════════════════ 阶段 1：编译启动器 ═══════════════════════

if ($doLauncher) {
    Write-Step '① 编译启动器（原生 Win32 C++）'

    # ── 定位 Visual Studio（必须含 MSVC v143 C++ 工具集）─────────────────────
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) {
        throw '找不到 vswhere.exe（需要安装 Visual Studio 2017 及以上）。'
    }

    $vsInstall = & $vswhere -latest -products * `
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
        -property installationPath | Select-Object -First 1

    if (-not $vsInstall) {
        throw '未找到包含「MSVC v143 - VS 2022 C++ x64/x86 生成工具」的 Visual Studio 安装。'
    }
    $vsInstall = $vsInstall.Trim()

    $devCmd      = Join-Path $vsInstall 'Common7\Tools\VsDevCmd.bat'
    $msbuildFull = Join-Path $vsInstall 'MSBuild\Current\Bin\MSBuild.exe'

    Write-Host "  VS 安装目录 : $vsInstall"
    Write-Host "  VsDevCmd    : $devCmd  (存在: $(Test-Path $devCmd))"
    Write-Host "  MSBuild     : $msbuildFull  (存在: $(Test-Path $msbuildFull))"
    Write-Host "  项目        : $launcherVcxp"
    Write-Host ''

    if (-not (Test-Path $devCmd)) {
        throw "找不到 VsDevCmd.bat：$devCmd"
    }
    if (-not (Test-Path $msbuildFull)) {
        throw "找不到 MSBuild.exe：$msbuildFull"
    }

    New-Item -ItemType Directory -Path $buildOut -Force | Out-Null

    # ── 在 VS 开发者环境中构建 ────────────────────────────────────────────
    #  C++ 项目依赖完整的 INCLUDE / LIB / PATH（cl.exe、rc.exe、link.exe 都靠它定位）。
    #  仅在普通 PowerShell 里直接调 MSBuild 时，它可能在 PrepareForBuild 之后
    #  静默终止（CI 上实测输出到 InitializeBuildStatus 就断了）。
    $inner = 'call "{0}" -arch=x64 -host_arch=x64 >nul 2>&1 && "{1}" "{2}" /p:Configuration={3} /p:Platform=x64 /nologo /v:minimal' -f `
        $devCmd, $msbuildFull, $launcherVcxp, $Configuration

    Write-Host "  cmd /c $inner"
    Write-Host ''

    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $output = @()
    $exitCode = 0
    try {
        $output = & cmd.exe /c $inner 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $prevEap
    }

    # 用 warning 级别输出诊断（同样会变成注解，可通过 API 读取）
    Write-Host ('::warning::[诊断] MSBuild 退出码 = ' + $exitCode + '，输出行数 = ' + @($output).Count)
    Write-Host ''

    if ($exitCode -ne 0) {
        Write-Host '  ── MSBuild 输出尾部 ──' -ForegroundColor Yellow
        foreach ($line in (@($output) | Select-Object -Last 60)) {
            $text = ($line | Out-String).TrimEnd()
            if ($text -match '\S') { Write-Host ('::error::' + $text) }
        }
        throw "启动器编译失败（MSBuild exit code = $exitCode）。"
    }

    # 产物查找：先按预期路径，找不到则递归搜索并归位
    $expected = Join-Path $buildOut "x64\$Configuration\VideoPresenter.Launcher.exe"
    $launcherExe = $null

    if (Test-Path $expected) {
        $launcherExe = $expected
    }
    else {
        $found = Get-ChildItem $buildOut -Recurse -Filter 'VideoPresenter.Launcher.exe' -File -ErrorAction SilentlyContinue |
                 Select-Object -First 1
        if ($found) {
            Write-Host "  产物路径与预期不同，已自动定位：$($found.FullName)" -ForegroundColor Yellow
            New-Item -ItemType Directory -Path (Split-Path $expected) -Force | Out-Null
            Copy-Item $found.FullName $expected -Force
            $launcherExe = $expected
        }
    }

    if ($launcherExe) {
        $size = [Math]::Round((Get-Item $launcherExe).Length / 1KB, 1)
        Write-Host "  启动器编译完成：$launcherExe （$size KB）" -ForegroundColor Green
    }
    else {
        Write-Host '::error::启动器编译成功，但未找到输出文件'
        if (Test-Path $buildOut) {
            Get-ChildItem $buildOut -Recurse -File | Select-Object -First 40 | ForEach-Object {
                Write-Host ('::error::  build/out: ' + $_.FullName)
            }
        }
        throw "编译成功但未找到 VideoPresenter.Launcher.exe（预期：$expected）"
    }
}

# ═══════════════════════ 阶段 1.5：XAML 资源名校验 ═══════════════════════
#
# 背景（这是一个真实踩过的坑，务必保留）：
#
#   XAML 里的 {ThemeResource X} 是【运行时】才解析的，编译期完全不报错。
#   如果写了 UWP / WinUI 2 时代的资源名，编译照样成功、打包照样成功，
#   但程序一启动就抛 XamlParseException 直接崩 —— 而 CI 全程显示绿色。
#
#   这一次就是栽在 `FlyoutBackgroundBrush` 上：主程序在 MainWindow..ctor()
#   抛 "Cannot find a Resource with the Name/Key FlyoutBackgroundBrush"，
#   用户看到的是一句冷冰冰的启动失败。
#
#   所以在打包前做一次静态拦截，把这类问题挡在上传之前。
#
Write-Step '①·5 校验 XAML 资源名（防 WinUI 2 残留）'

$appSrcDir = Join-Path (Split-Path $PSScriptRoot -Parent) 'src'

# UWP / WinUI 2 时代独有的资源名 —— 在 WinUI 3 中【不存在】
$bannedResourceNames = @(
    'FlyoutBackgroundBrush'
    'ApplicationPageBackgroundThemeBrush'
    'SystemControlBackgroundAccentBrush'
    'SystemControlBackgroundAltHighBrush'
    'SystemControlBackgroundBaseLowBrush'
    'SystemControlBackgroundChromeMediumLowBrush'
    'SystemControlBackgroundListLowBrush'
    'SystemControlDisabledBaseMediumLowBrush'
    'SystemControlFocusVisualPrimaryBrush'
    'SystemControlFocusVisualSecondaryBrush'
    'SystemControlForegroundAccentBrush'
    'SystemControlForegroundBaseHighBrush'
    'SystemControlForegroundBaseMediumBrush'
    'SystemControlForegroundBaseMediumHighBrush'
    'SystemControlForegroundChromeWhiteBrush'
    'SystemControlHighlightAccentBrush'
    'SystemControlHighlightAltAccentBrush'
    'SystemControlHighlightAltBaseHighBrush'
    'SystemControlHighlightBaseHighBrush'
    'SystemControlHighlightListAccentHighBrush'
    'SystemControlHighlightListAccentLowBrush'
    'SystemControlHighlightListAccentMediumBrush'
    'SystemControlHighlightListLowBrush'
    'SystemControlHighlightTransparentBrush'
    'SystemControlHyperlinkTextBrush'
    'SystemControlPageTextBaseHighBrush'
    'SystemControlPageTextBaseMediumBrush'
    'SystemControlRevealFocusVisualBrush'
    'SystemControlTransientBorderBrush'
    'SystemControlTransparentBrush'
    'ChromeWhiteBrush'
    'ChromeGrayBrush'
    'BaseHighBrush'
    'BaseLowBrush'
    'BaseMediumBrush'
    'BaseMediumHighBrush'
    'AltHighBrush'
    'AltMediumHighBrush'
    'AltMediumLowBrush'
)

# 注意两个坑：
#   ① -Filter '*.xaml' 在 Windows 上会把名为 Microsoft.UI.Xaml 的【目录】也匹配进来，
#      必须加 -File 只取文件；
#   ② bin / obj 里是编译产物，不该参与校验。
$xamlFiles = @(
    Get-ChildItem -Path $appSrcDir -Recurse -Filter '*.xaml' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
)
$bannedHits = New-Object System.Collections.Generic.List[string]

foreach ($xaml in $xamlFiles)
{
    $text = Get-Content $xaml.FullName -Raw

    foreach ($banned in $bannedResourceNames)
    {
        if ($text -match [regex]::Escape('{ThemeResource ' + $banned + '}'))
        {
            $bannedHits.Add("$($xaml.Name)  →  {ThemeResource $banned}")
        }
    }
}

if ($bannedHits.Count -gt 0)
{
    Write-Host ''
    Write-Host '✗ XAML 中出现了 WinUI 3 不存在的资源名：' -ForegroundColor Red
    foreach ($hit in $bannedHits) { Write-Host "    $hit" -ForegroundColor Red }
    Write-Host ''
    Write-Host '  这类引用【编译期不报错】，但运行时会让程序在启动瞬间崩溃。' -ForegroundColor Yellow
    Write-Host '  请改用 WinUI 3 标准资源，例如：' -ForegroundColor Yellow
    Write-Host '    TextFillColorPrimaryBrush / SecondaryBrush / TertiaryBrush' -ForegroundColor Yellow
    Write-Host '    ControlFillColorDefaultBrush / SecondaryBrush' -ForegroundColor Yellow
    Write-Host '    LayerFillColorDefaultBrush / CardBackgroundFillColorDefaultBrush' -ForegroundColor Yellow
    Write-Host '    SolidBackgroundFillColorBaseBrush / DividerStrokeColorDefaultBrush' -ForegroundColor Yellow
    throw 'XAML 资源名校验失败（见上）。'
}

Write-Host "✓ XAML 资源名校验通过（检查了 $($xamlFiles.Count) 个文件，无 WinUI 2 残留）" -ForegroundColor Green

# ═══════════════════════ 阶段 2：发布主程序 ═══════════════════════

if ($doApp) {
    Write-Step '② 发布主程序（WinUI 3 / 自包含 / ReadyToRun）'

    Assert-Command 'dotnet' '请安装 .NET 8 SDK（https://dot.net）'

    if (Test-Path $appOutDir) { Remove-Item $appOutDir -Recurse -Force }
    New-Item -ItemType Directory -Path $appOutDir -Force | Out-Null
    Write-Host "  输出：$appOutDir"
    Write-Host ''

    # 捕获完整输出：失败可能发生在编译之后的阶段（XAML 编译 / PRI / ReadyToRun /
    # 自包含打包），这些工具的输出不会自动变成 GitHub 注解。
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $publishOutput = @()
    $publishExit = 0
    try {
        $publishOutput = & dotnet publish $appProj `
            -c $Configuration `
            -r win-x64 `
            --self-contained true `
            -o $appOutDir `
            -p:Platform=x64 `
            -p:Version=$version `
            -p:PublishReadyToRun=true `
            -p:WindowsPackageType=None `
            -p:WindowsAppSDKSelfContained=true `
            -p:DebugType=none `
            -p:GenerateFullPaths=true `
            -v:n `
            --nologo 2>&1
        $publishExit = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $prevEap
    }

    Write-Host ('::warning::[诊断] dotnet publish 退出码 = ' + $publishExit + '，输出行数 = ' + @($publishOutput).Count)

    if ($publishExit -ne 0) {
        Write-Host '  ── dotnet publish 完整输出 ──' -ForegroundColor Yellow
        $all = @($publishOutput)
        Write-Host ('  （共 ' + $all.Count + ' 行）') -ForegroundColor Yellow

        # 错误行优先（XamlCompiler / C# 编译错误都带 "error"）
        foreach ($line in $all) {
            $text = ($line | Out-String).TrimEnd()
            if ($text -match 'error|Error|错误') { Write-Host ('::error::' + $text) }
        }

        # 再打尾部，保证上下文完整
        foreach ($line in ($all | Select-Object -Last 60)) {
            $text = ($line | Out-String).TrimEnd()
            if ($text -match '\S') { Write-Host $text }
        }

        throw "主程序发布失败（dotnet exit code = $publishExit）。"
    }

    # 启动器必须与主程序同目录（启动器按"同目录下的 VideoPresenter.exe"定位主程序）
    $launcherBuilt = Join-Path $buildOut "x64\$Configuration\VideoPresenter.Launcher.exe"
    if (Test-Path $launcherBuilt) {
        Copy-Item $launcherBuilt $appOutDir -Force
        Write-Host '  启动器已复制到发布目录。' -ForegroundColor Green
    }
    elseif (Test-Path (Join-Path $appOutDir 'VideoPresenter.Launcher.exe')) {
        Write-Host '  启动器已在发布目录中（来自上一次构建）。'
    }
    else {
        Write-Warning '  发布目录中没有启动器！桌面快捷方式将无法工作。请先执行 -Stage Launcher。'
    }

    # ── 裁剪多语言资源 ──────────────────────────────────────────────────
    #  self-contained 发布会把 Windows App SDK 的全部语言资源（.mui）带进来，
    #  本应用界面只有中文与英文，其余语言目录可直接删除：
    #  既显著缩小安装包，也减少 MSI 的组件数量。
    $keepLangs = @('zh-Hans', 'zh-CN', 'zh-Hant', 'en', 'en-US', 'en-GB')
    $langDirs = Get-ChildItem $appOutDir -Directory | Where-Object {
        $_.Name -match '^[A-Za-z]{2}(-[A-Za-z]{2,4})*$' -and $keepLangs -notcontains $_.Name
    }
    if ($langDirs) {
        $removed = 0
        foreach ($d in $langDirs) {
            Remove-Item $d.FullName -Recurse -Force -ErrorAction SilentlyContinue
            $removed++
        }
        Write-Host ("  已裁剪 {0} 个非中英语言资源目录" -f $removed)
    }

    $files = Get-ChildItem $appOutDir -Recurse -File
    $sizeMb = [Math]::Round(($files | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
    Write-Host ''
    Write-Host "  发布完成：$($files.Count) 个文件，$sizeMb MB" -ForegroundColor Green
}

# ═══════════════════════ 阶段 3：数字签名 ═══════════════════════

if ($doApp -and -not [string]::IsNullOrWhiteSpace($Sign)) {
    Write-Step '③ 数字签名（主体：UNSA Studio）'
    Assert-Command 'signtool' '请安装 Windows SDK（含 SignTool）。'

    foreach ($t in (Get-ChildItem $appOutDir -Filter '*.exe' -File)) {
        & signtool sign /fd SHA256 /f $Sign /p $SignPassword `
            /tr http://timestamp.digicert.com /td SHA256 $t.FullName
        if ($LASTEXITCODE -ne 0) { throw "签名失败：$($t.Name)" }
        Write-Host "  已签名 $($t.Name)"
    }
}

# ═══════════════════════ 阶段 4：打包 MSI ═══════════════════════

if ($doMsi) {
    Write-Step '④ 打包 MSI（WiX v4）'

    if (-not (Test-Path $appOutDir)) {
        throw "发布目录不存在：$appOutDir。请先执行 -Stage App。"
    }

    Assert-Command 'dotnet' 'WiX v4 通过 dotnet 构建，请安装 .NET SDK。'

    # 末尾不带反斜杠，避免 PowerShell / MSBuild 的引号歧义
    $publishDir = $appOutDir.TrimEnd('\') + '\'

    # ── 生成 WiX 文件清单（含完整目录树）────────────────────────────────
    #  WiX 的 <ComponentGroup> 不接受 <Files> 收割元素，因此自己扫描生成。
    #
    #  这里踩过两个坑，都已修掉：
    #   ① Guid="*" 会为「不同目录下的同名文件」算出相同 GUID
    #       → 改为基于相对路径的确定性 GUID。
    #   ② 必须还原完整的子目录结构，并让每个 Component 指向正确的 Directory。
    #      否则所有文件都会被当作装在 INSTALLFOLDER 根下，
    #      各语言目录里的同名 .mui 会撞车，报：
    #          error WIX0204: ICE30: The target file 'xxx.mui' is installed in ...
    #          by two different components
    #      而且安装后目录结构会全部塌平。
    # ── 生成 RTF 版许可协议 ─────────────────────────────────────────────
    #  WiX 的许可页只接受 RTF；源文件维护在 installer/EULA.txt（UTF-8）。
    $installerDir = Split-Path $msiProj            # installer/wix  → installer
    $installerDir = Split-Path $installerDir
    $eulaTxt = Join-Path $installerDir 'EULA.txt'
    $eulaRtf = Join-Path $installerDir 'EULA.rtf'
    if (ConvertTo-Rtf -TextPath $eulaTxt -RtfPath $eulaRtf) {
        Write-Host "  已生成许可协议：$eulaRtf"
    }

    $generatedWxs = Join-Path (Split-Path $msiProj) 'GeneratedFiles.wxs'
    $allFiles = Get-ChildItem $appOutDir -Recurse -File
    $rootLen = $appOutDir.TrimEnd('\').Length + 1

    # 目录树：相对目录路径 → 生成的 Directory Id
    $dirIds = @{}
    $dirIds[''] = 'INSTALLFOLDER'
    $childMap = @{}
    $childMap[''] = New-Object System.Collections.ArrayList
    $dirCounter = 0

    $entries = @()

    foreach ($f in $allFiles) {
        $rel = $f.FullName.Substring($rootLen)
        $parts = $rel -split '\\'

        $relDir = ''
        if ($parts.Length -gt 1) {
            $relDir = ($parts[0..($parts.Length - 2)] -join '\')
        }

        # 建立目录链（保证父目录先注册）
        $acc = ''
        for ($k = 0; $k -lt ($parts.Length - 1); $k++) {
            $parent = $acc
            if ($acc -eq '') { $acc = $parts[$k] } else { $acc = $acc + '\' + $parts[$k] }

            if (-not $dirIds.ContainsKey($acc)) {
                $dirCounter++
                $dirIds[$acc] = ('dir{0:D4}' -f $dirCounter)
                if (-not $childMap.ContainsKey($parent)) {
                    $childMap[$parent] = New-Object System.Collections.ArrayList
                }
                [void]$childMap[$parent].Add($acc)
            }
        }

        $key = 'VideoPresenter/' + $rel.Replace('\', '/').ToLowerInvariant()
        $hash = [System.Security.Cryptography.MD5]::HashData([Text.Encoding]::UTF8.GetBytes($key))
        $guid = (New-Object Guid -ArgumentList (,$hash)).ToString('B').ToUpper()

        $entries += [pscustomobject]@{
            Src = $f.FullName
            Dir = $dirIds[$relDir]
            Guid = $guid
        }
    }

    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
    [void]$sb.AppendLine('<!-- 本文件由 build.ps1 自动生成（扫描 dist/app），请勿手工编辑 -->')
    [void]$sb.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')

    # ① 目录树
    function Write-WixDirectoryNodes {
        param([string]$ParentRel, [int]$Indent)

        if (-not $childMap.ContainsKey($ParentRel)) { return }

        $pad = ' ' * $Indent
        foreach ($childRel in ($childMap[$ParentRel] | Sort-Object)) {
            $name = $childRel.Substring($childRel.LastIndexOf('\') + 1)
            [void]$sb.AppendLine(('{0}<Directory Id="{1}" Name="{2}">' -f $pad, $dirIds[$childRel], $name))
            Write-WixDirectoryNodes -ParentRel $childRel -Indent ($Indent + 2)
            [void]$sb.AppendLine(('{0}</Directory>' -f $pad))
        }
    }

    [void]$sb.AppendLine('  <Fragment>')
    [void]$sb.AppendLine('    <DirectoryRef Id="INSTALLFOLDER">')
    Write-WixDirectoryNodes -ParentRel '' -Indent 6
    [void]$sb.AppendLine('    </DirectoryRef>')
    [void]$sb.AppendLine('  </Fragment>')

    # ② 文件组件
    [void]$sb.AppendLine('  <Fragment>')
    [void]$sb.AppendLine('    <ComponentGroup Id="AppFilesGroup">')

    $i = 0
    foreach ($e in $entries) {
        $i++
        $id = 'cmp{0:D4}' -f $i
        $fid = 'fil{0:D4}' -f $i
        $srcEsc = [System.Security.SecurityElement]::Escape($e.Src)

        [void]$sb.AppendLine(('      <Component Id="{0}" Directory="{1}" Guid="{2}">' -f $id, $e.Dir, $e.Guid))
        [void]$sb.AppendLine(('        <File Id="{0}" Source="{1}" KeyPath="yes" />' -f $fid, $srcEsc))
        [void]$sb.AppendLine('      </Component>')
    }

    [void]$sb.AppendLine('    </ComponentGroup>')
    [void]$sb.AppendLine('  </Fragment>')
    [void]$sb.AppendLine('</Wix>')

    [IO.File]::WriteAllText($generatedWxs, $sb.ToString(), (New-Object System.Text.UTF8Encoding $false))
    Write-Host "  已生成 WiX 文件清单：$generatedWxs （$($entries.Count) 个文件 / $dirCounter 个目录）"

    Write-Host "  工程        : $msiProj"
    Write-Host "  VpPublishDir: $publishDir"
    Write-Host "  VpVersion   : $version"
    Write-Host ''

    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $msiOutput = @()
    $msiExit = 0
    try {
        $msiOutput = & dotnet build $msiProj `
            -c $Configuration `
            -p:Platform=x64 `
            -p:VpPublishDir="$publishDir" `
            -p:VpVersion=$version `
            --nologo 2>&1
        $msiExit = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $prevEap
    }

    Write-Host ('::warning::[诊断] WiX 退出码 = ' + $msiExit + '，输出行数 = ' + @($msiOutput).Count)

    if ($msiExit -ne 0) {
        Write-Host '  ── WiX 输出尾部 ──' -ForegroundColor Yellow
        foreach ($line in (@($msiOutput) | Select-Object -Last 80)) {
            $text = ($line | Out-String).TrimEnd()
            if ($text -match '\S') { Write-Host ('::error::' + $text) }
        }
        throw "MSI 打包失败（dotnet exit code = $msiExit）。"
    }
    Write-Host '  MSI 打包完成。' -ForegroundColor Green
}

# ═══════════════════════ 阶段 5：打包 EXE ═══════════════════════

if ($doExe) {
    Write-Step '⑤ 打包 EXE（Inno Setup 6）'

    if (-not (Test-Path $appOutDir)) {
        throw "发布目录不存在：$appOutDir。请先执行 -Stage App。"
    }

    # 许可协议 RTF：MSI 阶段已经生成，这里再确保一次（幂等），
    # 以便 -Stage Exe 单独执行时也能正常工作。
    $installerDir = Split-Path (Split-Path $issFile)   # installer/exe → installer
    $eulaTxt = Join-Path $installerDir 'EULA.txt'
    $eulaRtf = Join-Path $installerDir 'EULA.rtf'
    if (ConvertTo-Rtf -TextPath $eulaTxt -RtfPath $eulaRtf) {
        Write-Host "  已生成许可协议：$eulaRtf"
    }

    $iscc = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $iscc) {
        throw '未找到 Inno Setup 6（ISCC.exe）。下载：https://jrsoftware.org/isdl.php'
    }

    Write-Host "  ISCC: $iscc"
    Write-Host "  脚本: $issFile"
    Write-Host ''

    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $isccOutput = @()
    $isccExit = 0
    try {
        $isccOutput = & $iscc "/DMyAppVersion=$version" $issFile 2>&1
        $isccExit = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $prevEap
    }

    Write-Host ('::warning::[诊断] ISCC 退出码 = ' + $isccExit + '，输出行数 = ' + @($isccOutput).Count)

    if ($isccExit -ne 0) {
        # 单条注解有长度上限，长消息会被截断。
    # 这里跳过开头的版权横幅（约 10 行），并把剩余内容切成多条注解，
    # 保证真正的错误信息一定能被看到。
    $lines = @($isccOutput) |
        ForEach-Object { ($_ | Out-String).Trim() } |
        Where-Object { $_ } |
        Select-Object -Skip 10

    $chunkSize = 10
    $n = 0
    for ($s = 0; $s -lt $lines.Count; $s += $chunkSize) {
        $n++
        $end = [Math]::Min($s + $chunkSize - 1, $lines.Count - 1)
        $chunk = ($lines[$s..$end] -join ' / ')
        Write-Host ('::error::[ISCC#{0}] {1}' -f $n, $chunk)
        if ($n -ge 6) { break }
    }
        throw "EXE 打包失败（ISCC exit code = $isccExit）。"
    }
    Write-Host '  EXE 打包完成。' -ForegroundColor Green
}

# ═══════════════════════ 完成 ═══════════════════════

if ($Stage -eq 'All') {
    Write-Step '构建完成'
    if (Test-Path $distDir) {
        Get-ChildItem $distDir -File | ForEach-Object {
            Write-Host ('  {0,-42} {1,10:N1} MB' -f $_.Name, ($_.Length / 1MB)) -ForegroundColor Green
        }
    }
    Write-Host ''
    Write-Host '  提示：请先双击 VideoPresenter.Launcher.exe 验证「1 秒启动」体验。'
}
else {
    Write-Host ''
    Write-Host "  阶段 $Stage 完成。" -ForegroundColor Green
}