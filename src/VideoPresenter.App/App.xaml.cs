// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// Required Notice: Copyright (C) 2026 UNSA Studio
//
// 本文件属于视频展台（VideoPresenter）。分发时请随文件一并提供许可条款：
// https://polyformproject.org/licenses/noncommercial/1.0.0
//
using System.Diagnostics;
using System.Threading;
using Microsoft.UI.Xaml;
using VideoPresenter.App.Boot;
using VideoPresenter.App.Services;

namespace VideoPresenter.App;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\UNSA.VP.SingleInstance";

    private Mutex? _singleInstanceMutex;
    private MainWindow? _window;

    /// <summary>主程序首帧耗时（毫秒）。</summary>
    public static long BootElapsedMs { get; private set; }

    public App()
    {
        InitializeComponent();

        // 未处理异常兜底：回报给启动器，避免 Splash 干等
        UnhandledException += (_, e) =>
        {
            Debug.WriteLine($"[VP] 未处理异常：{e.Exception}");
            BootSignal.Instance.ReportFailure(e.Exception.Message);
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // ── ① 线程池预热 ───────────────────────────────────────────────────
        ThreadPool.GetMinThreads(out _, out int ioMin);
        ThreadPool.SetMinThreads(8, Math.Max(ioMin, 8));

        // ── ② 立刻与启动器建立握手（必须早于任何 UI 构造）──────────────────
        //     unpackaged 模式下 LaunchActivatedEventArgs.Arguments 为空，
        //     因此直接读进程命令行。
        BootSignal.Instance.Attach(Environment.GetCommandLineArgs());

        // ── ③ 单实例 ───────────────────────────────────────────────────────
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            WindowApiService.BroadcastActivate();
            Environment.Exit(0);
            return;
        }

        // ── ④ 建主窗口 ─────────────────────────────────────────────────────
        //  任何异常都必须让用户【看得见】，而不是静默闪退。
        //  在窗口创建出来之前，WinUI 的对话框一个都用不了，
        //  所以这里用 Win32 的 MessageBox 兜底。
        try
        {
            _window = new MainWindow();
            _window.Closed += (_, _) =>
            {
                BootSignal.Instance.Dispose();
                _singleInstanceMutex?.Dispose();
            };

            _window.Activate();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VP] 主窗口创建失败：{ex}");
            BootSignal.Instance.ReportFailure(ex.Message);
            ShowFatalError(ex);
        }
    }

    private static void ShowFatalError(Exception ex)
    {
        string text =
            "视频展台启动失败。\n\n" +
            ex.GetType().Name + "：" + ex.Message + "\n\n" +
            "技术详情（可截图反馈）：\n" +
            (ex.StackTrace ?? "(无调用栈)");

        MessageBoxW(IntPtr.Zero, text, "视频展台 · 启动失败", 0x00000010 /* MB_ICONERROR */);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    /// <summary>由 MainWindow 在首帧渲染完成后回调。</summary>
    internal static void ReportWindowReady(Window window)
    {
        BootElapsedMs = BootSignal.Instance.ElapsedMs;
        BootSignal.Instance.NotifyWindowReady(window);

        Debug.WriteLine(BootSignal.Instance.HasSession
            ? $"[VP-Boot] 主窗口就绪，端到端 {BootElapsedMs} ms（启动器 → WinUI 3 首帧）"
            : "[VP-Boot] 开发模式运行，无启动器计时");
    }
}