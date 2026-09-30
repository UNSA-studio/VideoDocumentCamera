; ============================================================================
;  视频展台 · EXE 安装包脚本（Inno Setup 6）
;  开发商 / 发布者：UNSA Studio
;
;  编译：
;      ISCC.exe installer\exe\setup.iss
;  或：
;      .\build\build.ps1 -Package Exe
;
;  与 MSI 版本的关系
;  ────────────────────────────────────────────────────────────────────────
;      两者功能等价，任选其一：
;        · MSI —— 适合企业批量部署（支持组策略 / SCCM / 静默安装）
;        · EXE —— 适合个人用户（向导更友好，中文体验更完整）
; ============================================================================

; ── 产品信息 ─────────────────────────────────────────────────────────────────
;  用 #ifndef 包裹：命令行可以用 ISCC /DMyAppName="…" /DMyAppVersion="…" 覆盖；
;  若不包裹，命令行定义与这里的 #define 同名会触发 ISPP 的重复符号错误。
#ifndef MyAppName
  #define MyAppName "视频展台"
#endif

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

#ifndef MyAppPublisher
  #define MyAppPublisher "UNSA Studio"
#endif

#ifndef MyAppURL
  #define MyAppURL "https://www.unsa.studio/"
#endif

#ifndef MyAppExeName
  #define MyAppExeName "VideoPresenter.Launcher.exe"
#endif

[Setup]
; AppId 一旦发布不可更改，否则会被视为另一个产品。
; 这里刻意【不写花括号】—— Inno 里 { 是常量起始符，{{ 表示字面 {，
; 写成 {{GUID} 会在部分版本上产生解析歧义（实测 Inno Setup 6.7 报
; "Parsing [Setup] section, line 42"）。AppId 允许是任意字符串，
; 用纯 GUID 文本最稳。
AppId=9C4B7A21-6E58-4D33-A1F7-2B8E5C90D6A4
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}

; 许可协议：使用 build.ps1 从 EULA.txt 转换出的 RTF。
;  为什么不用 .txt：Inno 读取无 BOM 的文本文件时按【系统 ANSI 代码页】解析，
;  而 CI runner 是英文 Windows（代码页 1252）—— 中文会变乱码。
;  RTF 里中文以 \u<码点>? 转义并声明 \ansicpg936，任何系统都能正确显示。
LicenseFile=..\EULA.rtf

; 安装目录：UNSA Studio\VideoDocumentCamera
;  刻意使用【英文】目录名 —— MSI 在 8.3 短文件名机制下会把中文折叠成 '????'，
;  纯 ASCII 路径可避开短文件名、命令行转义、区域设置这一整类问题。
;  用户可见的名称（开始菜单、桌面快捷方式、控制面板）仍然是中文「视频展台」。
DefaultDirName={autopf}\UNSA Studio\VideoDocumentCamera
DefaultGroupName=UNSA Studio
DisableProgramGroupPage=yes
AllowNoIcons=yes

OutputDir=..\..\dist
; 安装包文件名用缩写 vdc（VideoDocumentCamera），短、好敲、纯 ASCII。
; 用户可见的产品名仍然是中文「视频展台」，见 AppName。
OutputBaseFilename=vdc-{#MyAppVersion}-x64-setup
SetupIconFile=..\..\src\VideoPresenter.App\Assets\VideoPresenter.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}（UNSA Studio）

Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
; 用传统值 x64（而非 Inno Setup 6.3+ 才引入的 x64compatible），兼容性最好
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64

MinVersion=10.0.17763
VersionInfoVersion=1.0.0.0
VersionInfoCompany=UNSA Studio
VersionInfoDescription=视频展台 安装程序
VersionInfoProductName=视频展台
; 产品名后的 (C) 行会显示在文件属性中
VersionInfoCopyright=Copyright (C) 2026 UNSA Studio. All rights reserved.

; ── 数字签名（主体必须为 UNSA Studio）───────────────────────────────────
;  在 Inno Setup 中配置 SignTool：
;      Tools → Configure Sign Tools… → Name: unsa
;      Command: "C:\Program Files (x86)\Windows Kits\10\bin\x64\signtool.exe" sign
;               /fd SHA256 /f "C:\certs\unsa-studio.pfx" /p <密码>
;               /tr http://timestamp.digicert.com /td SHA256 $f
;  配置后取消下面一行的注释即可自动签名。
;SignTool=unsa

; ── 语言 ─────────────────────────────────────────────────────────────────────
;  简体中文语言文件随仓库提供：installer/Languages/ChineseSimplified.isl
;  （取自 Inno Setup 官方源码仓库 jrsoftware/issrc 的 Files/Languages/）
;
;  为什么不直接引用构建机自带的语言目录：各版本 Inno Setup 的安装包里
;  是否附带该文件并不稳定 —— CI 上就实际报过
;      Couldn't open include file "...\Inno Setup 6\Languages\ChineseSimplified.isl"
;  自带一份才能保证任何机器上构建出来的安装包语言都一致。
;
;  优先级：仓库自带 > 构建机自带 > 仅英文（三种情况都不会报错）
[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

#if FileExists("..\Languages\ChineseSimplified.isl")
Name: "chinesesimplified"; MessagesFile: "..\Languages\ChineseSimplified.isl"
#elif FileExists(AddBackslash(CompilerPath) + "Languages\ChineseSimplified.isl")
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
#endif

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："
Name: "launchafter"; Description: "安装完成后立即运行 视频展台"; GroupDescription: "附加任务："; Flags: unchecked

[Files]
; 打包整个发布目录（含启动器、主程序与运行时）
Source: "..\..\dist\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\视频展台"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{group}\卸载 视频展台"; Filename: "{uninstallexe}"
Name: "{autodesktop}\视频展台"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即运行 视频展台"; Flags: nowait postinstall skipifsilent; Tasks: launchafter

[UninstallDelete]
; 运行时产生的日志/缓存（用户素材保存在"图片\视频展台"，卸载时保留）
Type: filesandordirs; Name: "{localappdata}\UNSA Studio\视频展台"

[Registry]
; 供主程序读取「安装来源」，便于诊断
Root: HKLM; Subkey: "Software\UNSA Studio\视频展台"; ValueType: string; \
    ValueName: "InstallDir"; ValueData: "{app}"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\UNSA Studio\视频展台"; ValueType: string; \
    ValueName: "LauncherExe"; ValueData: "{app}\{#MyAppExeName}"; Flags: uninsdeletekey