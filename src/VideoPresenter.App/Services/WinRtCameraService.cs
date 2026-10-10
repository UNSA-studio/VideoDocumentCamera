// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// Required Notice: Copyright (C) 2026 UNSA Studio
//
// 本文件属于视频展台（VideoPresenter）。分发时请随文件一并提供许可条款：
// https://polyformproject.org/licenses/noncommercial/1.0.0
//
// ============================================================================
//  WinRtCameraService.cs —— 基于 WinRT MediaCapture 的采集实现
//
//  为什么放弃 Media Foundation？
//  ---------------------------------------------------------------------------
//  本项目最初用 MF（MFEnumDeviceSources + IMFSourceReader）实现采集，
//  在真机上连续踩了四个 COM interop 的坑：
//
//    ① SetGUID 通过 [ComImport] 接口调用不生效 → MFEnumDeviceSources 返回
//       E_INVALIDARG（0x80070057），表现为"一个设备都扫不到"；
//    ② SetString 的 vtable 索引多写 1 → 调到 SetBlob → 访问冲突闪退；
//    ③ SetGUID/SetUINT64 写 MediaType 同样不生效 → 格式协商静默失败 →
//       尺寸猜错 → 预览全黑；
//    ④ IMFSourceReader 虽然 IID 正确（且与官方 mfreadwrite.idl 一致），
//       但它是 IDL 里的 `local` 接口，.NET 的 RCW 在 ReadSample() 首次
//       真正调用时 QueryInterface 失败：E_NOINTERFACE (0x80004002)。
//
//  归根结底：手写 COM 接口 + 手算 vtable 索引在 .NET 上太脆弱。
//  WinRT 的 MediaCapture 是投影 API（由 CsWinRT 生成，无需手写 IID），
//  且是微软在 WinUI 3 上推荐的采集方案，因此改为本实现。
// ============================================================================
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Core;
using Windows.Media.MediaProperties;

namespace VideoPresenter.App.Services;

