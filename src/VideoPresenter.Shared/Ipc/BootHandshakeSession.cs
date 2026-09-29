using System.Diagnostics;

namespace VideoPresenter.Shared.Ipc;

/// <summary>
/// 主程序侧的启动握手会话。
///
/// <para>生命周期：</para>
/// <list type="number">
///   <item>附加到启动器创建的共享内存与就绪事件；</item>
///   <item>写入 <see cref="VpBootState.AppAttached"/>（启动器据此把 Splash 文案切到"正在初始化界面…"）；</item>
///   <item>主窗口首次 <c>ContentRendered</c> 时调用 <see cref="ReportReady"/>；</item>
///   <item>写入 <see cref="VpBootState.AppReady"/> + 窗口句柄 + 耗时，然后 SetEvent；</item>
///   <item>启动器收到后自杀，主程序 <see cref="Dispose"/> 释放自己的句柄 → 临时数据被内核回收。</item>
/// </list>
/// </summary>
public sealed class BootHandshakeSession : IDisposable
{
    private readonly SharedMemoryChannel _channel;
    private readonly NamedSignal _readySignal;
    private bool _disposed;

    /// <summary>启动器传入的参数。</summary>
    public BootArguments Arguments { get; }

    /// <summary>握手通道（共享内存）。</summary>
    public SharedMemoryChannel Channel => _channel;

    /// <summary>从启动器创建到当前时刻的耗时（毫秒）。</summary>
    public long ElapsedSinceLaunchMs
        => Arguments.LaunchTick == 0
            ? 0
            : (long)(Native.NativeMethods.GetTickCount64() - Arguments.LaunchTick);

    private BootHandshakeSession(BootArguments args, SharedMemoryChannel channel, NamedSignal ready)
    {
        Arguments = args;
        _channel = channel;
        _readySignal = ready;
    }

    /// <summary>
    /// 附加到启动器建立的握手通道。若当前不是由启动器拉起（例如开发期直接 F5 调试），返回 <c>null</c>。
    /// </summary>
    public static BootHandshakeSession? TryAttach(BootArguments args)
    {
        if (!args.HasHandshake) return null;

        try
        {
            var channel = SharedMemoryChannel.AttachWithRetry(args.MemoryMapName!, timeoutMs: 3000);
            var signal = NamedSignal.OpenWithRetry(args.ReadyEventName!, timeoutMs: 3000);

            var session = new BootHandshakeSession(args, channel, signal);
            session.Validate();
            return session;
        }
        catch (IOException ex)
        {
            Debug.WriteLine($"[VP-Boot] 握手通道附加失败：{ex.Message}");
            return null;
        }
    }

    private void Validate()
    {
        var header = _channel.ReadHeader();

        if (header.Magic != VpSharedHeader.MagicValue)
            throw new IOException($"共享内存魔数校验失败：0x{header.Magic:X8}（期望 0x{VpSharedHeader.MagicValue:X8}）");

        if (header.Version != VpSharedHeader.ProtocolVersion)
            throw new IOException($"协议版本不匹配：通道={header.Version}，本程序={VpSharedHeader.ProtocolVersion}。请重新安装完整包。");
    }

    /// <summary>第 2 步：告知启动器"主程序已加载，正在建界面"。</summary>
    public void ReportAttached()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _channel.Update(h =>
        {
            h.AppPid = Native.NativeMethods.GetCurrentProcessId();
            h.SilentStart = Arguments.Silent ? 1 : 0;
            h.State = (int)VpBootState.AppAttached;
            h.Message = "正在初始化界面…";
        });
    }

    /// <summary>第 4 步：主窗口已可见 —— 通知启动器可以自杀了。</summary>
    public void ReportReady(IntPtr mainWindowHandle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long elapsed = ElapsedSinceLaunchMs;

        _channel.Update(h =>
        {
            h.AppPid = Native.NativeMethods.GetCurrentProcessId();
            h.AppHwnd = unchecked((ulong)mainWindowHandle.ToInt64());
            h.ReadyTick = Native.NativeMethods.GetTickCount64();
            h.State = (int)VpBootState.AppReady;
            h.Message = "就绪";
        });

        // 通知启动器：立刻自杀
        _readySignal.Set();

        Debug.WriteLine($"[VP-Boot] 握手完成，端到端耗时 {elapsed} ms（含 CreateProcess + WPF 首帧）");
    }

    /// <summary>报告失败，让启动器显示错误而不是干等。</summary>
    public void ReportFailure(string reason)
    {
        if (_disposed) return;
        _channel.Update(h =>
        {
            h.State = (int)VpBootState.Failed;
            h.Message = reason.Length > 120 ? reason[..120] : reason;
        });
        _readySignal.Set();
    }

    /// <summary>读取启动器实时刷新的"已等待毫秒数"，用于启动耗时埋点。</summary>
    public uint ReadLauncherWaitedMs() => _channel.ReadHeader().WaitedMs;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 关闭本进程句柄。启动器已经在收到 AppReady 后退出并关闭其句柄，
        // 此刻引用计数归零 → 命名节与事件由内核销毁 → 临时交流数据彻底清除。
        _readySignal.Dispose();
        _channel.Dispose();
    }
}