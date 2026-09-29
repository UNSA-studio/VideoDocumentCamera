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
; AppId 一旦发布不可更改，否则会被视为另一个产品
AppId={{9C4B7A21-6E58-4D33-A1F7-2B8E5C90D6A4}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}

DefaultDirName={autopf}\UNSA Studio\{#MyAppName}
DefaultGroupName=UNSA Studio
DisableProgramGroupPage=yes
AllowNoIcons=yes

OutputDir=..\..\dist
OutputBaseFilename=视频展台-{#MyAppVersion}-x64-setup
SetupIconFile=..\..\src\VideoPresenter.App\Assets\VideoPresenter.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}（UNSA Studio）

Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

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

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english";           MessagesFile: "compiler:Default.isl"

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