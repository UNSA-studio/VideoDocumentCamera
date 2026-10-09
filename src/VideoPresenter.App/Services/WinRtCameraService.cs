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
    private readonly List<CameraDevice> _devices = new();
    private readonly List<string> _deviceIds = new();   // 与 _devices 一一对应

    private MediaCapture? _capture;
    private MediaFrameReader? _frameReader;
    private MediaFrameSource? _frameSource;

    /// <summary>最近一帧（复用的位图，供界面显示）。</summary>
    private SoftwareBitmap? _latest;

    private long _frameCount;
    private long _fpsWindowStart;
    private double _fps;
    private volatile bool _running;

    public IReadOnlyList<CameraDevice> Devices => _devices;
    public bool IsRunning => _running;
    public double Fps => _fps;

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

    private static void Log(string message)
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

            // BGRA8 —— 后续所有处理（显示 / 截图 / 录像）都基于这个格式
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

    private bool Fail(string message)
    {
        Log($"[VP-WinRT] 启动失败：{message}");
        StatusChanged?.Invoke(this, $"连接失败：{message}");
        try { Stop(); } catch { /* 忽略 */ }
        return false;
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            var video = frame?.VideoMediaFrame;
            if (video?.SoftwareBitmap is null) return;

            using var src = video.SoftwareBitmap;

            // 统一转成 BGRA8（某些设备原生是 NV12 / YUY2）
            var converted = SoftwareBitmap.Convert(src, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

            var old = Interlocked.Exchange(ref _latest, converted);
            old?.Dispose();

            UpdateFps();
            FrameArrived?.Invoke(this, converted);
        }
        catch (Exception ex)
        {
            Log($"[VP-WinRT] 取帧异常：{ex.Message}");
        }
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
