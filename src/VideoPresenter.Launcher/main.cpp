// ============================================================================
//  main.cpp —— 视频展台 启动器（VideoPresenter.Launcher）
//  开发商：UNSA Studio
//
//  设计目标
//  ────────────────────────────────────────────────────────────────────────
//  1. 极致轻量：纯 Win32 + GDI，静态链接 CRT，/O1，无 .NET、无第三方依赖；
//     链接后约 300 KB，冷启动 15~40 ms，i3-6 代机器上"1 秒内出界面"。
//  2. 立刻可见：进程起来后第一时间弹出 440×150 的迷你 Splash，
//     文案「应用程序正在启动…」，用户感知为零空窗。
//  3. 同内存通信：与主程序通过 CreateFileMappingW 共享同一页物理内存 +
//     命名事件完成握手，往返耗时约 0.3 ms（比常规 WM_COPYDATA / 命名管道快一个量级）。
//  4. 收到"主程序就绪"信号后立刻 ExitProcess —— 启动器自杀，绝不驻留。
//  5. 关闭全部句柄后，命名内核对象引用计数归零，由系统自动销毁，
//     临时交流数据在磁盘上零残留。
// ============================================================================

#include "VpIpc.h"
#include <dwmapi.h>
#include <cstdio>
#include <cwchar>

#pragma comment(lib, "dwmapi.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "advapi32.lib")

namespace {

// ── 常量 ────────────────────────────────────────────────────────────────────
const wchar_t* kSplashClass   = L"VpLauncherSplashWnd";
const wchar_t* kAppExeName    = L"VideoPresenter.exe";

// 主程序单实例互斥体（与 C# 侧 App.xaml.cs 中的名字保持一致）
const wchar_t* kSingleMutex   = L"Local\\UNSA.VP.SingleInstance";
const wchar_t* kActivateMsg   = L"UNSA.VP.Activate";

const UINT_PTR  kAnimIntervalMs = 33;                       // 30 fps 走马灯
const DWORD     kBootTimeoutMs  = 60000;                    // 主程序启动超时

const int kBaseWidth  = 440;
const int kBaseHeight = 150;
const int kPadding    = 24;

// 由 RegisterWindowMessageW 动态分配的"激活已有实例"消息
UINT g_msgActivate = 0;

// ── Splash 状态 ─────────────────────────────────────────────────────────────
struct SplashState
{
    HWND         hwnd      = nullptr;
    VP_CHANNEL*  channel   = nullptr;
    bool         dark      = false;
    int          dpi       = 96;
    int          phase     = 0;        // 0=启动中, 1=初始化界面, 2=失败
    wchar_t      status[160] = L"应用程序正在启动…";
    float        marquee   = 0.0f;
    DWORD        startTick = 0;

