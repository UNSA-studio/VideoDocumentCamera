using System.Runtime.InteropServices;
using System.Text;
using VideoPresenter.Shared.Native;

namespace VideoPresenter.Shared.Ipc;

/// <summary>
/// 对 <c>CreateFileMappingW</c> + <c>MapViewOfFile</c> 的托管封装。
/// 支持"创建者"（启动器）与"附加者"（主程序）两种角色，
/// 整个生命周期内共享同一页物理内存 —— 即"同一内存里交换数据"。
/// </summary>
public sealed class SharedMemoryChannel : IDisposable
{
    private IntPtr _hMapping = IntPtr.Zero;
    private IntPtr _pView = IntPtr.Zero;
    private bool _disposed;

    /// <summary>映射名。</summary>
    public string Name { get; }

    /// <summary>是否为本进程创建（true）还是附加到已有映射（false）。</summary>
    public bool IsCreator { get; }

    private SharedMemoryChannel(string name, IntPtr hMapping, IntPtr pView, bool isCreator)
    {
        Name = name;
        _hMapping = hMapping;
        _pView = pView;
        IsCreator = isCreator;
    }

    /// <summary>创建一块新的 4 KB 命名共享内存（启动器使用）。</summary>
    public static SharedMemoryChannel Create(string name)
    {
        IntPtr h = NativeMethods.CreateFileMappingW(
            new IntPtr(-1),               // INVALID_HANDLE_VALUE → 由系统页面文件承载，不落盘
            IntPtr.Zero,
            NativeMethods.PAGE_READWRITE,
            0,
            VpSharedHeader.MappingSize,
            name);

        if (h == IntPtr.Zero)
            throw new IOException($"CreateFileMappingW 失败，Win32Error={Marshal.GetLastWin32Error()}");

        IntPtr view = NativeMethods.MapViewOfFile(h, NativeMethods.FILE_MAP_ALL_ACCESS, 0, 0, VpSharedHeader.MappingSize);
        if (view == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            NativeMethods.CloseHandle(h);
            throw new IOException($"MapViewOfFile 失败，Win32Error={err}");
        }

        // 创建时内核保证零页，这里显式清零以杜绝极小概率的复用脏页
        var zeros = new byte[VpSharedHeader.MappingSize];
        Marshal.Copy(zeros, 0, view, VpSharedHeader.MappingSize);

        return new SharedMemoryChannel(name, h, view, isCreator: true);
    }

    /// <summary>附加到启动器已经创建的共享内存（主程序使用）。</summary>
    public static SharedMemoryChannel Attach(string name)
    {
        IntPtr h = NativeMethods.OpenFileMappingW(NativeMethods.FILE_MAP_ALL_ACCESS, false, name);
        if (h == IntPtr.Zero)
            throw new IOException($"OpenFileMappingW 失败，通道不存在或权限不足，Win32Error={Marshal.GetLastWin32Error()}");

        IntPtr view = NativeMethods.MapViewOfFile(h, NativeMethods.FILE_MAP_ALL_ACCESS, 0, 0, VpSharedHeader.MappingSize);
        if (view == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            NativeMethods.CloseHandle(h);
            throw new IOException($"MapViewOfFile（附加）失败，Win32Error={err}");
        }

        return new SharedMemoryChannel(name, h, view, isCreator: false);
    }

