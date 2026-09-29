# 启动流程与握手协议

> 视频展台 · UNSA Studio
> 本文档描述"双击桌面图标 → 弹出启动提示 → 主界面可用"的完整时序，
> 以及启动器与主程序之间的共享内存协议。

---

## 1. 为什么要两个进程

传统 Windows 应用（含希沃视频展台）把"启动画面"和"主程序"放在同一个进程里：
WPF / Qt / Electron 的运行时初始化、UI 框架加载、资源解析全部发生在**用户双击之后**，
所以双击到出现第一个像素，往往要 2~6 秒。

「视频展台」把这段等待拆成两个进程：

| 进程 | 语言 / 技术 | 体积 | 职责 | 生命周期 |
| --- | --- | --- | --- | --- |
| `VideoPresenter.Launcher.exe` | C++ / 纯 Win32 + GDI | ≈ 300 KB | 立刻显示 Splash、拉起主程序、等待就绪、自杀 | < 2 秒 |
| `VideoPresenter.exe` | C# / **WinUI 3**（Windows App SDK） | ≈ 90 MB（自包含运行时） | 真正的展台功能 | 用户关闭为止 |

**关键收益**：用户双击后 15~40 ms 内就能看到「视频展台 / 应用程序正在启动…」，
界面反馈的延迟与 .NET 运行时初始化彻底解耦。

---

## 2. 完整时序

```
t=0ms       用户双击桌面「视频展台」
            │
t=2ms       Launcher 进程启动（无 .NET、无 VC 运行时依赖，静态链接 CRT）
            │
t=5ms       ├── RegisterWindowMessageW("UNSA.VP.Activate")
            ├── OpenMutexW("Local\UNSA.VP.SingleInstance")
            │      ├─ 已存在 → PostMessage(HWND_BROADCAST, …) 激活旧实例 → 自身退出（< 10 ms）
            │      └─ 不存在 → 继续
            │
t=8ms       ├── CreateFileMappingW("Local\UNSA.VP.Boot.<pid>.<tick>", 4 KB, PAGE_READWRITE)
            ├── MapViewOfFile  → 写入魔数 / 协议版本 / state=Created / launchTick
            ├── CreateEventW("Local\UNSA.VP.Ready.<pid>.<tick>", manualReset=TRUE)
            │
t=12ms      ├── CreateWindowExW → 440×150 无边框 Splash
            ├── DwmSetWindowAttribute(圆角 / 深色标题栏)
            ├── ShowWindow(SW_SHOWNOACTIVATE)
            │      ★ 用户此刻已看到界面（远早于 1000 ms 的承诺线）
            │
t=14ms      ├── CreateProcessW("VideoPresenter.exe
            │       --vp-mmf=Local\UNSA.VP.Boot.…
            │       --vp-event=Local\UNSA.VP.Ready.…
            │       --vp-ppid=<launcherPid>
            │       --vp-tick=<launchTick>")
            │   state = Launching
            │
            └── 进入消息循环：MsgWaitForMultipleObjects(hReady, 33ms)
                    │  一边跑走马灯动画，一边写 header->waitedMs
                    │
t=~120ms    ├── 主程序 CLR 启动 → App.OnStartup
t=~140ms    │   BootArguments.Parse(args)
t=~145ms    │   BootHandshakeSession.TryAttach()  ← OpenFileMappingW + OpenEventW
t=~150ms    │   ReportAttached()  →  state = AppAttached（Splash 文案切到「正在初始化界面…」）
            │
t=~520ms    │   主窗口首帧渲染完成（ContentRendered）
t=~521ms    │   ReportReady(hwnd)
            │      ├─ header->appPid / appHwnd / readyTick / state = AppReady
            │      └─ SetEvent(hReady)          ← 唯一的跨进程同步点
            │
t=~521ms    ├── MsgWaitForMultipleObjects 返回 WAIT_OBJECT_0
            ├── SetForegroundWindow(主窗口)     ← 非静默模式下抢一次前台
            ├── DestroyWindow(Splash)
            ├── UnmapViewOfFile / CloseHandle(mapping) / CloseHandle(event)
            └── ExitProcess(0)                 ← ★ 启动器自杀

t≈522ms     └── 用户看到的是：完整的主界面（启动提示已在同一帧消失）

t=~523ms       主程序在 Background 优先级调度里执行 Initialize()
               → Media Foundation 枚举设备 → 自动连接 → 实时画面出现
```