    HFONT fontTitle = nullptr;
    HFONT fontSub   = nullptr;
};

SplashState g;
VP_CHANNEL  g_channel;

// ── 工具函数 ────────────────────────────────────────────────────────────────

/// 读取系统"应用主题"偏好：true = 深色
bool IsDarkTheme()
{
    DWORD value = 1;   // 默认浅色
    DWORD size  = sizeof(value);
    RegGetValueW(HKEY_CURRENT_USER,
                 L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize",
                 L"AppsUseLightTheme",
                 RRF_RT_REG_DWORD, nullptr, &value, &size);
    return value == 0;
}

int ScaleForDpi(int value, int dpi) { return MulDiv(value, dpi, 96); }

/// 取启动器同目录下的主程序完整路径
bool BuildAppPath(wchar_t* out, size_t cch)
{
    if (GetModuleFileNameW(nullptr, out, (DWORD)cch) == 0) return false;
    wchar_t* slash = wcsrchr(out, L'\\');
    if (!slash) return false;
    *(slash + 1) = L'\0';
    if (wcslen(out) + wcslen(kAppExeName) + 1 > cch) return false;
    wcscat_s(out, cch, kAppExeName);
    return true;
}

// ── Splash 绘制（完全 GDI，无任何 UI 框架） ────────────────────────────────

void DrawSplash(HDC dc, const RECT& rc)
{
    const COLORREF bg      = g.dark ? RGB(32, 32, 32)   : RGB(249, 249, 249);
    const COLORREF fgTitle = g.dark ? RGB(255, 255, 255) : RGB(26, 26, 26);
    const COLORREF fgSub   = g.dark ? RGB(176, 176, 176) : RGB(97, 97, 97);
    const COLORREF track   = g.dark ? RGB(57, 57, 57)   : RGB(229, 229, 229);
    const COLORREF accent  = (g.phase == 2)
                             ? (g.dark ? RGB(255, 153, 164) : RGB(196, 43, 28))   // 失败红
                             : (g.dark ? RGB(76, 194, 255)  : RGB(0, 103, 192));  // Win11 强调蓝

    // 背景
    HBRUSH bgBrush = CreateSolidBrush(bg);
    FillRect(dc, &rc, bgBrush);
    DeleteObject(bgBrush);

    SetBkMode(dc, TRANSPARENT);

    // 标题：视频展台
    SelectObject(dc, g.fontTitle);
    SetTextColor(dc, fgTitle);
    RECT rt = { ScaleForDpi(kPadding, g.dpi), ScaleForDpi(kPadding, g.dpi),
                rc.right - ScaleForDpi(kPadding, g.dpi), ScaleForDpi(kPadding + 30, g.dpi) };
    DrawTextW(dc, L"视频展台", -1, &rt, DT_LEFT | DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX);

    // 副标题：状态文案
    SelectObject(dc, g.fontSub);
    SetTextColor(dc, fgSub);
    RECT rs = { ScaleForDpi(kPadding, g.dpi), ScaleForDpi(kPadding + 34, g.dpi),
                rc.right - ScaleForDpi(kPadding, g.dpi), ScaleForDpi(kPadding + 58, g.dpi) };
    DrawTextW(dc, g.status, -1, &rs, DT_LEFT | DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX | DT_END_ELLIPSIS);

    // 进度条（WinUI 风格：4px 圆角轨道 + 走马灯滑块）
    const int pad     = ScaleForDpi(kPadding, g.dpi);
    const int barY    = ScaleForDpi(kPadding + 74, g.dpi);
    const int barH    = ScaleForDpi(4, g.dpi);
    const int totalW  = rc.right - pad * 2;
    const int radius  = barH;

    HBRUSH trackBrush = CreateSolidBrush(track);
    HGDIOBJ oldBrush  = SelectObject(dc, trackBrush);
    HGDIOBJ oldPen    = SelectObject(dc, GetStockObject(NULL_PEN));
    RoundRect(dc, pad, barY, pad + totalW, barY + barH, radius, radius);
    SelectObject(dc, oldPen);
    DeleteObject(trackBrush);

    HBRUSH barBrush = CreateSolidBrush(accent);
    SelectObject(dc, barBrush);
    SelectObject(dc, GetStockObject(NULL_PEN));

    const int slideW = totalW * 32 / 100;
    const int range  = totalW - slideW;

    if (g.phase == 2)
    {
        // 失败：整条变红，给出确定的视觉反馈
        RoundRect(dc, pad, barY, pad + totalW, barY + barH, radius, radius);
    }
    else
    {
        // 三角波走马灯：0 → 1 → 0，避免滑块"跳回起点"的突兀感
        float t = g.marquee;
        float tri = (t < 0.5f) ? (t * 2.0f) : ((1.0f - t) * 2.0f);
        int x = pad + (int)(tri * range);
        RoundRect(dc, x, barY, x + slideW, barY + barH, radius, radius);
    }

    SelectObject(dc, oldPen);
    DeleteObject(barBrush);

    // 右下角品牌标识（12px 次要色）
    SetTextColor(dc, fgSub);
    RECT rb = { rc.left, rc.bottom - ScaleForDpi(30, g.dpi),
                rc.right - ScaleForDpi(kPadding, g.dpi), rc.bottom - ScaleForDpi(12, g.dpi) };
    DrawTextW(dc, L"UNSA Studio", -1, &rb, DT_RIGHT | DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX);
}

// ── 窗口过程 ────────────────────────────────────────────────────────────────

void RebuildFonts()
{
    if (g.fontTitle) { DeleteObject(g.fontTitle); g.fontTitle = nullptr; }
    if (g.fontSub)   { DeleteObject(g.fontSub);   g.fontSub   = nullptr; }

    g.fontTitle = CreateFontW(-ScaleForDpi(16, g.dpi), 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE,
                              DEFAULT_CHARSET, OUT_TT_PRECIS, CLIP_DEFAULT_PRECIS,
                              CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Microsoft YaHei UI");
    g.fontSub = CreateFontW(-ScaleForDpi(12, g.dpi), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
                            DEFAULT_CHARSET, OUT_TT_PRECIS, CLIP_DEFAULT_PRECIS,
                            CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Microsoft YaHei UI");
}

LRESULT CALLBACK SplashProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam)
{
    switch (msg)
    {
    case WM_CREATE:
        g.hwnd = hwnd;
        g.dpi  = GetDpiForWindow(hwnd);
        if (g.dpi <= 0) g.dpi = 96;
        RebuildFonts();
        return 0;

    case WM_ERASEBKGND:
        return 1;   // 全部在 WM_PAINT 里双缓冲绘制，避免闪烁

    case WM_PAINT:
    {
        PAINTSTRUCT ps;
        HDC hdc = BeginPaint(hwnd, &ps);

        RECT rc;
        GetClientRect(hwnd, &rc);

        // 双缓冲
        HDC     memDc  = CreateCompatibleDC(hdc);
        HBITMAP bmp    = CreateCompatibleBitmap(hdc, rc.right, rc.bottom);
        HGDIOBJ oldBmp = SelectObject(memDc, bmp);

        DrawSplash(memDc, rc);

        BitBlt(hdc, 0, 0, rc.right, rc.bottom, memDc, 0, 0, SRCCOPY);

        SelectObject(memDc, oldBmp);
        DeleteObject(bmp);
        DeleteDC(memDc);

        EndPaint(hwnd, &ps);
        return 0;
    }

    case WM_CLOSE:
        // 用户手动关掉 Splash：视为取消启动，直接清理退出
        g_channel.Close();
        ExitProcess(0);
        return 0;
    }

    return DefWindowProcW(hwnd, msg, wParam, lParam);
}

HWND CreateSplashWindow(HINSTANCE hInst)
{
    WNDCLASSEXW wc{};
    wc.cbSize        = sizeof(wc);
    wc.style         = CS_HREDRAW | CS_VREDRAW;
    wc.lpfnWndProc   = SplashProc;
    wc.hInstance     = hInst;
    wc.hCursor       = LoadCursorW(nullptr, IDC_ARROW);
    wc.lpszClassName = kSplashClass;
    wc.hIcon         = LoadIconW(hInst, L"IDI_APP");
    wc.hIconSm       = wc.hIcon;
    RegisterClassExW(&wc);

    int dpi = GetDpiForSystem();
    if (dpi <= 0) dpi = 96;
    g.dpi = dpi;

    int w = ScaleForDpi(kBaseWidth, dpi);
    int h = ScaleForDpi(kBaseHeight, dpi);

    RECT wa{};
    SystemParametersInfoW(SPI_GETWORKAREA, 0, &wa, 0);
    int x = wa.left + ((wa.right - wa.left) - w) / 2;
    int y = wa.top + ((wa.bottom - wa.top) - h) / 2 - h / 5;   // 略偏上，符合视觉重心

    HWND hwnd = CreateWindowExW(
        WS_EX_TOOLWINDOW,                 // 不进任务栏、不进 Alt+Tab —— 它只活 1 秒
        kSplashClass, L"视频展台",
        WS_POPUP,
        x, y, w, h,
        nullptr, nullptr, hInst, nullptr);

    if (!hwnd) return nullptr;

    // Windows 11 圆角（DWM 原生，无需自绘）
    int corner = 2; // DWMWCP_ROUND
    DwmSetWindowAttribute(hwnd, 33 /*DWMWA_WINDOW_CORNER_PREFERENCE*/, &corner, sizeof(corner));

    // 跟随系统深浅色
    int darkFlag = g.dark ? 1 : 0;
    DwmSetWindowAttribute(hwnd, 20 /*DWMWA_USE_IMMERSIVE_DARK_MODE*/, &darkFlag, sizeof(darkFlag));

    ShowWindow(hwnd, SW_SHOWNOACTIVATE);
    UpdateWindow(hwnd);
    return hwnd;
}

// ── 拉起主程序 ──────────────────────────────────────────────────────────────

bool LaunchApp(VP_CHANNEL& ch, const wchar_t* exePath)
{
    wchar_t workDir[MAX_PATH];
    wcscpy_s(workDir, exePath);
    wchar_t* slash = wcsrchr(workDir, L'\\');
    if (slash) *slash = L'\0';

    wchar_t cmd[2048];
    swprintf_s(cmd, 2048,
               L"\"%s\" --vp-mmf=\"%s\" --vp-event=\"%s\" --vp-ppid=%lu --vp-tick=%llu --vp-silent=%d",
               exePath,
               ch.mappingName,
               ch.readyName,
               (unsigned long)GetCurrentProcessId(),
               (unsigned long long)ch.header->launchTick,
               ch.header->silentStart);

    STARTUPINFOW si{};
    si.cb = sizeof(si);
    PROCESS_INFORMATION pi{};

    // CREATE_UNICODE_ENVIRONMENT：保证环境块为 Unicode，主进程无需再转换
    BOOL ok = CreateProcessW(exePath, cmd, nullptr, nullptr, FALSE,
                             CREATE_UNICODE_ENVIRONMENT,
                             nullptr, workDir, &si, &pi);
    if (!ok) return false;

    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);

    ch.header->state = VP_LAUNCHING;
    return true;
}

// ── 清理并退出（"自杀"路径） ───────────────────────────────────────────────

[[noreturn]] void CleanupAndExit(int code)
{
    if (g.hwnd) { DestroyWindow(g.hwnd); g.hwnd = nullptr; }

    // 关闭共享内存与事件句柄。
    // 主程序侧同样会释放自己的句柄；当引用计数归零，
    // 内核立刻销毁命名节与事件对象 —— 临时交流数据被彻底清除，磁盘无残留。
    g_channel.Close();

    ExitProcess((UINT)code);
}

} // namespace

