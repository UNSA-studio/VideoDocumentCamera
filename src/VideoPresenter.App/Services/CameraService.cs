using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Graphics.Imaging;

namespace VideoPresenter.App.Services;

/// <summary>视频展台设备（高拍仪 / USB 摄像头 / 采集卡 …）。</summary>
public sealed record CameraDevice(int Index, string Name, string SymbolicLink)
{
    public override string ToString() => Name;
}

/// <summary>采集服务抽象。</summary>
public interface ICameraService : IDisposable
{
    IReadOnlyList<CameraDevice> Devices { get; }
    bool IsRunning { get; }
    double Fps { get; }

    /// <summary>每来一帧触发。参数为<b>复用</b>的 SoftwareBitmap，请尽快使用，勿长期持有。</summary>
    event EventHandler<SoftwareBitmap>? FrameArrived;

    event EventHandler<string>? StatusChanged;

    void RefreshDevices();

    bool Start(CameraDevice device);

    void Stop();

    /// <summary>取当前帧的<b>副本</b>（拍照用，调用方负责 Dispose）。</summary>
    SoftwareBitmap? GrabStill();

    /// <summary>
    /// 取当前帧的 BGRA32 原始像素（录像用）。
    /// <para>每次调用都会新建一个数组，调用方负责回收。</para>
    /// </summary>
    byte[]? GrabFrameBytes();
}

/// <summary>
/// 基于 <b>Windows Media Foundation</b> 的采集实现（WinUI 3 版）。
///
/// <para>选型理由（对比传统方案）：</para>
/// <list type="bullet">
///   <item><c>MFEnumDeviceSources</c> 直接向系统设备栈要设备，无需构建 DirectShow 滤镜图；</item>
///   <item><c>IMFSourceReader</c> 拉模式取帧，按需取帧、掉帧自动追；</item>
///   <item><c>MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING</c> 让 MF 内建色彩转换单元完成 BGRA 输出。</item>
/// </list>
/// </summary>
public sealed class MediaFoundationCameraService : ICameraService
{
    // ───────────────────────── Media Foundation 常量 ─────────────────────────

    private const uint MF_SOURCE_READER_ALL_STREAMS = 0xFFFFFFFE;
    private const uint MF_SOURCE_READER_FIRST_VIDEO_STREAM = 0xFFFFFFFC;
    private const uint MF_SOURCE_READERF_ENDOFSTREAM = 0x00000002;

    private static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE = new("C60AC5FE-252A-478F-A0EF-BC8FA5F7CAD3");
    private static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID = new("8AC3587A-4AE7-42D0-99E0-0A6013EEF90B");
    private static readonly Guid MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME = new("60D0E559-52F8-4FA2-BBCE-ACDB34A8EC01");
    private static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK = new("58F0AAD8-22BF-4F8A-BB3D-D2C4978C6E2F");
    private static readonly Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING = new("FB394F3D-CCF1-42EE-BBB3-F9B845D5681D");

    private static readonly Guid MF_MT_MAJOR_TYPE = new("48EBA18E-F8C9-4687-BF11-0A74CD669FA8");
    private static readonly Guid MF_MT_SUBTYPE = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
    private static readonly Guid MF_MT_FRAME_SIZE = new("1652C33D-D6B2-4012-B834-72030849A37D");
    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00AA00389B71");
    private static readonly Guid IID_IMFMediaSource = new("80B3FFD1-C067-4B48-B4F4-72D3F2B7D3D3");

    private const int MF_VERSION = 0x0002_0070;

    // ───────────────────────────── 状态 ─────────────────────────────

    private readonly object _gate = new();
    private readonly List<CameraDevice> _devices = new();

    /// <summary>
    /// Media Foundation 是否可用。
    /// <para>
    /// Windows N / KN 版（欧洲版）以及部分精简版系统【不包含 Media Foundation】，
    /// 此时相机功能整体降级为"不可用"，但应用本身必须照常运行。
    /// </para>
    /// </summary>
    private readonly bool _mfAvailable;

