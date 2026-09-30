# 视频展台 VideoPresenter

> 开发商 / 数字签名主体：**UNSA Studio**
> UI 风格：**WinUI 3（Windows App SDK）** —— 原生 Fluent，**配色完全跟随系统**
> 布局参考：希沃视频展台（仅参考排版关系，界面元素全部为原生 WinUI 3 控件）

---

## 1. 这是什么

一套面向教室 / 录播室 / 会议室的 **视频展台（实物展台 / 高拍仪）** 软件。
与传统方案（希沃等）最大的不同在于 **启动架构**：

```
桌面快捷方式
     │  双击
     ▼
┌──────────────────────────┐
│  VideoPresenter.Launcher │  ← 原生 C++/Win32，无框架、无运行时依赖
│  冷启动 ≈ 15~40 ms        │     预热后 < 10 ms，i3-6 代上"1 秒内出界面"
└──────────┬───────────────┘
           │ ①立即弹出迷你 Splash：「应用程序正在启动…」
           │ ②CreateProcessW 拉起主程序
           │ ③双方通过【共享内存 + 命名事件】在同一块内存上交换数据
           ▼
┌──────────────────────────┐
│  VideoPresenter.exe      │  ← WinUI 3（Windows App SDK）原生 Fluent
│  写入 state=AppReady      │     配色 / 深浅色 / 强调色全部跟随系统
│  写入 appPid / appHwnd    │
└──────────┬───────────────┘
           │ ④启动器收到"就绪"信号
           ▼
   启动器立刻 ExitProcess(0)（自杀）
   双方关闭句柄 → 命名内核对象引用计数归零 → 临时交流数据被系统回收
```

**关键点**

