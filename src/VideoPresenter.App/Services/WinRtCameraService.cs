// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// Required Notice: Copyright (C) 2026 UNSA Studio
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace VideoPresenter.App.Services;

[ComImport, Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal unsafe interface IMemoryBufferByteAccess { void GetBuffer(out byte* buffer, out uint capacity); }

/// <summary>
/// UI/STA-initiated, genuinely asynchronous camera lifecycle. No Wait/Result/GetResult.
/// Each snapshot has one owner. FrameAvailable is only a signal, never a borrowed bitmap.
/// References: Microsoft Learn, MediaCapture.InitializeAsync; Process media frames with MediaFrameReader.
/// </summary>
public sealed class WinRtCameraService : ICameraService
{
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread()
        ?? throw new InvalidOperationException("Camera service must be created on the UI thread.");
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _frameGate = new();
    private readonly List<CameraDevice> _devices = new();
    private sealed class CameraSession
    {
        public readonly MediaCapture Capture = new();
        public readonly NativeOperationGuard Operations = new();
        public MediaFrameReader? Reader;
        public Task? StopTask;
        public bool AcceptFrames;
        public bool FailedSubscribed;
        public Exception? Failure;
        public TaskCompletionSource<bool> FirstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private volatile CameraSession? _session;
    private Task _cleanup = Task.CompletedTask;
    private SoftwareBitmap? _latest;
    private volatile bool _running;
    private bool _disposed;
    private int _callbackBusy;
    private long _received, _dropped, _windowStart, _lastFrame, _presented, _presentStart;
    private double _fps, _displayFps;
    private bool _formatLogged;
    private long _lastFrameError;
    public IReadOnlyList<CameraDevice> Devices => _devices;
    public bool IsRunning => _running;
    public double Fps => Volatile.Read(ref _fps);
    // This measures successful SetBitmapAsync completions, NOT monitor/remote-client presentation.
    public double DisplayFps => Volatile.Read(ref _displayFps);
    public string DiagnosticsText { get; private set; } = "尚未扫描设备";
    public event EventHandler? FrameAvailable;
    public event EventHandler<string>? StatusChanged;
    public static string LogFilePath => AppLog.CurrentFile;
    internal static void Log(string text) => AppLog.Write(text, category: "Camera");

    private void RequireUI()
    {
        if (!_ui.HasThreadAccess) throw new InvalidOperationException("Camera lifecycle must run on the UI/STA thread.");
    }

    public async Task RefreshDevicesAsync(CancellationToken cancellationToken = default)
    {
        RequireUI();
        Log("Enumerate BEGIN");
        try
        {
            var infos = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _devices.Clear();
            int index = 0;
            foreach (var info in infos)
            {
                _devices.Add(new CameraDevice(index++, info.Name, info.Id));
                Log($"Device name={info.Name}; id={info.Id}");
            }
            DiagnosticsText = $"已发现 {_devices.Count} 个视频设备；尚未验证出帧与显示";
            StatusChanged?.Invoke(this, DiagnosticsText);
            Log($"Enumerate END count={_devices.Count}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Error("Enumerate video devices", ex);
            DiagnosticsText = $"扫描失败：{ex.Message} (0x{ex.HResult:X8})";
            StatusChanged?.Invoke(this, DiagnosticsText);
        }
    }

    private void EnsureCleanupComplete()
    {
        if (!_cleanup.IsCompletedSuccessfully)
            throw new InvalidOperationException(_cleanup.IsCompleted
                ? "上一次设备清理失败，禁止复用；请重启应用并保留日志。"
                : "上一次驱动初始化仍未返回；请稍后重试或重新插拔设备。");
    }

    public async Task<bool> StartAsync(CameraDevice device, CancellationToken cancellationToken = default)
    {
        RequireUI();
        await _lifecycle.WaitAsync(cancellationToken);
        var clock = Stopwatch.StartNew();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureCleanupComplete();
            await StopCoreAsync();
            EnsureCleanupComplete();
            Log($"Connect BEGIN name={device.Name}; id={device.SymbolicLink}; apartment={Thread.CurrentThread.GetApartmentState()}");
            foreach (var sharing in new[] { MediaCaptureSharingMode.ExclusiveControl, MediaCaptureSharingMode.SharedReadOnly })
            {
                cancellationToken.ThrowIfCancellationRequested();
                var session = new CameraSession();
                _session = session;
                var settings = new MediaCaptureInitializationSettings
                {
                    VideoDeviceId = device.SymbolicLink,
                    StreamingCaptureMode = StreamingCaptureMode.Video,
                    MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                    SharingMode = sharing,
                };
                try
                {
                    Log($"Initialize BEGIN sharing={sharing}");
                    await session.Operations.RunAsync(() => session.Capture.InitializeAsync(settings).AsTask(),
                        TimeSpan.FromSeconds(15), cancellationToken);
                    ObjectDisposedException.ThrowIf(_disposed || !ReferenceEquals(session, _session), this);
                    session.Capture.Failed += CaptureFailed;
                    session.FailedSubscribed = true;
                    Log($"Initialize END sharing={sharing}; elapsed={clock.ElapsedMilliseconds}ms");
                }
                catch (OperationCanceledException) { throw; }
                catch (TimeoutException) { throw; }
                catch (Exception ex)
                {
                    AppLog.Error("Initialize sharing=" + sharing, ex);
                    await StopCoreAsync();
                    EnsureCleanupComplete();
                    if (sharing == MediaCaptureSharingMode.SharedReadOnly) throw;
                    continue;
                }
                var source = session.Capture.FrameSources.Values
                    .Where(v => v.Info.SourceKind == MediaFrameSourceKind.Color)
                    .OrderByDescending(v => v.Info.MediaStreamType == MediaStreamType.VideoPreview)
                    .FirstOrDefault() ?? throw new InvalidOperationException("设备未提供彩色视频流。");
                Log($"Source id={source.Info.Id}; kind={source.Info.SourceKind}; stream={source.Info.MediaStreamType}");
                foreach (var format in source.SupportedFormats)
                    Log($"Supported {format.VideoFormat?.Width}x{format.VideoFormat?.Height} @{Rate(format):F2} {format.Subtype}");
                if (sharing == MediaCaptureSharingMode.ExclusiveControl)
                {
                    var formats = source.SupportedFormats.Where(f => f.VideoFormat is not null &&
                        f.VideoFormat.Width > 0 && f.VideoFormat.Height > 0 &&
                        (long)f.VideoFormat.Width * f.VideoFormat.Height <= 1920L * 1080 && Rate(f) > 0 && Rate(f) <= 60).ToArray();
                    var chosen = formats.Where(f => Rate(f) >= 25)
                        .OrderByDescending(f => (long)f.VideoFormat.Width * f.VideoFormat.Height)
                        .ThenByDescending(Rate).FirstOrDefault()
                        ?? formats.OrderByDescending(Rate).ThenByDescending(f => (long)f.VideoFormat.Width * f.VideoFormat.Height).FirstOrDefault();
                    if (chosen is not null)
                    {
                        try
                        {
                            await session.Operations.RunAsync(() => source.SetFormatAsync(chosen).AsTask(),
                                TimeSpan.FromSeconds(8), cancellationToken);
                            Log($"Selected {chosen.VideoFormat.Width}x{chosen.VideoFormat.Height} @{Rate(chosen):F2} {chosen.Subtype}");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (TimeoutException) { throw; } // Never inspect/reconfigure a still-changing source.
                        catch (Exception ex) { AppLog.Error("Set source format failed; inspect actual format", ex); }
                    }
                }
                else Log("SharedReadOnly: SetFormatAsync intentionally skipped; the controller owns source format.");
                var current = source.CurrentFormat;
                if (current?.VideoFormat is not { Width: > 0, Height: > 0 } video)
                    throw new InvalidOperationException("设备返回了无效的视频尺寸，不能创建预览读取器。");
                Log($"Actual input {video.Width}x{video.Height} @{Rate(current):F2} {current.Subtype}");
                double scale = Math.Min(1.0, Math.Min(1920.0 / video.Width, 1080.0 / video.Height));
                var size = new BitmapSize { Width = Math.Max(2u, (uint)(video.Width * scale) / 2 * 2),
                    Height = Math.Max(2u, (uint)(video.Height * scale) / 2 * 2) };
                foreach (string subtype in new[] { MediaEncodingSubtypes.Bgra8, current.Subtype }.Distinct())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        Log($"Probe BEGIN subtype={subtype}; output={size.Width}x{size.Height}");
                        // The owned task stores a late result in THIS session, never in the next session.
                        await session.Operations.RunAsync(async () =>
                        {
                            session.Reader = await session.Capture.CreateFrameReaderAsync(source, subtype, size);
                        }, TimeSpan.FromSeconds(10), cancellationToken);
                        ObjectDisposedException.ThrowIf(_disposed || !ReferenceEquals(session, _session), this);
                        var reader = session.Reader!;
                        reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
                        session.FirstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        session.StopTask = null;
                        lock (_frameGate) session.AcceptFrames = true;
                        reader.FrameArrived += OnFrameArrived;
                        _formatLogged = false;
                        _received = _dropped = _presented = 0;
                        _windowStart = _presentStart = Environment.TickCount64;
                        var result = await session.Operations.RunAsync(() => reader.StartAsync().AsTask(),
                            TimeSpan.FromSeconds(10), cancellationToken);
                        ObjectDisposedException.ThrowIf(_disposed || !ReferenceEquals(session, _session), this);
                        Log($"Reader Start status={result}");
                        if (result != MediaFrameReaderStartStatus.Success)
                            throw new InvalidOperationException("帧读取器启动失败：" + result);
                        try { await session.FirstFrame.Task.WaitAsync(TimeSpan.FromSeconds(6), cancellationToken); }
                        catch (TimeoutException ex)
                        {
                            // Unlike a native operation timeout, the first-frame waiter owns no pending native call.
                            AppLog.Error("No first normalized frame; stop reader before trying next subtype", ex);
                            await StopReaderAsync(session);
                            continue;
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                        lock (_frameGate)
                        {
                            if (session.Failure is not null) throw session.Failure;
                            _running = true;
                        }
                        DiagnosticsText = $"已收到可显示帧：{device.Name}；输出 BGRA8/Premultiplied";
                        StatusChanged?.Invoke(this, DiagnosticsText);
                        Log($"Connect END first normalized frame verified; elapsed={clock.ElapsedMilliseconds}ms");
                        return true;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (TimeoutException) { throw; } // Abort, retire, and wait for late native completion.
                    catch (Exception ex)
                    {
                        AppLog.Error("Probe subtype=" + subtype, ex);
                        await StopReaderAsync(session);
                        if (session.Failure is not null) throw;
                    }
                }
                break;
            }
            throw new InvalidOperationException("没有收到可用视频帧；请查看日期日志中的 Probe/Frame 异常。");
        }
        catch (OperationCanceledException) { Log("Connect CANCELLED"); await StopCoreAsync(); return false; }
        catch (Exception ex)
        {
            AppLog.Error("Connect FAILED elapsed=" + clock.ElapsedMilliseconds + "ms", ex);
            DiagnosticsText = $"连接失败：{ex.Message} (0x{ex.HResult:X8})";
            await StopCoreAsync();
            StatusChanged?.Invoke(this, DiagnosticsText);
            return false;
        }
        finally { _lifecycle.Release(); }
    }

    private void CaptureFailed(MediaCapture sender, MediaCaptureFailedEventArgs args)
    {
        CameraSession? session;
        string message = $"采集失败：{args.Message} (0x{args.Code:X8})";
        lock (_frameGate)
        {
            session = _session;
            if (session is null || !ReferenceEquals(sender, session.Capture)) return;
            session.Failure = new InvalidOperationException(message);
            session.AcceptFrames = false;
            _running = false;
            DiagnosticsText = message;
        }
        session.FirstFrame.TrySetException(session.Failure);
        Log($"Capture FAILED code=0x{args.Code:X8}; message={args.Message}");
        StatusChanged?.Invoke(this, message);
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        CameraSession? session;
        lock (_frameGate)
        {
            session = _session;
            if (_disposed || session is null || !session.AcceptFrames || !ReferenceEquals(sender, session.Reader)) return;
            // Admission and stop/invalidation use the same lock, so cleanup cannot miss a callback about to enter.
            if (Interlocked.Exchange(ref _callbackBusy, 1) != 0) { Interlocked.Increment(ref _dropped); return; }
        }
        var timer = Stopwatch.StartNew();
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            using var input = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (input is null) return;
            var normalized = input.BitmapPixelFormat == BitmapPixelFormat.Bgra8 && input.BitmapAlphaMode == BitmapAlphaMode.Premultiplied
                ? SoftwareBitmap.Copy(input)
                : SoftwareBitmap.Convert(input, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            if ((long)normalized.PixelWidth * normalized.PixelHeight > 1920L * 1080)
            {
                normalized.Dispose();
                throw new InvalidOperationException("Driver ignored bounded preview output size.");
            }
            string formatSummary = $"{normalized.BitmapPixelFormat}/{normalized.BitmapAlphaMode}";
            lock (_frameGate)
            {
                if (!ReferenceEquals(session, _session) || !session.AcceptFrames || !ReferenceEquals(sender, session.Reader)) { normalized.Dispose(); return; }
                // Ownership transfers to _latest; snapshots are copied under this same lock.
                var old = _latest;
                _latest = normalized;
                old?.Dispose();
            }
            if (!_formatLogged)
            {
                _formatLogged = true;
                Log($"First Frame input={input.BitmapPixelFormat}/{input.BitmapAlphaMode} {input.PixelWidth}x{input.PixelHeight}; " +
                    $"normalized={formatSummary}; callback={timer.Elapsed.TotalMilliseconds:F2}ms");
            }
            _lastFrame = Environment.TickCount64;
            session.FirstFrame.TrySetResult(true);
            _received++;
            long elapsed = Environment.TickCount64 - _windowStart;
            if (elapsed >= 5000)
            {
                Volatile.Write(ref _fps, _received * 1000.0 / elapsed);
                Log($"PERF processed={_fps:F1}fps; submittedToImage={DisplayFps:F1}fps; droppedBusy={_dropped}; " +
                    $"lastCallback={timer.Elapsed.TotalMilliseconds:F2}ms; managedBytes={GC.GetTotalMemory(false)}");
                _received = 0; _windowStart = Environment.TickCount64;
            }
            FrameAvailable?.Invoke(this, EventArgs.Empty); // MainWindow performs bounded, single-flight dispatch.
        }
        catch (Exception ex)
        {
            long now = Environment.TickCount64;
            if (_lastFrameError == 0 || now - _lastFrameError >= 5000)
            {
                _lastFrameError = now;
                AppLog.Error("Frame acquire/normalize/notify (rate limited to 5s)", ex);
            }
            session.FirstFrame.TrySetException(ex);
        }
        finally { Interlocked.Exchange(ref _callbackBusy, 0); }
    }

    public void ReportPresented()
    {
        _presented++;
        long elapsed = Environment.TickCount64 - _presentStart;
        if (elapsed >= 1000)
        {
            Volatile.Write(ref _displayFps, _presented * 1000.0 / elapsed);
            _presented = 0; _presentStart = Environment.TickCount64;
        }
    }

    public SoftwareBitmap? GrabStill()
    {
        lock (_frameGate) return _latest is null ? null : SoftwareBitmap.Copy(_latest);
    }

    public byte[]? GrabFrameBytes()
    {
        using var snapshot = GrabStill();
        return snapshot is null ? null : BitmapPixels.ReadBgra(snapshot);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        RequireUI();
        await _lifecycle.WaitAsync(cancellationToken);
        try { await StopCoreAsync(); } finally { _lifecycle.Release(); }
    }

    private async Task WaitForCallbackAsync()
    {
        while (Volatile.Read(ref _callbackBusy) != 0) await Task.Delay(10);
    }

    private async Task StopReaderAsync(CameraSession session)
    {
        var reader = session.Reader;
        if (reader is null) return;
        lock (_frameGate) session.AcceptFrames = false;
        reader.FrameArrived -= OnFrameArrived;
        // Keep ownership on a timeout. StopCoreAsync will wait for this same native task.
        await session.Operations.RunAsync(() => session.StopTask ??= reader.StopAsync().AsTask(), TimeSpan.FromSeconds(5));
        await WaitForCallbackAsync().WaitAsync(TimeSpan.FromSeconds(5));
        reader.Dispose();
        session.Reader = null;
        session.StopTask = null;
        lock (_frameGate) { _latest?.Dispose(); _latest = null; }
    }

    private async Task ReleaseSessionAsync(CameraSession session)
    {
        if (session.FailedSubscribed) session.Capture.Failed -= CaptureFailed;
        var reader = session.Reader; // Includes a reader returned AFTER the caller timed out.
        if (reader is not null)
        {
            reader.FrameArrived -= OnFrameArrived;
            try { await (session.StopTask ??= reader.StopAsync().AsTask()); }
            catch (Exception ex) { AppLog.Error("Retired reader stop completed with failure", ex); }
            await WaitForCallbackAsync();
            reader.Dispose();
        }
        session.Capture.Dispose();
        Log("Retired session: native operations settled; callbacks drained; resources released.");
    }

    private async Task StopCoreAsync()
    {
        CameraSession? session;
        lock (_frameGate)
        {
            session = _session;
            _session = null;
            _running = false;
            if (session is not null) session.AcceptFrames = false;
            _latest?.Dispose(); _latest = null;
        }
        if (session is not null)
            _cleanup = session.Operations.RetireAsync(() => ReleaseSessionAsync(session),
                ex => AppLog.Error("Retired native operation completed with failure", ex));
        _fps = _displayFps = 0;
        try { await _cleanup.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException)
        {
            Log("CLEANUP_DEFERRED: native task/callback is still running; resources retained and reconnect blocked.");
        }
        catch (Exception ex) { AppLog.Error("Session cleanup failed; reconnect blocked", ex); }
    }

    public void Dispose()
    {
        RequireUI();
        if (_disposed) return;
        _disposed = true;
        _ = DisposeCoreAsync();
    }
    private async Task DisposeCoreAsync()
    {
        try { await StopCoreAsync(); }
        catch (Exception ex) { AppLog.Error("Dispose camera", ex); }
    }

    private static double Rate(MediaFrameFormat format) => format.FrameRate.Denominator == 0 ? 0 :
        (double)format.FrameRate.Numerator / format.FrameRate.Denominator;
}