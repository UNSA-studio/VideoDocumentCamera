// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// Required Notice: Copyright (C) 2026 UNSA Studio
//
// 本文件属于视频展台（VideoPresenter）。分发时请随文件一并提供许可条款：
// https://polyformproject.org/licenses/noncommercial/1.0.0
//
using System.Diagnostics;
using System.IO;
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
        try
        {
            AppPaths.Initialize();
            AppLog.Initialize();
            if (AppPaths.FallbackReason is not null)
            {
                AppLog.Write("[VP] 数据目录回退：" + AppPaths.DescribeLayout(), "WARN", "Paths");
                ShowDataDirNotice();
            }
            else
            {
                AppLog.Write("[VP] " + AppPaths.DescribeLayout(), "INFO", "Paths");
            }
        }
        catch (Exception ex)
        {
            ShowFatalError(new IOException(
                "无法在以下任一位置创建数据目录：\n" +
                "  ① " + Path.Combine(AppPaths.InstallRoot, "Users") + "（安装目录，可能没有写权限）\n" +
                "  ② " + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                        "UNSA Studio", "VideoDocumentCamera", "Data", "Users") + "\n" +
                "  ③ " + Path.Combine(Path.GetTempPath(), "VideoDocumentCamera", "Users") + "\n" +
                "请把程序放到你有写权限的目录，或用安装包正常安装后重试。", ex));
            throw;
        }
        UnhandledException += (_, e) =>
        {
            AppLog.Fatal("WinUI.UnhandledException", e.Exception);
            BootSignal.Instance.ReportFailure(e.Exception.Message);
            // Do not mask an unknown fatal UI fault and keep running in a corrupt state.
            e.Handled = false;
        };
        try { InitializeComponent(); }
        catch (Exception ex) { AppLog.Fatal("InitializeComponent", ex); throw; }
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
            AppLog.CompleteSession();
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
                AppLog.CompleteSession();
            };

            _window.Activate();
        }
        catch (Exception ex)
        {
            AppLog.Fatal("Create MainWindow", ex);
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

    /// <summary>
    /// 数据目录回退时的提示。不是错误——程序照常可用，
    /// 只是要明确告诉用户文件落在哪里，免得找不到照片和日志。
    /// </summary>
    private static void ShowDataDirNotice()
    {
        string text =
            "安装目录不可写，已自动改用用户数据目录。\n\n" +
            "数据位置：" + AppPaths.UsersRoot + "\n" +
            "原因：" + AppPaths.FallbackReason + "\n\n" +
            "程序可正常使用，照片、扫描件、录像与日志都保存在上述位置。\n" +
            "若希望数据跟随程序目录，请把程序安装到你有写权限的文件夹。";

        MessageBoxW(IntPtr.Zero, text, "视频展台 · 数据目录已切换", 0x00000040 /* MB_ICONINFORMATION */);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    /// <summary>由 MainWindow 在首帧渲染完成后回调。</summary>
    internal static void ReportWindowReady(Window window)
    {
        BootElapsedMs = BootSignal.Instance.ElapsedMs;
        BootSignal.Instance.NotifyWindowReady(window);

        AppLog.Write(BootSignal.Instance.HasSession
            ? $"[VP-Boot] 主窗口就绪，端到端 {BootElapsedMs} ms（启动器 → WinUI 3 首帧）"
            : "[VP-Boot] 开发模式运行，无启动器计时");
    }
}