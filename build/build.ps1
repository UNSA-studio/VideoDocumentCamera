<#
.SYNOPSIS
    「视频展台」一键构建 / 出包脚本。

.DESCRIPTION
    产出物（dist/）：
        app/                                便携目录（可直接拷走运行）
            VideoPresenter.Launcher.exe     ← 启动器（原生，1 秒内出界面）
            VideoPresenter.exe             ← 主程序（self-contained）
            …运行时依赖
        视频展台-1.0.0-x64.msi              ← MSI 安装包（WiX v4）
        视频展台-1.0.0-x64-setup.exe        ← EXE 安装包（Inno Setup 6）

.PARAMETER Configuration
    Debug 或 Release，默认 Release。

.PARAMETER Package
    None / Msi / Exe / All，默认 All。

.PARAMETER Sign
    提供 PFX 路径后会对所有产物做数字签名（主体：UNSA Studio）。

.EXAMPLE
    ./build.ps1 -Configuration Release -Package All
    ./build.ps1 -Package Exe -Sign ./certs/unsa-studio.pfx

.NOTES
    开发商：UNSA Studio
#>

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('None', 'Msi', 'Exe', 'All')]
    [string]$Package = 'All',

    [string]$Sign = '',
    [string]$SignPassword = '',
    [switch]$SkipIcon,
    [switch]$SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ─────────────────────────── 路径 ───────────────────────────

$root       = Split-Path -Parent $PSScriptRoot
$srcDir     = Join-Path $root 'src'
$distDir    = Join-Path $root 'dist'
$appOutDir  = Join-Path $distDir 'app'
$buildOut   = Join-Path $root 'build\out'

$appProj    = Join-Path $srcDir 'VideoPresenter.App\VideoPresenter.App.csproj'
$launcherVcxp = Join-Path $srcDir 'VideoPresenter.Launcher\Launcher.vcxproj'
$msiProj    = Join-Path $root 'installer\wix\VideoPresenter.Msi.wixproj'
$issFile    = Join-Path $root 'installer\exe\setup.iss'
$iconScript = Join-Path $PSScriptRoot 'tools\make-icon.ps1'

$version    = '1.0.0'
$productName = '视频展台'

function Write-Step([string]$text) {
    Write-Host ''
    Write-Host ("═" * 72) -ForegroundColor DarkCyan
    Write-Host ("  $text") -ForegroundColor Cyan
    Write-Host ("═" * 72) -ForegroundColor DarkCyan
}

function Assert-Command([string]$name, [string]$hint) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw "找不到命令 $name。$hint"
    }
}

# ─────────────────────── 0. 环境自检 ───────────────────────

Write-Step '0/6  环境自检'
Assert-Command 'dotnet' '请安装 .NET 8 SDK（https://dot.net）'
Write-Host ("  dotnet : {0}" -f (dotnet --version))

$msbuild = $null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path $vswhere) {
    $msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' |
               Select-Object -First 1
}
if (-not $msbuild) {
    $msbuild = (Get-Command msbuild -ErrorAction SilentlyContinue).Source
}
if (-not $msbuild) {
    Write-Warning '  未找到 MSBuild：将跳过启动器（C++）编译，直接复用已有产物。'
    Write-Warning '  需安装 Visual Studio 2022 并勾选「使用 C++ 的桌面开发」工作负载。'
}
else {
    Write-Host ("  msbuild: {0}" -f $msbuild)
}

# ─────────────────────── 1. 生成图标 ───────────────────────

Write-Step '1/6  生成应用程序图标'
if ($SkipIcon) {
    Write-Host '  已按参数跳过'
}
else {
    & $iconScript
}

# ─────────────────────── 2. 编译启动器 ───────────────────────

