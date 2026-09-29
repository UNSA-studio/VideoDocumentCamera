using System.Diagnostics;
using Microsoft.UI.Xaml;
using VideoPresenter.Shared.Ipc;

namespace VideoPresenter.App.Boot;

/// <summary>
/// 把主程序的 WinUI 生命周期接到启动握手协议上。
/// （与 UI 框架无关，仅把窗口句柄的获取方式换成 WinRT.Interop。）
/// </summary>
public sealed class BootSignal : IDisposable
{
    private static readonly Lazy<BootSignal> LazyInstance = new(() => new BootSignal());
    public static BootSignal Instance => LazyInstance.Value;

    private BootHandshakeSession? _session;
    private bool _reported;

    /// <summary>是否为"启动器拉起"（false 表示开发者直接运行）。</summary>
    public bool HasSession => _session is not null;

    /// <summary>端到端启动耗时（毫秒）。</summary>
    public long ElapsedMs { get; private set; }

    /// <summary>启动器传入的参数。</summary>
    public BootArguments Arguments { get; private set; } = new();

    /// <summary>启动器进程 PID（0 表示非启动器拉起）。</summary>
    public uint LauncherPid => Arguments.ParentPid;

    private BootSignal() { }

    public void Attach(string[] commandLineArgs)
    {
        Arguments = BootArguments.Parse(commandLineArgs);

        _session = BootHandshakeSession.TryAttach(Arguments);
        if (_session is null)
        {
            Debug.WriteLine("[VP-Boot] 未检测到启动器通道（开发模式运行）。");
            return;
        }

        _session.ReportAttached();
        Debug.WriteLine("[VP-Boot] 已附加到启动器通道，回写 AppAttached。");
    }

    /// <summary>主窗口首帧渲染完成后调用 —— 通知启动器"可以自杀了"。</summary>
    public void NotifyWindowReady(Window window)
    {
        if (_reported || _session is null) return;
        _reported = true;

        // WinUI 3 取窗口句柄的标准方式
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        ElapsedMs = _session.ElapsedSinceLaunchMs;

        _session.ReportReady(hwnd);
    }

    public void ReportFailure(string reason) => _session?.ReportFailure(reason);

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
    }
}