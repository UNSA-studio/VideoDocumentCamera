// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// Required Notice: Copyright (C) 2026 UNSA Studio
//
// 本文件属于视频展台（VideoPresenter）。分发时请随文件一并提供许可条款：
// https://polyformproject.org/licenses/noncommercial/1.0.0
//
using System.Runtime.InteropServices;

namespace VideoPresenter.App.Services;

/// <summary>
/// 主程序侧所需的 Win32 声明。
/// <para>
/// WinUI 3 已经接管了绝大部分 UI 工作；这里只补系统资源覆盖不到的部分：
/// 进程间广播（单实例激活）、全局热键、DWM 窗口属性、窗口过程子类化。
/// </para>
/// </summary>
internal static class Win32
{
    public static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    public const int WM_HOTKEY = 0x0312;

    // ─────────────────────────── DWM 窗口属性 ───────────────────────────

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    public const int DWMWCP_DEFAULT = 0;
    public const int DWMWCP_DONOTROUND = 1;
    public const int DWMWCP_ROUND = 2;
    public const int DWMWCP_ROUNDSMALL = 3;

    // ──────────────────────────── 热键修饰符 ────────────────────────────

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    // ──────────────────────────── P/Invoke ──────────────────────────────

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    // ─────────────────────── 窗口过程子类化（接收 WM_HOTKEY） ───────────────────────
    //  WinUI 3 没有 WPF 的 HwndSource，因此用 comctl32 的 SetWindowSubclass。

    public delegate IntPtr SUBCLASSPROC(
        IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam,
        UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass,
                                                UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RemoveWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass,
                                                   UIntPtr uIdSubclass);

    [DllImport("comctl32.dll")]
    public static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
}