    /// <summary>谨慎重试式附加：等待启动器把映射创建出来（最多 <paramref name="timeoutMs"/>）。</summary>
    public static SharedMemoryChannel AttachWithRetry(string name, int timeoutMs = 5000, int pollMs = 10)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            try
            {
                return Attach(name);
            }
            catch (IOException) when (Environment.TickCount64 < deadline)
            {
                Thread.Sleep(pollMs);
            }
        }
    }

    /// <summary>读取头部结构。</summary>
    public VpSharedHeader ReadHeader()
    {
        ThrowIfDisposed();
        return Marshal.PtrToStructure<VpSharedHeader>(_pView);
    }

    /// <summary>整块写回头部（注意：会覆盖对方刚写入的字段，写前请先 <see cref="ReadHeader"/> 合并）。</summary>
    public void WriteHeader(in VpSharedHeader header)
    {
        ThrowIfDisposed();
        Marshal.StructureToPtr(header, _pView, fDeleteOld: false);
    }

    /// <summary>原子更新若干个字段（读-改-写），用于避免双端互相覆盖。</summary>
    public void Update(Action<VpSharedHeader> mutate)
    {
        ThrowIfDisposed();
        var h = Marshal.PtrToStructure<VpSharedHeader>(_pView);
        mutate(h);
        Marshal.StructureToPtr(h, _pView, fDeleteOld: false);
    }

    /// <summary>读取任意偏移处的 UTF-16 字符串（用于扩展载荷区）。</summary>
    public string ReadString(int offset, int maxChars = 256)
    {
        ThrowIfDisposed();
        return Marshal.PtrToStringUni(_pView + offset, maxChars) ?? string.Empty;
    }

    /// <summary>向任意偏移写入 UTF-16 字符串（截断到 <paramref name="maxChars"/>）。</summary>
    public void WriteString(int offset, string value, int maxChars = 256)
    {
        ThrowIfDisposed();
        var bytes = Encoding.Unicode.GetBytes(value);
        int len = Math.Min(bytes.Length, (maxChars - 1) * 2);
        Marshal.Copy(bytes, 0, _pView + offset, len);
        Marshal.WriteInt16(_pView + offset + len, 0);
    }

    /// <summary>共享内存视图基址，供不安全的直接访问 / 结构化载荷使用。</summary>
    public IntPtr View => _pView;

    /// <summary>写入一段原始字节到载荷区（例如把启动器采集的环境快照带过来）。</summary>
    public void WritePayload(ReadOnlySpan<byte> data)
    {
        ThrowIfDisposed();
        int len = Math.Min(data.Length, VpSharedHeader.PayloadSize);
        var tmp = new byte[len];
        data[..len].CopyTo(tmp);
        Marshal.Copy(tmp, 0, _pView + VpSharedHeader.PayloadOffset, len);
    }

    /// <summary>从载荷区读取一段字节。</summary>
    public byte[] ReadPayload(int length)
    {
        ThrowIfDisposed();
        int len = Math.Min(length, VpSharedHeader.PayloadSize);
        var buf = new byte[len];
        Marshal.Copy(_pView + VpSharedHeader.PayloadOffset, buf, 0, len);
        return buf;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_pView != IntPtr.Zero)
        {
            NativeMethods.UnmapViewOfFile(_pView);
            _pView = IntPtr.Zero;
        }
        if (_hMapping != IntPtr.Zero)
        {
            // 最后一个句柄关闭时，内核自动销毁该命名节 —— 临时交流数据零残留。
            NativeMethods.CloseHandle(_hMapping);
            _hMapping = IntPtr.Zero;
        }
    }
}

/// <summary>命名事件封装（自动重置 / 手动重置）。</summary>
public sealed class NamedSignal : IDisposable
{
    private IntPtr _handle;
    private bool _disposed;

    public string Name { get; }
    public bool IsCreator { get; }

    private NamedSignal(string name, IntPtr handle, bool isCreator)
    {
        Name = name;
        _handle = handle;
        IsCreator = isCreator;
    }

    public static NamedSignal Create(string name, bool manualReset = true)
    {
        IntPtr h = NativeMethods.CreateEventW(IntPtr.Zero, manualReset, false, name);
        if (h == IntPtr.Zero)
            throw new IOException($"CreateEventW 失败，Win32Error={Marshal.GetLastWin32Error()}");
        return new NamedSignal(name, h, true);
    }
public static NamedSignal Open(string name)
    {
        // ⚠ 这里【绝不能】用 FILE_MAP_ALL_ACCESS！
        //
        //   FILE_MAP_ALL_ACCESS 是"文件映射对象"的访问权限常量，
        //   用在"事件对象"上会被内核直接拒绝（ERROR_ACCESS_DENIED）。
        //
        //   一旦这里失败，主程序就无法打开启动器创建的就绪事件 ——
        //   于是整条握手链路静默断掉：主程序照常启动，却永远不会 SetEvent，
        //   启动器只能一直干等。
        //
        //   事件对象正确的访问权限是 EVENT_MODIFY_STATE（用于 SetEvent）
        //   加上 SYNCHRONIZE（用于等待）。
        IntPtr h = NativeMethods.OpenEventW(
            NativeMethods.EVENT_MODIFY_STATE | NativeMethods.SYNCHRONIZE, false, name);

        if (h == IntPtr.Zero)
            throw new IOException($"OpenEventW 失败，Win32Error={Marshal.GetLastWin32Error()}");

        return new NamedSignal(name, h, false);
    }

    public static NamedSignal OpenWithRetry(string name, int timeoutMs = 5000, int pollMs = 10)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            try { return Open(name); }
            catch (IOException) when (Environment.TickCount64 < deadline) { Thread.Sleep(pollMs); }
        }
    }

    /// <summary>触发事件，唤醒等待方（握手完成信号）。</summary>
    public void Set()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeMethods.SetEvent(_handle);
    }

    /// <summary>等待信号，返回是否在超时前收到。</summary>
    public bool WaitOne(int millisecondsTimeout)
        => NativeMethods.WaitForSingleObject(_handle, (uint)millisecondsTimeout) == 0;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }
}