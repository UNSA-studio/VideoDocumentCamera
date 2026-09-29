# 构建环境要求

> 结论先写在最前面：**打包（MSI / EXE）只能在 Windows 上做，且必须是 x64 Windows。**
> Android / Linux / macOS / ARM 设备都做不到，这不是权限问题，而是工具链的硬性依赖。

---

## 1. 为什么不能在当前环境（Android / Linux aarch64）打包

实际探测当前工作区环境的结果：

```
运行平台 : Linux-4.14.116-aarch64-with-glibc2.39
机器架构 : aarch64
dotnet   : /usr/bin/dotnet
mono     : /usr/bin/mono
gcc/g++  : 已安装
msbuild  : （未安装）
cl       : （未安装）    ← MSVC 编译器，C++ 启动器必需
clang    : （未安装）
wine     : （未安装）
iscc     : （未安装）    ← Inno Setup 编译器
wix      : （未安装）
signtool : （未安装）
```

即使把缺的工具都装上，仍然过不去下面三道坎：

| # | 障碍 | 说明 |
| --- | --- | --- |
| ① | **CPU 架构不匹配** | 宿主是 `aarch64`，目标是 `win-x64`。虽然 Linux→Windows 的 C/C++ 交叉编译理论上可行，但需要完整的 Windows SDK（`.lib` / `.winmd` / 头文件），还要处理 `rc.exe`（资源编译器）——它同样是 Windows PE 程序。 |
| ② | **WinUI 3 禁止非 Windows 宿主构建** | 项目设置 `UseWinUI=true` 后，Windows App SDK 的 MSBuild targets 会强制检查构建宿主。XAML 编译器（`XamlCompiler.exe`）本身就是 Windows 程序，"在 Linux 上编译 WinUI 3"不受支持。 |
| ③ | **打包工具是 Windows-only** | `ISCC.exe`（Inno Setup）无 Linux 版；`signtool.exe` 是 Windows 程序；WiX 虽然本身是 .NET 工具，但 MSI 的目录表语义（`ProgramFiles64Folder` 等）与文件收割都依赖 Windows 环境。 |

> 顺带说明：`dotnet` 存在并不代表能编出 Windows 程序。
> 对于**不依赖 WinUI/WPF/WinForms** 的纯 C# 库，`dotnet publish -r win-x64` 在 Linux 上确实可行；
> 但本项目的主程序是 WinUI 3、启动器是原生 C++，两者都不在此列。

---

## 2. 三种可行的打包方式

### 方式 A：Windows 本机打包（最直接）

**前置**

| 组件 | 版本 | 说明 |
| --- | --- | --- |
| Windows | 10 1809+ / 11，**x64** | ARM64 Windows 需要改用 `-r win-arm64` 并自行验证 C++ 工程 |
| Visual Studio 2022 | 17.9+ | 必须勾选 **「使用 C++ 的桌面开发」** 工作负载（含 MSBuild + Windows SDK + v143 工具集） |
| .NET 8 SDK | 8.0.x | https://dot.net |
| WiX v4 | 最新 | `dotnet tool install --global wix` |
| Inno Setup 6 | 最新 | https://jrsoftware.org/isdl.php |

**执行**

```powershell
cd build
./build.ps1 -Configuration Release -Package All -SkipIcon
```

产物落在 `dist/`：

```
dist/
├─ 视频展台-1.0.0-x64.msi
├─ 视频展台-1.0.0-x64-setup.exe
└─ app/                              便携目录（含两个 exe，可直接拷贝运行）
```

`-SkipIcon` 表示沿用已入库的图标；若想按品牌色重新生成，去掉该参数。

### 方式 B：GitHub Actions 云构建（推荐给"手上没有 Windows 机器"）

仓库里已经放好了流水线：`.github/workflows/build.yml`。

```
把项目推到 GitHub
        │
        ├─ 推送到 main / master  → 自动构建
        ├─ 打 tag（v1.0.0）      → 自动构建 + 挂到 Release
        └─ Actions 页面 → Run workflow（手动触发）

runner: windows-latest（预装 VS2022 + Windows SDK）
产物  : Artifacts → 视频展台-安装包 / 视频展台-便携版
```

手机上也能触发：在 GitHub App 里点一下 Run workflow，几分钟后下载 MSI / EXE 即可。

### 方式 C：自建 CI（Azure Pipelines / Jenkins）

同样需要 `windows-latest` 或自建 **Windows x64** 代理机；步骤与方式 A 完全一致。
非 Windows 的代理机一律不可用。

---

## 3. 当前环境中**可以**做的事情

虽然打不出安装包，但这个工作区并非只能看代码：

| 可以做 | 说明 |
| --- | --- |
| 编辑 / 重构全部源码 | 工程是纯文本，任意工具都能改 |
| 语法层面的自查 | 用 `grep_code` 检查 C++ 与 C# 两端的共享结构体是否仍然逐字节一致 |
| 重新生成图标 | `python build/tools/make_icon.py`（本环境已验证可运行，纯标准库） |
| 生成代码文档 / 审查 | 静态阅读、结构梳理 |
| 编写 CI 配置 | 也就是本仓库已经放好的 workflow |

| 做不到 | 原因 |
| --- | --- |
| 编译 C++ 启动器 | 缺 MSVC |
| 编译 WinUI 3 主程序 | 框架限制 + 缺 Windows SDK |
| 生成 MSI / EXE | 缺 WiX 运行时环境与 Inno Setup |
| 数字签名 | 缺 signtool 与证书 |

---

## 4. 关于"想先看看界面长什么样"

编译之前想确认视觉效果，有两个办法：

1. **在 Windows 上用设计时预览**：用 VS2022 打开 `MainWindow.xaml`，XAML 设计器可直接渲染（需要先恢复 NuGet 包）。
2. **让 AI 在工作区里生成一份 HTML 界面预览**：用 Fluent 配色与布局关系做静态还原，
   手机浏览器即可打开，用于**确认排版与层次**，但它不是真实控件，不保证与 WinUI 逐像素一致。

---

## 5. 速查表

| 问题 | 答案 |
| --- | --- |
| 能在这里（Android）打包吗？ | ❌ 不能 |
| 能在这里编译 C# 逻辑吗？ | ⚠️ 纯 C# 库可以；WinUI 3 主程序不行 |
| 能在这里编译 C++ 启动器吗？ | ❌ 不能（无 MSVC） |
| 能在这里重新生成图标吗？ | ✅ 可以（`make_icon.py`，纯 Python 标准库） |
| 最省事的打包方式？ | ✅ 推 GitHub，用 `windows-latest` runner |
| 必须本地打包的话？ | Windows x64 + VS2022（C++ 工作负载）+ .NET 8 + WiX + Inno Setup |