Write-Step '2/6  编译启动器（原生 Win32，静态 CRT）'
if ($msbuild) {
    & $msbuild $launcherVcxp `
        /p:Configuration=$Configuration `
        /p:Platform=x64 `
        /m /nologo /v:minimal

    if ($LASTEXITCODE -ne 0) { throw '启动器编译失败。' }
    Write-Host '  启动器编译完成。'
}
else {
    Write-Host '  跳过（未检测到 MSBuild）。'
}

# ─────────────────────── 3. 发布主程序 ───────────────────────

Write-Step '3/6  发布主程序（self-contained + ReadyToRun）'
if (Test-Path $appOutDir) { Remove-Item $appOutDir -Recurse -Force }
New-Item -ItemType Directory -Path $appOutDir -Force | Out-Null

dotnet publish $appProj `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -o $appOutDir `
    -p:Platform=x64 `
    -p:Version=$version `
    -p:PublishReadyToRun=true `
    -p:WindowsAppSDKSelfContained=true `
    -p:DebugType=none `
    --nologo

if ($LASTEXITCODE -ne 0) { throw '主程序发布失败。' }
Write-Host '  主程序发布完成。'

# 把启动器放进同一个目录（启动器按"同目录下的 VideoPresenter.exe"定位主程序）
$launcherBuilt = Join-Path $buildOut "x64\$Configuration\VideoPresenter.Launcher.exe"
if (Test-Path $launcherBuilt) {
    Copy-Item $launcherBuilt $appOutDir -Force
    Write-Host '  启动器已复制到发布目录。'
}
elseif (-not (Test-Path (Join-Path $appOutDir 'VideoPresenter.Launcher.exe'))) {
    Write-Warning '  发布目录中没有启动器！桌面快捷方式将无法工作。'
}

Write-Host ''
Write-Host '  ── 发布目录体积统计 ──'
$size = (Get-ChildItem $appOutDir -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Host ("  共 {0} 个文件，{1:N1} MB" -f `
    (Get-ChildItem $appOutDir -Recurse -File).Count, ($size / 1MB))

# ─────────────────────── 4. 数字签名 ───────────────────────

Write-Step '4/6  数字签名（主体：UNSA Studio）'
if ([string]::IsNullOrWhiteSpace($Sign)) {
    Write-Host '  未提供 -Sign 参数，跳过。'
    Write-Host '  正式发布时请执行：'
    Write-Host '    ./build.ps1 -Sign .\certs\unsa-studio.pfx -SignPassword ***'
}
else {
    Assert-Command 'signtool' '请安装 Windows SDK（含 SignTool）。'
    $targets = Get-ChildItem $appOutDir -Filter '*.exe' -File
    foreach ($t in $targets) {
        & signtool sign /fd SHA256 /f $Sign /p $SignPassword `
            /tr http://timestamp.digicert.com /td SHA256 $t.FullName
        if ($LASTEXITCODE -ne 0) { throw "签名失败：$($t.Name)" }
        Write-Host ("  已签名 {0}" -f $t.Name)
    }
}

# ─────────────────────── 5. 打包 MSI ───────────────────────

Write-Step '5/6  打包 MSI（WiX v4）'
if ($Package -in @('Msi', 'All')) {
    Assert-Command 'dotnet' 'WiX v4 通过 dotnet tool 安装：dotnet tool install --global wix'
    dotnet build $msiProj -c $Configuration -p:Platform=x64 `
        -p:VpPublishDir="$appOutDir\" -p:VpVersion="$version" --nologo
    if ($LASTEXITCODE -ne 0) { throw 'MSI 打包失败。' }
    Write-Host '  MSI 打包完成。'
}
else {
    Write-Host '  已按参数跳过。'
}

# ─────────────────────── 6. 打包 EXE ───────────────────────

Write-Step '6/6  打包 EXE（Inno Setup 6）'
if ($Package -in @('Exe', 'All')) {
    $iscc = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $iscc) {
        Write-Warning '  未找到 Inno Setup 6，跳过 EXE 打包。'
        Write-Warning '  下载：https://jrsoftware.org/isdl.php'
    }
    else {
        & $iscc "/DMyAppVersion=$version" $issFile
        if ($LASTEXITCODE -ne 0) { throw 'EXE 打包失败。' }
        Write-Host '  EXE 打包完成。'
    }
}
else {
    Write-Host '  已按参数跳过。'
}

# ─────────────────────── 完成 ───────────────────────

Write-Step '构建完成'
Get-ChildItem $distDir -File | ForEach-Object {
    Write-Host ("  {0,-40} {1,10:N1} MB" -f $_.Name, ($_.Length / 1MB)) -ForegroundColor Green
}
Write-Host ''
Write-Host ("  产物目录：{0}" -f $distDir)
Write-Host '  提示：请先双击 VideoPresenter.Launcher.exe 验证「1 秒启动」体验。'