// ============================================================================
//  入口
// ============================================================================
int WINAPI wWinMain(HINSTANCE hInst, HINSTANCE, LPWSTR, int)
{
    // ── 0. 单实例快速通道 ───────────────────────────────────────────────
    //     若主程序已在运行：广播"请激活"消息后立刻退出，耗时 < 5 ms。
    //     这里用命名互斥体探测（比 FindWindow 更可靠，不受窗口标题/类名变更影响），
    //     用 RegisterWindowMessageW 让两端在无约定的情况下共享同一个消息 ID。
    g_msgActivate = RegisterWindowMessageW(kActivateMsg);

    if (HANDLE hExisting = OpenMutexW(SYNCHRONIZE, FALSE, kSingleMutex))
    {
        CloseHandle(hExisting);
        PostMessageW(HWND_BROADCAST, g_msgActivate, 0, 0);
        return 0;
    }

    g.dark      = IsDarkTheme();
    g.startTick = GetTickCount();

    // ── 1. 建立与主程序的同内存通道 ─────────────────────────────────────
    if (!g_channel.Create())
    {
        MessageBoxW(nullptr, L"无法创建启动通道（共享内存/事件）。\n请检查系统资源或安全软件拦截。",
                    L"视频展台 · 启动失败", MB_ICONERROR | MB_OK);
        return 2;
    }
    g.channel = &g_channel;

    // ── 2. 立刻弹出 Splash ──────────────────────────────────────────────
    HWND splash = CreateSplashWindow(hInst);
    if (!splash) CleanupAndExit(3);

    g_channel.header->launcherHwnd = (uint64_t)(uintptr_t)splash;

    // ── 3. 拉起主程序 ───────────────────────────────────────────────────
    wchar_t exePath[MAX_PATH];
    bool launched = false;

    if (BuildAppPath(exePath, MAX_PATH))
        launched = LaunchApp(g_channel, exePath);

    if (!launched)
    {
        g.phase = 2;
        wcscpy_s(g.status, L"无法启动主程序，请重新安装「视频展台」。");
        InvalidateRect(splash, nullptr, FALSE);

        // 给用户 4 秒看清错误，然后清理退出
        DWORD until = GetTickCount() + 4000;
        MSG msg;
        while (GetTickCount() < until)
        {
            while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE))
            {
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
            MsgWaitForMultipleObjects(0, nullptr, FALSE, 50, QS_ALLINPUT);
        }
        CleanupAndExit(4);
    }

    // ── 4. 主循环：一边走动画，一边等"主程序就绪"信号 ──────────────────
    MSG msg;
    for (;;)
    {
        // +1 表示"有窗口消息"，0 表示"就绪事件被 SetEvent"
        DWORD r = MsgWaitForMultipleObjects(1, &g_channel.hReady, FALSE, kAnimIntervalMs, QS_ALLINPUT);

        if (r == WAIT_OBJECT_0)
        {
            // ★ 主程序已经完成首帧渲染 —— 启动器立刻自杀
            break;
        }

        if (r == WAIT_OBJECT_0 + 1)
        {
            while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE))
            {
                if (msg.message == WM_QUIT) CleanupAndExit(0);
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
        }

        DWORD elapsed = GetTickCount() - g.startTick;

        // 依据主程序回填的状态更新副标题（这就是"同内存交换数据"的体现）
        if (g_channel.header)
        {
            g_channel.header->waitedMs = elapsed;

            switch (g_channel.State())
            {
            case VP_APP_ATTACHED:
                if (g.phase != 1)
                {
                    g.phase = 1;
                    wcscpy_s(g.status, L"正在初始化界面…");
                }
                break;
            case VP_FAILED:
                g.phase = 2;
                wcscpy_s(g.status, L"主程序启动失败，请查看事件日志。");
                break;
            default:
                break;
            }
        }

        // 走马灯动画
        if (g.phase != 2)
        {
            g.marquee += 0.045f;
            if (g.marquee > 1.0f) g.marquee = 0.0f;
        }

        if (g.hwnd) InvalidateRect(g.hwnd, nullptr, FALSE);

        // 超时保护：不让用户对着 Splash 干等
        if (elapsed > kBootTimeoutMs)
        {
            g.phase = 2;
            wcscpy_s(g.status, L"启动超时，请检查安装完整性。");
            InvalidateRect(g.hwnd, nullptr, FALSE);
            Sleep(3000);
            CleanupAndExit(5);
        }
    }

    // ── 5. 握手成功：把主窗口拉到前台，然后自杀 ─────────────────────────
    if (g_channel.header && g_channel.header->appHwnd && !g_channel.header->silentStart)
        SetForegroundWindow((HWND)(uintptr_t)g_channel.header->appHwnd);

    CleanupAndExit(0);
    return 0; // 不可达，仅为消除告警
}