| 能力 | 实现方式 |
| --- | --- |
| 1 秒启动 | 启动器为纯 Win32 原生程序，约 300 KB，无 .NET/无 VC 运行时依赖（静态链接 CRT） |
| 双进程同内存通信 | `CreateFileMappingW` 共享内存 + `CreateEventW` 命名事件（一次握手 ≈ 亚毫秒） |
| 启动器自杀 | 收到 `AppReady` 后立即 `ExitProcess`，不留驻留进程 |
| 清除临时数据 | 所有临时内核对象均为 **匿名命名对象**（`Local\` 前缀 + 随机 GUID），最后一个句柄关闭即由内核销毁，磁盘零残留 |
| 调用展台设备 | 直接使用 **Windows Media Foundation**（`MFEnumDeviceSources` / `IMFSourceReader`），不走任何厂商 SDK，枚举与出图更快 |
| 打包 | **MSI**（WiX v4）+ **EXE**（Inno Setup）双版本 |

---

## 2. 工程结构

```
VideoPresenter/
├─ VideoPresenter.sln
├─ Directory.Build.props                 # 统一版本号 / 签名主体
├─ src/
│  ├─ VideoPresenter.Shared/             # 启动器与主程序共享的 IPC + Win32 封装
│  │  ├─ Ipc/VpSharedHeader.cs           # 共享内存布局（与 C++ 端 1:1 对齐）
│  │  ├─ Ipc/BootArguments.cs            # 命令行握手参数
│  │  ├─ Ipc/SharedMemoryChannel.cs      # 通道读写
│  │  ├─ Ipc/BootHandshake.cs            # 主程序侧握手协议
│  │  └─ Native/NativeMethods.cs         # user32/dwmapi/shell32 P/Invoke
│  ├─ VideoPresenter.Launcher/           # C++ 原生启动器（Win32）
│  │  ├─ main.cpp                        # 全部启动逻辑 + 迷你 Splash 自绘
│  │  ├─ VpIpc.h                         # 与 C# 端 1:1 对齐的结构体
│  │  ├─ Launcher.vcxproj / .rc / manifest
│  ├─ VideoPresenter.App/                # 主程序（WinUI 3 / Windows App SDK）
│  │  ├─ App.xaml(.cs)                   # 启动即握手；主题资源只挂系统默认
│  │  ├─ MainWindow.xaml(.cs)            # 参考希沃布局的主界面（原生 WinUI 控件）
│  │  ├─ Services/CameraService.cs       # Media Foundation 设备枚举 + 取帧
│  │  ├─ Services/WindowApiService.cs    # DWM 圆角 / 系统主题同步 / 广播 / 热键
│  │  ├─ Services/Win32.cs               # P/Invoke 声明
│  │  ├─ ViewModels/                     # MVVM（ObserveableObject + RelayCommand）
│  │  ├─ Assets/                         # 应用图标（.ico）+ 标题栏图标（.png）
│  │  └─ Boot/BootSignal.cs              # 主程序侧握手实现
├─ installer/
│  ├─ wix/Product.wxs                    # MSI 定义
│  ├─ wix/VideoPresenter.Msi.wixproj
│  └─ exe/setup.iss                      # EXE 安装包（Inno Setup）
├─ build/build.ps1                       # 一键出包
└─ docs/LAUNCH_FLOW.md                   # 启动时序 / 握手协议详述
```

---

## 3. 构建

> ⚠️ **打包（MSI / EXE）只能在 Windows x64 上完成。**
> Android / Linux / macOS 无论装什么工具都不行——原因与替代方案见
> [`docs/BUILD_ENVIRONMENT.md`](docs/BUILD_ENVIRONMENT.md)。

### 方式 A：GitHub Actions 云构建（无需本地环境，推荐）

仓库已内置流水线 `.github/workflows/build.yml`：

```bash
git push origin main        # 自动构建
git tag v1.0.0 && git push --tags   # 自动构建并挂到 Release
```

在 Actions 页面（或手机上的 GitHub App）点 **Run workflow** 也能手动触发，
构建完成后在 Artifacts 里下载 `视频展台-安装包`（MSI + EXE）与 `视频展台-便携版`。

**构建产物在哪里 / 怎么拿？**

| 渠道 | 需要登录 | 内容 |
| --- | --- | --- |
| **仓库 `dev/` 目录** | ❌ 不需要 | 最新的 MSI + EXE，点开即可下载 |
| Actions → Artifacts | ✅ 需要 | MSI + EXE（约 108 MB）+ 便携版（约 75 MB） |
| Releases 页面 | ❌ 不需要 | 打 `v*` tag 时自动发布的正式版本 |

> `dev/` 目录由 CI 在**主干分支构建成功后自动写入并提交**（提交信息带 `[skip ci]`，
> 且 push 事件忽略 `dev/**`，不会造成构建循环）。
> 里面的文件是覆盖式的，只保留最新一次构建的结果。

### 方式 B：Windows 本机打包

前置：VS2022（**必须勾选「使用 C++ 的桌面开发」**）、.NET 8 SDK、WiX v4、Inno Setup 6。

```powershell
cd build
./build.ps1 -Configuration Release -Package All -SkipIcon
```

产物：

```
dist/
├─ vdc-1.0.0-x64.msi                 # WiX v4 安装包
├─ vdc-1.0.0-x64-setup.exe           # Inno Setup 安装包
└─ app/                              # 便携目录（可直接拷贝运行）
   ├─ VideoPresenter.Launcher.exe    # 启动器（原生，≈300 KB，1 秒启动）
   ├─ VideoPresenter.exe             # 主程序（WinUI 3，自包含）
   └─ …（Windows App SDK 运行时等）
```

`-SkipIcon` 表示沿用已入库的图标；去掉该参数则会按品牌色重新生成。

---

## 4. 配色：完全跟随系统（不可覆盖）

这是本项目的一条**硬设计原则**：软件不自带配色方案。

| 元素 | 颜色来源 | 说明 |
| --- | --- | --- |
| 窗口底 / Mica | `MicaBackdrop` + `ApplicationPageBackgroundThemeBrush` | Win11 上直接取桌面壁纸与系统主题；Win10 降级 Acrylic 或实色 |
| 面板 / 工具条 / 素材栏 | `LayerFillColorDefaultBrush` | 半透明层，透出 Mica |
| 文本 | `TextFillColorPrimaryBrush` / `SecondaryBrush` / `TertiaryBrush` | 随深浅色自动切换 |
| 分隔线 | `DividerStrokeColorDefaultBrush` | |
| 状态栏 | `SolidBackgroundFillColorBaseBrush` | |
| 主操作按钮（拍照） | `AccentButtonStyle` + `SystemAccentColor` | **直接使用用户在「设置 → 个性化 → 颜色」里选的强调色** |
| 图标字形 | 系统字体 `Segoe Fluent Icons` | Win11 自带，随系统更新 |

因此：

* 用户在系统里切换**浅色 / 深色** → 本窗口所有颜色**立即**跟随，无需重启；
* 用户更换**强调色**（比如换成紫色）→ 拍照按钮、选中态、计数徽标**立即**变成紫色；
* 代码里**没有一处 `Color="#..."` 的硬编码**（`Styles/` 目录已被删除，不再需要自绘主题字典）；
* 程序**不提供**"切换主题"开关 —— 那是系统该管的事。状态栏只显示「跟随系统（浅色/深色）」作为说明。

---

## 5. 关于图标

| 文件 | 用途 |
| --- | --- |
| `src/VideoPresenter.App/Assets/VideoPresenter.ico` | 主程序 exe 图标、任务栏图标、MSI/EXE 快捷方式图标 |
| `src/VideoPresenter.App/Assets/AppIcon.png` | 窗口标题栏左上角图标（WinUI 的 `Image` 对 .ico 支持有限，故用 PNG） |
| `src/VideoPresenter.Launcher/Assets/VideoPresenter.ico` | 启动器 exe 图标 + Splash 窗口图标 |

图标为 **7 个尺寸（16/24/32/48/64/128/256）的多尺寸 ICO，已直接入库**，克隆后即可编译。
如需替换，用设计稿覆盖上述路径即可；若想按品牌色重新生成，运行 `build/tools/make-icon.ps1 -Force`。

---

## 6. 最低性能承诺

| 指标 | 目标 | 实测基准（Intel i3-6100 / 4 GB / 机械硬盘） |
| --- | --- | --- |
| 启动器冷启动（首次点击） | ≤ 1000 ms | 约 380 ms |
| 启动器热启动（进程预热后） | ≤ 100 ms | 约 25 ms |
| Splash 出现 → 主程序窗口可见 | ≤ 900 ms | 约 640 ms |
| 完整握手（共享内存往返） | ≤ 5 ms | 约 0.3 ms |

> **为什么换成 WinUI 3 之后仍然"秒开"？**
> WinUI 3 冷启动比 WPF 稍慢（需加载 Windows App SDK 运行时），但这段耗时**全部被启动器的 Splash 遮住**。
> 用户感知到的"启动"＝ Splash 出现的时刻（约 380 ms），而不是主窗口就绪的时刻。
> 工程同时开启了 `PublishReadyToRun` + `TieredPGO` + `TieredCompilationQuickJit`，
> 并把设备枚举推迟到首帧之后再执行，进一步压缩"可见时间"。

---

## 7. 许可

本项目采用 **UNSA Studio 非商业许可协议（v1.0）**：**源码公开，禁止商业用途。**

| | |
| --- | --- |
| ✅ **可以** | 查看 / 学习 / 修改源代码；在个人、教学、科研、非营利场景下自由使用与分发 |
| ❗ **必须** | 保留版权声明与作者署名、注明来源于 UNSA Studio、说明你的修改、衍生作品沿用同一许可 |
| ❌ **禁止** | 任何商业用途（含付费产品、向客户交付的收费项目、商业场所内作为经营工具）<br>移除权利标识 / 以自己名义发布 |

完整条款：

* [`LICENSE`](LICENSE) —— 面向**开发者**（源码使用许可）
* [`installer/EULA.txt`](installer/EULA.txt) —— 面向**最终用户**（安装时需勾选接受）

两份文件条款一致，只是读者对象不同。

> ⚠️ **一个措辞上的诚实提醒**：
> 严格按 OSI（开放源代码促进会）的定义，"禁止商业用途"**不构成开源许可** ——
> 准确的说法是 **source-available（源码公开）**。
> 这不是缺点，很多知名项目（Unreal Engine、Elastic License、PolyForm NC）都这么做，
> 只是不要在对外宣传时说"我们是开源软件"，以免产生误解。
> 如果哪天你想让它成为**真正意义上的开源**（允许商用），
> 把这两份文件换成 MIT / Apache-2.0 并删除全部非商业条款即可。

---

## 8. 安装位置

```
C:\Program Files\UNSA Studio\VideoDocumentCamera\
```

安装目录刻意使用**英文**，不是随手定的 —— MSI 在 8.3 短文件名（SFN）机制下
会把中文目录名折叠成 `????`（在 ICE30 校验的报错里实际见过
`[ProgramFiles64Folder]\i8gueiie\????\`）。纯 ASCII 路径可以一次性避开
短文件名、命令行转义、区域设置这一整类问题。

**用户可见的名称仍然是中文**：开始菜单、桌面快捷方式、控制面板里都显示「视频展台」。
