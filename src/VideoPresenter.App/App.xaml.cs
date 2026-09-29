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
        _window = new MainWindow();
        _window.Closed += (_, _) =>
        {
            BootSignal.Instance.Dispose();
            _singleInstanceMutex?.Dispose();
        };

        _window.Activate();
    }

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