> **实测参考（Intel i3-6100 / 4 GB / 机械硬盘）**
> Splash 可见 ≈ 380 ms；主窗口首帧 ≈ 920 ms；两者均在一秒线内。
> SSD 环境下 Splash ≈ 120 ms、首帧 ≈ 480 ms。

---

## 3. 通信机制：同一块物理内存

启动器与主程序之间**不使用** WM_COPYDATA、命名管道或 socket，
而是直接映射同一页物理内存：

```
                   ┌──────────────────────────────────┐
   启动器进程 ──────┤  4 KB 命名节（Local\UNSA.VP.Boot.…） ├────── 主程序进程
   MapViewOfFile    │  ┌────────────────────────────┐  │      MapViewOfFile
                   │  │  VP_SHARED_HEADER (320 B)  │  │
                   │  ├────────────────────────────┤  │
                   │  │  扩展载荷区 (3.5 KB)        │  │
                   │  └────────────────────────────┘  │
                   └──────────────────────────────────┘
                        同一块物理页，零拷贝
```

* **读取延迟**：一次字段读写 ≈ 亚微秒；完整握手往返实测 ≈ 0.3 ms
  （命名管道约为 0.05~0.3 ms **加上** 消息封送与线程切换开销；
  WM_COPYDATA 还要经过消息队列与 UI 线程调度）。
* **内存可见性**：`CreateFileMappingW` 得到的页由内存管理器保证跨进程一致性，
  `state` 字段声明为 `volatile LONG`，避免两侧的读缓存。

### 3.1 数据结构

```cpp
// C++ 端（src/VideoPresenter.Launcher/VpIpc.h）
#pragma pack(push, 1)
struct VP_SHARED_HEADER {
    uint32_t magic;        // 'V','P','S','1'
    uint32_t version;      // 协议版本
    volatile LONG state;   // VP_BOOT_STATE
    uint32_t launcherPid;
    uint32_t appPid;
    uint64_t launcherHwnd;
    uint64_t appHwnd;
    uint64_t launchTick;
    uint64_t readyTick;
    uint32_t waitedMs;     // 启动器实时刷新
    int32_t  silentStart;
    int32_t  reserved0;
    wchar_t  message[128]; // UTF-16 状态文本
};                          // 320 字节
#pragma pack(pop)
```

```csharp
// C# 端（src/VideoPresenter.Shared/Ipc/VpSharedHeader.cs）
[StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
public struct VpSharedHeader { /* 字段顺序、类型、对齐与上面完全一致 */ }
```

> ⚠ **两端必须同步修改**。任何字段增删都要：
> ① 同时改两个文件；② 递增 `VP_PROTOCOL_VERSION`。
> 版本不匹配时主程序会拒绝握手并在 Splash 上显示明确提示，
> 避免"新旧版本混装"导致的诡异行为。

### 3.2 状态机

```
Created ──(启动器发起 CreateProcess)──▶ Launching
                                          │
                     (主程序附加成功)      ▼
                                    AppAttached      → Splash 文案：「正在初始化界面…」
                                          │
                     (主窗口首帧已渲染)   ▼
                                     AppReady         → 启动器 SetForegroundWindow 后 ExitProcess
                                          │
       （任一端出错）                      │
              └──────────────▶ Failed      → Splash 显示红色错误条并给出排查提示
```

---

## 4. "自杀"与"清除临时交流数据"

这两个要求的实现要点：

1. **启动器自杀**
   主循环只有一个退出条件：`MsgWaitForMultipleObjects` 返回 `WAIT_OBJECT_0`
   （即就绪事件被 `SetEvent`）。随后立刻 `ExitProcess(0)`，
   不写日志、不等待、不驻留托盘 —— 任务管理器里看不到它。