    /// <summary>Media Foundation 是否可用（供界面提示用）。</summary>
    public bool IsAvailable => _mfAvailable;

    private IMFSourceReader? _reader;
    private Thread? _readThread;
    private volatile bool _running;
    private int _width, _height;

    /// <summary>复用的目标位图（避免每帧分配 8 MB 的托管缓冲）。</summary>
    private SoftwareBitmap? _bitmap;

    private long _frameCount;
    private long _fpsWindowStart;
    private double _fps;

    public IReadOnlyList<CameraDevice> Devices => _devices;
    public bool IsRunning => _running;
    public double Fps => _fps;

    public event EventHandler<SoftwareBitmap>? FrameArrived;
    public event EventHandler<string>? StatusChanged;

    // ══════════════════════════ 诊断日志 ══════════════════════════

    /// <summary>
    /// 日志文件路径（供界面提示 / 用户反馈用）。
    /// <para>%LOCALAPPDATA%\UNSA Studio\VideoDocumentCamera\logs\vdc.log</para>
    /// </summary>
    public static string LogFilePath
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UNSA Studio", "VideoDocumentCamera", "logs");
            return Path.Combine(dir, "vdc.log");
        }
    }

    /// <summary>
    /// 同时写 Debug 输出与日志文件。
    /// <para>
    /// 为什么要有文件日志：相机枚举失败的原因（系统缺 Media Foundation、
    /// 设备被占用、驱动异常……）在客户机上无法用调试器看。
    /// 有了这个文件，用户直接把日志发回来就能定位。
    /// </para>
    /// </summary>
    private static void Log(string message)
    {
        Debug.WriteLine(message);

        try
        {
            string file = LogFilePath;

            var fi = new FileInfo(file);
            if (fi.Exists && fi.Length > 1024 * 1024)
            {
                fi.Delete();   // 只保留最近 1 MB
            }

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.AppendAllText(file,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败绝不能影响主流程
        }
    }

    public MediaFoundationCameraService()
    {
        // ⚠ 这里【绝对不能抛异常】。
        //
        //  本对象是在 MainWindow 的构造函数里创建的 —— 一旦抛异常，
        //  整个主程序会在窗口创建之前就崩掉，用户看到的就是"双击没反应"。
        //
        //  Windows N / KN 版（欧洲版）与部分精简版系统不包含 Media Foundation，
        //  MFStartup 会失败；此时应当降级为"相机不可用"，其余功能照常。
        try
        {
            int hr = MFStartup(MF_VERSION, 0);
            _mfAvailable = hr >= 0;

            if (_mfAvailable)
            {
                Debug.WriteLine("[VP-MF] Media Foundation 已启动");
            }
            else
            {
                Debug.WriteLine($"[VP-MF] Media Foundation 不可用，HRESULT=0x{hr:X8}（相机功能已禁用）");
            }
        }
        catch (Exception ex)
        {
            // 例如 mfplat.dll 加载失败（Windows N 版根本没这个组件）
            _mfAvailable = false;
            Debug.WriteLine($"[VP-MF] Media Foundation 初始化异常：{ex.Message}（相机功能已禁用）");
        }
    }

    // ══════════════════════════ 设备枚举 ══════════════════════════

    public void RefreshDevices()
    {
        if (!_mfAvailable)
        {
            lock (_gate) { _devices.Clear(); }
            StatusChanged?.Invoke(this, "本系统未提供 Media Foundation 组件，无法枚举视频设备");
            return;
        }

        lock (_gate)
        {
            _devices.Clear();

            IMFAttributes? attributes = null;

            try
            {
                if (MFCreateAttributes(out attributes, 1) < 0)
                    throw new InvalidOperationException("MFCreateAttributes 失败");

                Guid srcTypeKey = MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE;
                Guid vidcapValue = MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID;
                attributes.SetGUID(ref srcTypeKey, ref vidcapValue);

                // ── 枚举设备（手动遍历指针数组，见下方 P/Invoke 处的说明）──
                int hr = MFEnumDeviceSources(attributes, out IntPtr pArray, out uint count);

                Log($"[VP-MF] MFEnumDeviceSources → hr=0x{hr:X8}, count={count}, array=0x{pArray.ToInt64():X}");

                if (hr < 0)
                    throw new InvalidOperationException($"MFEnumDeviceSources 失败，HRESULT=0x{hr:X8}");

                if (pArray == IntPtr.Zero || count == 0)
                {
                    Log("[VP-MF] 系统返回 0 个视频设备（机器上可能确实没有摄像头/展台）");
                }
                else
                {
                    try
                    {
                        for (uint i = 0; i < count; i++)
                        {
                            IntPtr pActivate = Marshal.ReadIntPtr(pArray, (int)i * IntPtr.Size);
                            if (pActivate == IntPtr.Zero) continue;

                            var activate = (IMFActivate)Marshal.GetObjectForIUnknown(pActivate);
                            try
                            {
                                string name = GetActivateString(activate, MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME);
                                string link = GetActivateString(activate, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK);

                                Log($"[VP-MF] 设备 {i}：\"{name}\"");

                                _devices.Add(new CameraDevice(
                                    (int)i,
                                    string.IsNullOrWhiteSpace(name) ? $"视频设备 {i + 1}" : name,
                                    link));
                            }
                            finally
                            {
                                Marshal.ReleaseComObject(activate);  // 抵掉 GetObjectForIUnknown 的 AddRef
                                Marshal.Release(pActivate);          // 释放 MF 返回的那份引用
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(pArray);               // 释放指针数组本身
                    }
                }

                Log($"[VP-MF] 枚举完成，共 {_devices.Count} 个视频设备");
            }
            catch (Exception ex)
            {
                Log($"[VP-MF] 设备枚举失败：{ex}");
            }
            finally
            {
                if (attributes is not null) Marshal.ReleaseComObject(attributes);
            }

            StatusChanged?.Invoke(this, _devices.Count > 0
                ? $"已发现 {_devices.Count} 个视频设备"
                : "未发现视频设备（请检查设备连接与驱动）");
        }
    }

    private static string GetActivateString(IMFActivate activate, Guid key)
    {
        try
        {
            Guid k = key;
            activate.GetStringLength(ref k, out uint len);
            if (len == 0) return string.Empty;

            var sb = new StringBuilder((int)len + 1);
            activate.GetString(ref k, sb, (uint)sb.Capacity, out _);
            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    // ══════════════════════════ 打开 / 关闭 ══════════════════════════

    public bool Start(CameraDevice device)
    {
        if (!_mfAvailable)
        {
            return Fail("本系统未提供 Media Foundation 组件，无法打开视频设备");
        }

        Stop();

        lock (_gate)
        {
            IntPtr source = IntPtr.Zero;
            IMFAttributes? attributes = null;

            try
            {
                if (MFCreateAttributes(out attributes, 1) < 0)
                    return Fail("MFCreateAttributes 失败");

                Guid srcTypeKey = MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE;
                Guid vidcapValue = MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID;
                attributes.SetGUID(ref srcTypeKey, ref vidcapValue);

                // 同样手动遍历指针数组（理由见 MFEnumDeviceSources 的 P/Invoke 说明）
                int hr = MFEnumDeviceSources(attributes, out IntPtr pArray, out uint count);

                if (hr < 0 || pArray == IntPtr.Zero || device.Index < 0 || device.Index >= (int)count)
                {
                    Marshal.FreeCoTaskMem(pArray);   // 传 Zero 是安全的 no-op
                    return Fail($"无法再次枚举到该视频设备（索引 {device.Index}，共 {count} 个）");
                }

                Guid iidSource = IID_IMFMediaSource;
                try
                {
                    IntPtr pActivate = Marshal.ReadIntPtr(pArray, device.Index * IntPtr.Size);
                    var activate = (IMFActivate)Marshal.GetObjectForIUnknown(pActivate);
                    try
                    {
                        hr = activate.ActivateObject(ref iidSource, out source);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(activate);
                        Marshal.Release(pActivate);
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pArray);
                }

                Marshal.ReleaseComObject(attributes);
                attributes = null;

                if (hr < 0 || source == IntPtr.Zero)
                    return Fail("设备可能已被其它程序占用（如相机应用 / 其它展台软件）");

                // SourceReader + MF 内建视频处理（色彩转换 / 缩放）
                var readerAttrs = CreateAttributesWith(MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, 1);

                hr = MFCreateSourceReaderFromMediaSource(source, readerAttrs, out _reader);
                Marshal.ReleaseComObject(readerAttrs);
                Marshal.Release(source);
                source = IntPtr.Zero;

                if (hr < 0 || _reader is null) return Fail($"创建 SourceReader 失败 0x{hr:X8}");

                _reader.SetStreamSelection(MF_SOURCE_READER_ALL_STREAMS, false);
                _reader.SetStreamSelection(MF_SOURCE_READER_FIRST_VIDEO_STREAM, true);

                if (!TrySetOutputFormat(1920, 1080) && !TrySetOutputFormat(1280, 720))
                    UseNativeFormat();

                // 建复用的 SoftwareBitmap
                _bitmap?.Dispose();
                _bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, _width, _height, BitmapAlphaMode.Premultiplied);

                _running = true;
                _frameCount = 0;
                _fpsWindowStart = Environment.TickCount64;

                _readThread = new Thread(ReadLoop)
                {
                    IsBackground = true,
                    Name = "VP.CameraRead",
                    Priority = ThreadPriority.AboveNormal,
                };
                _readThread.Start();

                StatusChanged?.Invoke(this, $"已连接：{device.Name}");
                return true;
            }
            catch (Exception ex)
            {
                if (source != IntPtr.Zero) Marshal.Release(source);
                if (attributes is not null) Marshal.ReleaseComObject(attributes);
                return Fail(ex.Message);
            }
        }
    }

    private bool Fail(string message)
    {
        Debug.WriteLine($"[VP-MF] 启动失败：{message}");
        StatusChanged?.Invoke(this, $"连接失败：{message}");
        return false;
    }

    private bool TrySetOutputFormat(int width, int height)
    {
        if (_reader is null) return false;

        IMFMediaType? type = null;
        try
        {
            if (MFCreateMediaType(out type) < 0 || type is null) return false;

            Guid major = MF_MT_MAJOR_TYPE, video = MFMediaType_Video;
            Guid sub = MF_MT_SUBTYPE, rgb32 = MFVideoFormat_RGB32;
            Guid frameSizeKey = MF_MT_FRAME_SIZE;
            ulong frameSize = ((ulong)(uint)width << 32) | (uint)height;

            type.SetGUID(ref major, ref video);
            type.SetGUID(ref sub, ref rgb32);
            type.SetUINT64(ref frameSizeKey, frameSize);

            if (_reader.SetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, IntPtr.Zero, type) < 0)
                return false;

            _width = width;
            _height = height;
            Debug.WriteLine($"[VP-MF] 输出格式已协商为 BGRA32 {width}×{height}");
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (type is not null) Marshal.ReleaseComObject(type);
        }
    }

    private void UseNativeFormat()
    {
        _width = 1920;
        _height = 1080;
        Debug.WriteLine("[VP-MF] 使用设备原生输出格式（按 1080p 处理）");
    }

    private static IMFAttributes CreateAttributesWith(Guid key, uint value)
    {
        if (MFCreateAttributes(out var attrs, 1) < 0)
            throw new InvalidOperationException("MFCreateAttributes 失败");

        attrs.SetUINT32(ref key, value);
        return attrs;
    }

    // ══════════════════════════ 取帧循环 ══════════════════════════

    private void ReadLoop()
    {
        Debug.WriteLine("[VP-MF] 取帧线程已启动");

        while (_running)
        {
            IMFSample? sample = null;
            IMFMediaBuffer? buffer = null;

            try
            {
                var reader = _reader;
                if (reader is null) break;

                int hr = reader.ReadSample(MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0,
                                           out _, out uint flags, out _, out sample);
                if (hr < 0)
                {
                    Debug.WriteLine($"[VP-MF] ReadSample 失败 0x{hr:X8}");
                    break;
                }

                if ((flags & MF_SOURCE_READERF_ENDOFSTREAM) != 0) break;
                if (sample is null) continue;

                if (sample.ConvertToContiguousBuffer(out buffer) < 0 || buffer is null) continue;
                if (buffer.Lock(out IntPtr ptr, out _, out uint curLen) < 0) continue;

                try
                {
                    int stride = _width * 4;
                    int height = _height;
                    if (curLen < (uint)(stride * height))
                    {
                        int possible = (int)(curLen / (uint)(stride == 0 ? 4 : stride));
                        if (possible > 0) height = possible;
                    }

                    int len = (int)Math.Min(curLen, (uint)(stride * height));
                    var bitmap = _bitmap;
                    if (bitmap is null) continue;

                    CopyIntoSoftwareBitmap(bitmap, ptr, len);

                    UpdateFps();
                    FrameArrived?.Invoke(this, bitmap);
                }
                finally
                {
                    buffer.Unlock();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VP-MF] 取帧异常：{ex.Message}");
                break;
            }
            finally
            {
                if (buffer is not null) Marshal.ReleaseComObject(buffer);
                if (sample is not null) Marshal.ReleaseComObject(sample);
            }
        }

        Debug.WriteLine("[VP-MF] 取帧线程已退出");
    }

    /// <summary>
    /// 把 MF 的裸 BGRA 像素写进复用的 SoftwareBitmap。
    /// 通过 <see cref="IMemoryBufferByteAccess"/> 拿到 WinRT 缓冲的原始指针，零额外分配。
    /// </summary>
    private static unsafe void CopyIntoSoftwareBitmap(SoftwareBitmap bitmap, IntPtr source, int length)
    {
        using var locked = bitmap.LockBuffer(BitmapBufferAccessMode.Write);
        using var reference = locked.CreateReference();

        var access = (IMemoryBufferByteAccess)(object)reference;
        access.GetBuffer(out byte* dst, out uint capacity);

        int copyLength = (int)Math.Min((uint)length, capacity);
        Buffer.MemoryCopy((void*)source, dst, capacity, (uint)copyLength);
    }

    private void UpdateFps()
    {
        _frameCount++;
        long now = Environment.TickCount64;
        long elapsed = now - _fpsWindowStart;

        if (elapsed >= 1000)
        {
            _fps = _frameCount * 1000.0 / elapsed;
            _frameCount = 0;
            _fpsWindowStart = now;
        }
    }

    public SoftwareBitmap? GrabStill()
    {
        var bitmap = _bitmap;
        if (bitmap is null) return null;

        try
        {
            // 返回副本：采集线程会持续改写 _bitmap，直接返回会导致拍照结果撕裂。
            // 注意 SoftwareBitmap.Copy 是【静态方法】：Copy(source)。
            return SoftwareBitmap.Copy(bitmap);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VP-MF] 取静帧失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>取当前帧的 BGRA32 原始像素（录像用）。</summary>
    public byte[]? GrabFrameBytes()
    {
        var bitmap = _bitmap;
        if (bitmap is null) return null;

        int len = _width * _height * 4;
        if (len <= 0) return null;

        var bytes = new byte[len];

        try
        {
            using var locked = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
            using var reference = locked.CreateReference();

            var access = (IMemoryBufferByteAccess)(object)reference;
            unsafe
            {
                access.GetBuffer(out byte* src, out uint capacity);
                int n = Math.Min(len, (int)capacity);
                Marshal.Copy((IntPtr)src, bytes, 0, n);
            }

            return bytes;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VP-MF] 取帧像素失败：{ex.Message}");
            return null;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_running && _reader is null) return;

            _running = false;

            try
            {
                // Flush 立刻唤醒阻塞中的 ReadSample，缩短停止耗时
                _reader?.Flush(MF_SOURCE_READER_ALL_STREAMS);
            }
            catch
            {
                // 忽略
            }

            _readThread?.Join(1500);
            _readThread = null;

            if (_reader is not null)
            {
                Marshal.ReleaseComObject(_reader);
                _reader = null;
            }

            _bitmap?.Dispose();
            _bitmap = null;
            _fps = 0;

            StatusChanged?.Invoke(this, "已停止预览");
        }
    }

    public void Dispose()
    {
        Stop();
        try { MFShutdown(); } catch { /* 忽略 */ }
    }

    // ══════════════════════════ MF P/Invoke ══════════════════════════

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(int version, int dwFlags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateAttributes(out IMFAttributes ppMFAttributes, uint cInitialSize);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMediaType(out IMFMediaType ppMFType);

    [DllImport("mfreadwrite.dll", ExactSpelling = true)]
    private static extern int MFCreateSourceReaderFromMediaSource(
        IntPtr pMediaSource, IMFAttributes? pAttributes, out IMFSourceReader ppSourceReader);

    // ⚠ 关键：这里【不能】用 LPArray + SizeParamIndex 让 marshaler 自动转数组。
    //
    //  原生签名：
    //      HRESULT MFEnumDeviceSources(IMFAttributes*, IMFActivate***, UINT32*);
    //                                       ↑ 指向指针数组的指针
    //
    //  用 out IMFActivate[] + SizeParamIndex 看起来优雅，但数组长度参数排在
    //  数组之后 —— marshaler 需要先知道长度才能分配托管数组，处理顺序一乱，
    //  就会"返回成功但拿到空数组"，表现为【一个设备都扫不到，且不报任何错】。
    //
    //  因此退回最笨但最可靠的写法：拿 IntPtr，自己按指针宽度逐个读。
    [DllImport("mf.dll", ExactSpelling = true)]
    private static extern int MFEnumDeviceSources(
        IMFAttributes pAttributes,
        out IntPtr pppSourceActivate,
        out uint pcSourceActivate);

    /// <summary>WinRT 缓冲的原始指针访问接口（写 SoftwareBitmap 用）。</summary>
    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }

    // ─────────────────────────── COM 接口 ───────────────────────────
    //  vtable 顺序必须与 Windows SDK 头文件完全一致，未使用的方法也不能删除。

    [ComImport, Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFAttributes
    {
        [PreserveSig] int GetItem(ref Guid guidKey, IntPtr pValue);
        [PreserveSig] int GetItemType(ref Guid guidKey, out int pType);
        [PreserveSig] int CompareItem(ref Guid guidKey, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
        [PreserveSig] int Compare([MarshalAs(UnmanagedType.Interface)] IMFAttributes pTheirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
        [PreserveSig] int GetUINT32(ref Guid guidKey, out uint punValue);
        [PreserveSig] int GetUINT64(ref Guid guidKey, out ulong punValue);
        [PreserveSig] int GetDouble(ref Guid guidKey, out double pfValue);
        [PreserveSig] int GetGUID(ref Guid guidKey, out Guid pguidValue);
        [PreserveSig] int GetStringLength(ref Guid guidKey, out uint pcchLength);
        [PreserveSig] int GetString(ref Guid guidKey, StringBuilder pwszValue, uint cchBufSize, out uint pcchLength);
        [PreserveSig] int GetAllocatedString(ref Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
        [PreserveSig] int GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
        [PreserveSig] int GetBlob(ref Guid guidKey, byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
        [PreserveSig] int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int SetItem(ref Guid guidKey, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid guidKey);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid guidKey, uint unValue);
        [PreserveSig] int SetUINT64(ref Guid guidKey, ulong unValue);
        [PreserveSig] int SetDouble(ref Guid guidKey, double fValue);
        [PreserveSig] int SetGUID(ref Guid guidKey, ref Guid guidValue);
        [PreserveSig] int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        [PreserveSig] int SetBlob(ref Guid guidKey, byte[] pbBuf, uint cbBufSize);
        [PreserveSig] int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint pcItems);
        [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
        [PreserveSig] int CopyAllItems([MarshalAs(UnmanagedType.Interface)] IMFAttributes pDest);
    }

    [ComImport, Guid("7FEE9E9A-4A89-47A6-899C-B6A53A70FB67"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFActivate : IMFAttributes
    {
        [PreserveSig] int ActivateObject(ref Guid riid, out IntPtr ppv);
        [PreserveSig] int DetachObject();
        [PreserveSig] int ShutdownObject();
    }

    [ComImport, Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaType : IMFAttributes
    {
        // 继承 IMFAttributes 的全部成员
    }

    [ComImport, Guid("70AE66F2-C809-4E4F-8915-BDCB406B7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSourceReader
    {
        [PreserveSig] int GetStreamSelection(uint dwStreamIndex, [MarshalAs(UnmanagedType.Bool)] out bool pSelected);
        [PreserveSig] int SetStreamSelection(uint dwStreamIndex, [MarshalAs(UnmanagedType.Bool)] bool fSelected);
        [PreserveSig] int GetNativeMediaType(uint dwStreamIndex, uint dwMediaTypeIndex, out IMFMediaType ppMediaType);
        [PreserveSig] int GetCurrentMediaType(uint dwStreamIndex, out IMFMediaType ppMediaType);
        [PreserveSig] int SetCurrentMediaType(uint dwStreamIndex, IntPtr pdwReserved, IMFMediaType pMediaType);
        [PreserveSig] int SetCurrentPosition(ref Guid guidTimeFormat, IntPtr varPosition);
        [PreserveSig] int ReadSample(uint dwStreamIndex, uint dwControlFlags,
                                     out uint pdwActualStreamIndex, out uint pdwStreamFlags,
                                     out long pllTimestamp, out IMFSample ppSample);
        [PreserveSig] int Flush(uint dwStreamIndex);
        [PreserveSig] int GetServiceForStream(uint dwStreamIndex, ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
        [PreserveSig] int GetPresentationAttribute(uint dwStreamIndex, ref Guid guidAttribute, IntPtr pvarAttribute);
    }

    [ComImport, Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSample : IMFAttributes
    {
        [PreserveSig] int GetSampleFlags(out uint pdwSampleFlags);
        [PreserveSig] int SetSampleFlags(uint dwSampleFlags);
        [PreserveSig] int GetSampleTime(out long phnsSampleTime);
        [PreserveSig] int SetSampleTime(long hnsSampleTime);
        [PreserveSig] int GetSampleDuration(out long phnsSampleDuration);
        [PreserveSig] int SetSampleDuration(long hnsSampleDuration);
        [PreserveSig] int GetBufferCount(out uint pdwBufferCount);
        [PreserveSig] int GetBufferByIndex(uint dwIndex, out IMFMediaBuffer ppBuffer);
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer);
        [PreserveSig] int AddBuffer(IMFMediaBuffer pBuffer);
        [PreserveSig] int RemoveBufferByIndex(uint dwIndex);
        [PreserveSig] int RemoveAllBuffers();
        [PreserveSig] int GetTotalLength(out uint pcbTotalLength);
        [PreserveSig] int CopyToBuffer(IMFMediaBuffer pBuffer);
    }

    [ComImport, Guid("045FA593-8799-42B8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr ppbBuffer, out uint pcbMaxLength, out uint pcbCurrentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out uint pcbCurrentLength);
        [PreserveSig] int SetCurrentLength(uint cbCurrentLength);
        [PreserveSig] int GetMaxLength(out uint pcbMaxLength);
    }
}