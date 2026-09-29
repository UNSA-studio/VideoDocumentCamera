using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace VideoPresenter.App.Services;

/// <summary>
/// 窗口外观、系统级交互与进程协作。
///
/// <para>全部走 Windows 原生 API。在 WinUI 3 中，<b>配色、深浅色、强调色
/// 完全由系统主题资源负责</b>，本类只处理"系统主题资源覆盖不到"的部分：</para>
/// <list type="bullet">
///   <item>窗口圆角（DWM，Win11）；</item>
///   <item>标题栏深色状态（让系统绘制的窗口边框 / 阴影与内容一致）；</item>
///   <item>单实例激活广播；</item>
///   <item>全局热键。</item>
/// </list>
/// </summary>
public static class WindowApiService
{
    /// <summary>
    /// 跨进程"请激活你自己"消息。
    /// <c>RegisterWindowMessage</c> 保证同名注册在所有进程中返回同一个消息 ID。
    /// </summary>
    public static readonly uint WM_VP_ACTIVATE = Win32.RegisterWindowMessageW("UNSA.VP.Activate");

    public const int HotKeyCapture = 0x9001;
    public const int HotKeyFullScreen = 0x9002;

    // ───────────────────────── 单实例激活 ─────────────────────────

    public static void BroadcastActivate()
        => Win32.PostMessageW(Win32.HWND_BROADCAST, WM_VP_ACTIVATE, IntPtr.Zero, IntPtr.Zero);

    // ───────────────────────── 窗口外观 ─────────────────────────

    /// <summary>
    /// 应用 Win11 Fluent 窗口外观。
    /// <para>
    /// WinUI 3 的 <c>MicaBackdrop</c> 负责材质与主题跟随；
    /// 这里额外补上"原生圆角"和"系统标题栏深色状态"，
    /// 让 DWM 绘制的窗口阴影 / 边框与内容保持一致。
    /// </para>
    /// </summary>
    /// <param name="hwnd">窗口句柄（WinUI: WinRT.Interop.WindowNative.GetWindowHandle）。</param>
    /// <param name="dark">当前是否深色（用于同步 DWM 的非客户区）。</param>
    public static void ApplyFluentChrome(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero) return;

        int corner = Win32.DWMWCP_ROUND;
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        SetImmersiveDarkMode(hwnd, dark);
    }

    /// <summary>同步 DWM 非客户区的深浅色状态。</summary>
    public static void SetImmersiveDarkMode(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero) return;
        int value = dark ? 1 : 0;
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }

    /// <summary>
    /// 判断系统当前是否为深色主题。
    /// <para>
    /// 仅用于两件事：① 同步 DWM 非客户区；② 在状态栏显示"跟随系统（深色/浅色）"。
    /// 界面配色本身由 WinUI 的主题资源自动处理，不依赖这个判断。
    /// </para>
    /// </summary>
    public static bool IsSystemDarkTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    // ───────────────────────── 全局热键 ─────────────────────────

    public static bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey)
        => Win32.RegisterHotKey(hwnd, id, modifiers | Win32.MOD_NOREPEAT, virtualKey);

    public static void UnregisterHotKey(IntPtr hwnd, int id)
        => Win32.UnregisterHotKey(hwnd, id);
}