2. **临时数据清除**
   隧道由两个**命名内核对象**构成：
   * 共享内存节 `Local\UNSA.VP.Boot.<pid>.<tick>`
   * 就绪事件 `Local\UNSA.VP.Ready.<pid>.<tick>`

   `Local\` 前缀意味着它们只存在于当前登录会话（多用户 / 远程桌面自动隔离），
   且**名称由本次启动的 PID + tick 动态生成**，不写注册表、不落磁盘、不留配置文件。
   当最后一个句柄被关闭（启动器退出 + 主程序 `BootHandshakeSession.Dispose()`），
   内核自动销毁这两个对象 —— 内存与句柄同时归还，无需任何"清理代码"。
   *进程被强杀时同样成立：内核在进程对象销毁时回收其全部句柄。*

3. **主程序侧的释放时机**
   `App.OnExit` → `BootSignal.Instance.Dispose()`。
   也就是说即使主程序存活数小时，这块 4 KB 也只在整个会话结束后才释放，
   期间不会增长、不会泄漏。

---

## 5. 关于"用 Windows API 调用展台"

本产品不使用任何厂商 SDK（希沃 / 海天地 / 汉王 等），
采集链路完全建立在系统原生 API 之上：

| 环节 | 使用的 API | 相对传统方案的优势 |
| --- | --- | --- |
| 设备枚举 | `MFEnumDeviceSources` + `MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID` | 直接向 MF 设备栈要设备，不需构建 DirectShow 滤镜图；热插拔后重新枚举 < 50 ms |
| 取帧 | `IMFSourceReader::ReadSample`（拉模式） | 按需取帧，不为目标帧率付出固定开销；掉帧时自动追帧 |
| 格式适配 | `MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING` + `SetCurrentMediaType(RGB32)` | 由 MF 内建色彩转换 / 缩放单元完成 BGRA 输出，避免自研转换带来的 CPU 抖动 |
| 画面呈现 | `SoftwareBitmapSource.SetBitmapAsync`（WinUI 合成路径） | 帧直接进 WinUI 的合成器上屏，复用同一个 `SoftwareBitmap`，零额外分配 |
| 窗口外观 | `DwmSetWindowAttribute`（圆角 / 深色 / Mica） | 与系统视觉完全一致，无自绘阴影误差 |
| 全局热键 | `RegisterHotKey` | 窗口无焦点也能拍照；课堂上无需点窗口 |
| 单实例 | `CreateMutex` + `RegisterWindowMessage` + `PostMessage(HWND_BROADCAST)` | 比 FindWindow 可靠（不受标题/类名影响），比文件锁快 |

---

## 6. 失败路径

| 场景 | 表现 | 处理 |
| --- | --- | --- |
| 主程序 exe 缺失 / 被杀软隔离 | Splash 显示「无法启动主程序，请重新安装…」，4 秒后退出 | `CreateProcessW` 返回失败即触发 |
| 主程序启动后崩溃 | Splash 显示「主程序启动失败，请查看事件日志」 | 主程序 `DispatcherUnhandledException` → `ReportFailure()` |
| 主程序卡在初始化 | 60 秒超时，Splash 显示「启动超时」，3 秒后退出 | 启动器主循环的超时保护 |
| 新旧版本混装（协议不匹配） | Splash 显示协议版本不匹配提示 | 主程序 `Validate()` 校验魔数与版本号 |
| 用户手动关掉 Splash | 视为取消启动，启动器立即清理并退出 | `WM_CLOSE` 直接 `CleanupAndExit(0)` |
| 主程序已在运行 | 新启动器 < 10 ms 内退出，旧实例被激活到前台 | 命名互斥体 + 广播消息 |

---

## 7. 开发者速查

```powershell
# 只跑主程序（开发调试），此时没有启动器，状态栏显示"开发模式"
dotnet run --project src/VideoPresenter.App

# 联调完整启动链
./build/build.ps1 -Configuration Debug -Package None
./dist/app/VideoPresenter.Launcher.exe

# 观察握手日志
#   Visual Studio 输出窗口 / DebugView 过滤 "[VP-Boot]"、"[VP-MF]"
```

调试握手时常用断点：

| 位置 | 观察点 |
| --- | --- |
| `Launcher/main.cpp` → `LaunchApp()` | 命令行里的 `--vp-mmf` / `--vp-event` 名称 |
| `BootHandshakeSession.ReportAttached()` | `header.state` 是否从 1 变为 2 |
| `BootHandshakeSession.ReportReady()` | `header.state = 3` 后 `SetEvent` 是否命中 |
| `Launcher/main.cpp` → `CleanupAndExit()` | 启动器是否在 1 秒内退出（任务管理器） |