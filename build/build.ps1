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

    $msbuild = Find-MSBuild
    if (-not $msbuild) {
        throw '未找到 MSBuild。请安装 Visual Studio（勾选「使用 C++ 的桌面开发」工作负载）。'
    }
    Write-Host "  MSBuild: $msbuild"
    Write-Host "  项目   : $launcherVcxp"
    Write-Host ''

    New-Item -ItemType Directory -Path $buildOut -Force | Out-Null
    $msbuildLog = Join-Path $buildOut 'launcher-build.log'

    # 完整捕获 MSBuild 的 stdout + stderr。
    # CI 上曾出现"日志只到 PrepareForBuild 就断了"的现象 —— 说明 MSBuild 是异常终止
    # 而非报编译错误，因此必须拿到退出码与真实输出。
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'   # 避免 native stderr 触发陷阱
    $output = @()
    $exitCode = 0
    try {
        $output = & $msbuild $launcherVcxp `
            /p:Configuration=$Configuration `
            /p:Platform=x64 `
            /nologo /v:normal 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $prevEap
    }

    Write-Host ''
    Write-Host ("  MSBuild 退出码 : " + $exitCode)
    Write-Host ("  捕获输出行数   : " + @($output).Count)
    Write-Host ''

    if ($exitCode -ne 0) {
        Write-Host '  ── MSBuild 输出尾部（同时作为注解，便于远程诊断）──' -ForegroundColor Yellow
        foreach ($line in (@($output) | Select-Object -Last 80)) {
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

# ═══════════════════════ 阶段 2：发布主程序 ═══════════════════════

if ($doApp) {
    Write-Step '② 发布主程序（WinUI 3 / 自包含 / ReadyToRun）'

    Assert-Command 'dotnet' '请安装 .NET 8 SDK（https://dot.net）'

    if (Test-Path $appOutDir) { Remove-Item $appOutDir -Recurse -Force }
    New-Item -ItemType Directory -Path $appOutDir -Force | Out-Null
    Write-Host "  输出：$appOutDir"
    Write-Host ''

    dotnet publish $appProj `
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
        --nologo

    if ($LASTEXITCODE -ne 0) {
        throw "主程序发布失败（dotnet exit code = $LASTEXITCODE）。"
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

    Write-Host "  工程        : $msiProj"
    Write-Host "  VpPublishDir: $publishDir"
    Write-Host "  VpVersion   : $version"
    Write-Host ''

    dotnet build $msiProj `
        -c $Configuration `
        -p:Platform=x64 `
        -p:VpPublishDir="$publishDir" `
        -p:VpVersion=$version `
        --nologo

    if ($LASTEXITCODE -ne 0) {
        throw "MSI 打包失败（dotnet exit code = $LASTEXITCODE）。"
    }
    Write-Host '  MSI 打包完成。' -ForegroundColor Green
}

# ═══════════════════════ 阶段 5：打包 EXE ═══════════════════════

if ($doExe) {
    Write-Step '⑤ 打包 EXE（Inno Setup 6）'

    if (-not (Test-Path $appOutDir)) {
        throw "发布目录不存在：$appOutDir。请先执行 -Stage App。"
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

    & $iscc "/DMyAppVersion=$version" $issFile

    if ($LASTEXITCODE -ne 0) {
        throw "EXE 打包失败（ISCC exit code = $LASTEXITCODE）。"
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