/// <summary>
/// WinRT 缓冲的原始指针访问接口（写 / 读 SoftwareBitmap 像素用）。
///
/// <para>
/// 放在命名空间级而不是某个类内部：采集服务（WinRt 与 MF 两套实现）
/// 都需要它，嵌在类里会导致另一处无法引用。
/// </para>
/// </summary>
[ComImport]
[Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal unsafe interface IMemoryBufferByteAccess
{
    void GetBuffer(out byte* buffer, out uint capacity);
}

/// <summary>
    /// 基于 WinRT <see cref="MediaCapture"/> 的采集实现。
    /// </summary>
    public sealed class WinRtCameraService : ICameraService
    {
        public WinRtCameraService()
        {
            // 采集回调在后台线程，帧通知必须经 DispatcherQueue 投递到 UI 线程
            _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        }

    private readonly List<CameraDevice> _devices = new();
    private readonly List<string> _deviceIds = new();   // 与 _devices 一一对应

    private MediaCapture? _capture;
    private MediaFrameReader? _frameReader;
    private MediaFrameSource? _frameSource;

    /// <summary>最近一帧（复用的位图，供界面显示）。</summary>
    private SoftwareBitmap? _latest;

    /// <summary>帧格式只打印一次，避免日志刷屏。</summary>
    private bool _logFormatOnce = true;

    // ── 性能计时（每秒汇总一次，帮助定位瓶颈）──
    private long _perfWindowStart = Environment.TickCount64;
    private long _perfFrames;
    private long _perfAcquireTicks;
    private long _perfConvertTicks;

    /// <summary>
    /// 上一帧。
    /// <para>
    /// 用于"延迟一帧释放"：界面显示与 GrabStill()（拍照）都可能仍在引用
    /// _latest，所以不能在新帧到来时立刻 Dispose 它 —— 那会让拍照
    /// 拿到已释放的对象、静默失败。
    /// </para>
    /// </summary>
    private SoftwareBitmap? _previous;

    private long _frameCount;
    private long _fpsWindowStart;
    private double _fps;

    private long _displayFrames;
    private long _displayWindowStart;
    private double _displayFps;

    /// <summary>UI 线程派发器（帧通知必须投递到 UI 线程）。</summary>
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;

    private volatile bool _running;

    public IReadOnlyList<CameraDevice> Devices => _devices;
    public bool IsRunning => _running;
    /// <summary>接口帧率：对外暴露的是【采集帧率】（设备给帧速度）。</summary>
    public double Fps => _fps;

    /// <summary>界面显示帧率（渲染到屏幕的速度，远程桌面下会明显偏低）。</summary>
    public double DisplayFps => _displayFps;

    public event EventHandler<SoftwareBitmap>? FrameArrived;
    public event EventHandler<string>? StatusChanged;

    // ───────────────────────── 诊断 ─────────────────────────

    private string _diagnostics = "尚未扫描设备";
    public string DiagnosticsText => _diagnostics;

    /// <summary>日志文件路径（与 MF 实现保持一致，界面直接显示它）。</summary>
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

    private static readonly object LogGate = new();

    /// <summary>
    /// 写一行诊断日志。
    /// <para>internal 而非 private：View / ViewModel 也要用它记录拍照等失败原因 ——
    /// 那些地方原先用 Debug.WriteLine，用户在日志里根本看不到。</para>
    /// </summary>
    internal static void Log(string message)
    {
        try
        {
            string path = LogFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // 超过 1 MB 就截断，避免无限增长
            if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                File.Delete(path);

            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
            lock (LogGate) File.AppendAllText(path, line + Environment.NewLine);
            Debug.WriteLine(line);
        }
        catch
        {
            // 日志失败绝不能影响主流程
        }
    }

    // ───────────────────────── 枚举 ─────────────────────────

    /// <summary>
    /// 枚举视频采集设备。
    ///
    /// <para><b>为什么用同步阻塞</b></para>
    /// <para>
    /// DeviceInformation.FindAllAsync 是异步 API，而 ICameraService 是同步接口。
    /// 这里用 GetAwaiter().GetResult() 阻塞 —— 调用方在 UI 线程且不在
    /// 同步上下文中，不会造成死锁（MS 文档对 WinRT 异步 API 的显式说明）。
    /// </para>
    /// </summary>
    public void RefreshDevices()
    {
        _devices.Clear();
        _deviceIds.Clear();

        try
        {
            var infos = DeviceInformation.FindAllAsync(DeviceClass.VideoCapture)
                                             .AsTask().GetAwaiter().GetResult();

            int index = 0;
            foreach (var info in infos)
            {
                string name = string.IsNullOrWhiteSpace(info.Name) ? $"视频设备 {index + 1}" : info.Name;

                // 只保留真正可用于视频采集的设备（排除音频等）
                _devices.Add(new CameraDevice(index, name, info.Id));
                _deviceIds.Add(info.Id);

                Log($"[VP-WinRT] 设备 {index}：\"{name}\"  id={info.Id}");
                index++;
            }

            Log($"[VP-WinRT] 设备枚举完成，共 {_devices.Count} 个");
            _diagnostics = _devices.Count > 0
                ? $"✓ WinRT 枚举到 {_devices.Count} 个视频设备"
                : "✗ WinRT 未枚举到任何视频设备\n" +
                  "   请检查：USB 连接 / 是否需要厂商驱动 / 是否被其它程序占用\n" +
                  "   可在「设备管理器 → 照相机」确认设备是否存在";
        }
        catch (Exception ex)
        {
            Log($"[VP-WinRT] 设备枚举失败：{ex}");
            _diagnostics = $"✗ 设备枚举失败：{ex.Message}";
        }

        StatusChanged?.Invoke(this, _devices.Count > 0
            ? $"已发现 {_devices.Count} 个视频设备"
            : "未发现视频设备");
    }

    // ───────────────────────── 打开 / 关闭 ─────────────────────────

    public bool Start(CameraDevice device)
    {
        Stop();

        try
        {
            if (device.Index < 0 || device.Index >= _deviceIds.Count)
            {
                return Fail($"设备索引无效（{device.Index}）");
            }

            string deviceId = _deviceIds[device.Index];
            Log($"[VP-WinRT] 正在打开设备：{device.Name}");

            _capture = new MediaCapture();

            var settings = new MediaCaptureInitializationSettings
            {
                VideoDeviceId = deviceId,
                StreamingCaptureMode = StreamingCaptureMode.Video,

                // CPU 内存：这样 SoftwareBitmap 可以直接读像素
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,

                // 共享只读：允许多个程序同时用（希沃展台可能也在开着的）
                SharingMode = MediaCaptureSharingMode.SharedReadOnly,
            };

            _capture.InitializeAsync(settings).AsTask().GetAwaiter().GetResult();
            Log("[VP-WinRT] MediaCapture 初始化成功");

            // 选一个彩色视频源（展台就是普通彩色摄像头）
            _frameSource = _capture.FrameSources.Values
                .FirstOrDefault(s => s.Info?.SourceKind == MediaFrameSourceKind.Color)
                ?? _capture.FrameSources.Values.FirstOrDefault();

            if (_frameSource is null)
            {
                return Fail("设备没有可用的视频源");
            }

            Log($"[VP-WinRT] 视频源：{_frameSource.Info?.DeviceInformation?.Name}");

            // ── 选择设备支持的【最优格式】 ──
            //
            // ⚠ 不要用 CreateFrameReaderAsync(source, Bgra8) 直接要 BGRA8：
            //   那等于强制系统走"实时格式转换"管道，很多展台在转换时
            //   只能跑 10fps 左右（实测希沃展台就是这样）。
            //
            //   正确做法：先看设备原生支持哪些格式，挑一个分辨率/帧率最高的，
            //   用 SetFormatAsync 设上去，再创建 reader（不指定输出格式，
            //   即使用源的原生格式），最后由我们自己转成 BGRA8。
            SelectBestFormat();

            if (_frameSource is null)
            {
                return Fail("设备没有可用的视频源");
            }

            // 让系统直接把帧转成 BGRA8。
            //
            // ⚠ 之前为了"省开销"改成不指定输出格式 + 自己 SoftwareBitmap.Convert，
            //   结果画面一片绿 —— 那是 NV12 的 UV 平面被按 BGRA 解析导致的
            //   典型通道错位。自己转 YUV 太容易踩坑，交回给 MediaFrameReader
            //   的内建转换最稳。
            //
            //   代价是转换开销，但配合"预览分辨率限制在 1080p 以内"，
            //   这个开销是可以接受的（720p/1080p 的 BGRA 远小于 800 万像素）。
            _frameReader = _capture.CreateFrameReaderAsync(_frameSource, MediaEncodingSubtypes.Bgra8)
                                   .AsTask().GetAwaiter().GetResult();

            _frameReader.FrameArrived += OnFrameArrived;

            _frameReader.StartAsync().AsTask().GetAwaiter().GetResult();

            _running = true;
            _frameCount = 0;
            _fpsWindowStart = Environment.TickCount64;

            Log("[VP-WinRT] 帧读取器已启动");
            StatusChanged?.Invoke(this, $"已连接：{device.Name}");
            return true;
        }
        catch (Exception ex)
        {
            Log($"[VP-WinRT] 打开设备失败：{ex}");
            return Fail(ex.Message);
        }
    }

    /// <summary>
    /// 在设备支持的格式里挑一个适合【实时预览】的。
    ///
    /// <para><b>⚠ 关键约束：必须限制分辨率上限</b></para>
    /// <para>
    /// 展台设备通常同时暴露"拍照模式"和"视频模式"的格式，
    /// 前者可能高达 3264×2448（800 万像素）—— 那个分辨率跑 30fps，
    /// 每帧 BGRA 就要 32 MB，光内存带宽就接近 1 GB/s，
    /// 再加上逐帧格式转换，会把整机拖到卡死。
    /// 所以这里硬性限制：预览分辨率不超过 1920×1080。
    /// </para>
    /// </summary>
    private void SelectBestFormat()
    {
        if (_frameSource is null) return;

        // 预览分辨率上限：超过这个值的格式一律不考虑
        const long MaxPreviewPixels = 1920L * 1080L;

        try
        {
            var formats = _frameSource.SupportedFormats;
            if (formats is null || formats.Count == 0)
            {
                Log("[VP-WinRT] 设备未报告支持的格式，沿用默认");
                return;
            }

            foreach (var f in formats)
            {
                var v = f.VideoFormat;
                Log($"[VP-WinRT]   支持格式：{v?.Width}×{v?.Height} @ {FpsOf(f):F0}fps  {f.Subtype}");
            }

            // ① 先筛"分辨率不超过 1080p 且帧率不小于 20"的候选
            var candidates = formats
                .Where(f => f.VideoFormat is not null
                            && (long)f.VideoFormat.Width * f.VideoFormat.Height <= MaxPreviewPixels
                            && FpsOf(f) >= 20)
                .ToList();

            // ② 候选里取分辨率最高的（最清晰），同分辨率取帧率最高的
            var chosen = candidates
                .OrderByDescending(f => (long)f.VideoFormat.Width * f.VideoFormat.Height)
                .ThenByDescending(FpsOf)
                .FirstOrDefault();

            // ③ 没有达标帧率的 → 退让：允许任意帧率，但仍限制在 1080p 以内
            chosen ??= formats
                .Where(f => f.VideoFormat is not null
                            && (long)f.VideoFormat.Width * f.VideoFormat.Height <= MaxPreviewPixels)
                .OrderByDescending(f => (long)f.VideoFormat.Width * f.VideoFormat.Height)
                .ThenByDescending(FpsOf)
                .FirstOrDefault();

            // ④ 实在没有 1080p 以内的 → 取所有格式里最"省"的那个（像素最少）
            chosen ??= formats
                .Where(f => f.VideoFormat is not null)
                .OrderBy(f => (long)f.VideoFormat.Width * f.VideoFormat.Height)
                .FirstOrDefault();

            if (chosen is null) return;

            Log($"[VP-WinRT] 选择格式：{chosen.VideoFormat.Width}×{chosen.VideoFormat.Height} "
              + $"@ {FpsOf(chosen):F0}fps  {chosen.Subtype}");

            _frameSource.SetFormatAsync(chosen).AsTask().GetAwaiter().GetResult();
            Log("[VP-WinRT] 格式已设置");
        }
        catch (Exception ex)
        {
            Log($"[VP-WinRT] 选择格式失败（沿用默认）：{ex.Message}");
        }
    }

    /// <summary>把 MediaRatio 形式的帧率转成数字（不能与属性 Fps 重名）。</summary>
    private static double FpsOf(MediaFrameFormat format)
    {
        var r = format.FrameRate;
        if (r is null || r.Denominator == 0) return 0;

        return (double)r.Numerator / r.Denominator;
    }

    private bool Fail(string message)
    {
        Log($"[VP-WinRT] 启动失败：{message}");
        StatusChanged?.Invoke(this, $"连接失败：{message}");
        try { Stop(); } catch { /* 忽略 */ }
        return false;
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        long t0 = Stopwatch.GetTimestamp();
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            var video = frame?.VideoMediaFrame;
            if (video?.SoftwareBitmap is null) return;

            long t1 = Stopwatch.GetTimestamp();

            using var src = video.SoftwareBitmap;

            // 系统已经按 BGRA8 输出（见 CreateFrameReaderAsync 的说明），
            // 这里只需确保颜色模式一致；SoftwareBitmap.Convert 对同格式是廉价操作。
            SoftwareBitmap converted;
            if (src.BitmapPixelFormat == BitmapPixelFormat.Bgra8 &&
                src.BitmapAlphaMode == BitmapAlphaMode.Premultiplied)
            {
                converted = SoftwareBitmap.Copy(src);
            }
            else
            {
                converted = SoftwareBitmap.Convert(src, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            }

            long t2 = Stopwatch.GetTimestamp();

            if (_logFormatOnce)
            {
                _logFormatOnce = false;
                Log($"[VP-WinRT] 帧格式：源 {src.BitmapPixelFormat}/{src.BitmapAlphaMode} "
                  + $"{src.PixelWidth}×{src.PixelHeight} → 输出 {converted.BitmapPixelFormat}");
            }

            // ── 双缓冲：延迟一帧释放 ──
            //
            // ⚠ 不能立刻 Dispose 旧帧：
            //   它可能正被界面显示、或被 GrabStill() 复制（拍照）。
            //   之前就是在这里立刻 old?.Dispose()，导致拍照时
            //   SoftwareBitmap.Copy 抛异常 → GrabStill 返回 null →
            //   拍照静默失败（状态栏只说"当前没有可用画面"）。
            //
            //   现在保留"当前 + 上一帧"两个引用，释放的是更早那一帧 ——
            //   到那时它已不可能仍在使用。
            var previous = Interlocked.Exchange(ref _previous, null);
            var old = Interlocked.Exchange(ref _latest, converted);
            previous?.Dispose();

            // ── 性能计时 ──
            // 每秒打印一次各阶段耗时，用来判断瓶颈在哪：
            //   采集取帧(下) / 格式转换 / 通知投递
            _perfAcquireTicks += t1 - t0;
            _perfConvertTicks += t2 - t1;
            _perfFrames++;

            // 采集帧率：纯统计"设备给了多少帧"，不包含界面渲染开销
            UpdateFps();

            if (Environment.TickCount64 - _perfWindowStart >= 1000 && _perfFrames > 0)
            {
                double msPerTick = 1000.0 / Stopwatch.Frequency;

                Log($"[VP-Perf] 帧数 {_perfFrames}  "
                  + $"取帧 {_perfAcquireTicks * msPerTick / _perfFrames:F1}ms  "
                  + $"转换 {_perfConvertTicks * msPerTick / _perfFrames:F1}ms  "
                  + $"→ {_fps:F1} fps");

                _perfFrames = 0;
                _perfAcquireTicks = 0;
                _perfConvertTicks = 0;
                _perfWindowStart = Environment.TickCount64;
            }

            // ⚠ 通知界面用【异步】派发，绝不在采集回调里同步调用。
            //
            //   原先是 FrameArrived?.Invoke(...) —— 界面处理（尤其是屏幕编码 /
            //   远程传输）会把耗时算进采集回调，直接回压到设备，
            //   表现为 UU 远程时帧率掉到个位数。
            //
            //   现在只投递一个"有新帧"的信号，界面自己去取最新帧；
            //   界面慢的时候丢的是中间帧，而不是拖慢采集。
            var handler = FrameArrived;
            if (handler is not null)
            {
                _dispatcher?.TryEnqueue(() =>
                {
                    var cur = _latest;
                    if (cur is not null) handler(this, cur);
                    UpdateDisplayFps();
                });
            }
        }
        catch (Exception ex)
        {
            Log($"[VP-WinRT] 取帧异常：{ex.Message}");
        }
    }

    /// <summary>采集帧率（设备实际给帧的速度）。</summary>
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

    /// <summary>界面显示帧率（渲染到屏幕的速度，受远程桌面等影响）。</summary>
    private void UpdateDisplayFps()
    {
        _displayFrames++;
        long now = Environment.TickCount64;
        long elapsed = now - _displayWindowStart;

        if (elapsed >= 1000)
        {
            _displayFps = _displayFrames * 1000.0 / elapsed;
            _displayFrames = 0;
            _displayWindowStart = now;
        }
    }

    public void Stop()
    {
        _running = false;

        try
        {
            if (_frameReader is not null)
            {
                _frameReader.FrameArrived -= OnFrameArrived;
                try { _frameReader.StopAsync().AsTask().GetAwaiter().GetResult(); } catch { }
                _frameReader.Dispose();
                _frameReader = null;
            }

            _frameSource = null;

            if (_capture is not null)
            {
                _capture.Dispose();
                _capture = null;
            }

            var old = Interlocked.Exchange(ref _latest, null);
            old?.Dispose();

            var prev = Interlocked.Exchange(ref _previous, null);
            prev?.Dispose();

            _fps = 0;
            Log("[VP-WinRT] 已停止预览");
        }
        catch (Exception ex)
        {
            Log($"[VP-WinRT] 停止预览时出错：{ex.Message}");
        }
    }

    // ───────────────────────── 取帧 ─────────────────────────

    public SoftwareBitmap? GrabStill()
    {
        var cur = _latest;
        if (cur is null) return null;

        try
        {
            return SoftwareBitmap.Copy(cur);
        }
        catch (Exception ex)
        {
            Log($"[VP-WinRT] 复制静帧失败：{ex.Message}");
            return null;
        }
    }

    public byte[]? GrabFrameBytes()
    {
        var cur = _latest;
        if (cur is null) return null;

        try
        {
            int w = cur.PixelWidth, h = cur.PixelHeight;
            int stride = w * 4;
            var bytes = new byte[stride * h];

            CopyOut(cur, bytes);
            return bytes;
        }
        catch (Exception ex)
        {
            Log($"[VP-WinRT] 取原始像素失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 把 SoftwareBitmap 的像素拷进托管数组。
    ///
    /// <para>
    /// 注意不能用 WindowsRuntimeBufferExtensions.AsBuffer(byte[]) ——
    /// 那个扩展方法在 .NET 5+ 的 CsWinRT 投影里已经不存在了。
    /// 这里走 IMemoryBufferByteAccess（与 MF 实现里写位图用的是同一个接口），
    /// 拿到原始指针后 Marshal.Copy，最直接也最可靠。
    /// </para>
    /// </summary>
    private static unsafe void CopyOut(SoftwareBitmap bitmap, byte[] destination)
    {
        using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
        using var reference = buffer.CreateReference();

        ((IMemoryBufferByteAccess)reference).GetBuffer(out byte* ptr, out uint capacity);

        int len = Math.Min(destination.Length, (int)capacity);
        System.Runtime.InteropServices.Marshal.Copy((IntPtr)ptr, destination, 0, len);
    }

    public void Dispose() => Stop();
}
