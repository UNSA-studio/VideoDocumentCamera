using System.Runtime.InteropServices;

namespace VideoPresenter.App.Services;

/// <summary>
/// 系统提示音。
///
/// <para><b>为什么不用 Console.Beep</b></para>
/// <para>
/// <c>Console.Beep</c> 走的是 PC 蜂鸣器（很多现代主板已经取消），
/// 而且会阻塞调用线程；用 Win32 的 <c>MessageBeep</c> 播放的是系统声音方案里
/// 的提示音 —— 用户换了系统主题 / 声音方案，音色会跟着变，符合"跟随系统"的一贯做法。
/// </para>
/// </summary>
internal static class AppSounds
{
    private const uint MB_OK = 0x00000000;

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MessageBeep(uint uType);

    /// <summary>拍照提示音。</summary>
    public static void Capture() => MessageBeep(MB_OK);
}