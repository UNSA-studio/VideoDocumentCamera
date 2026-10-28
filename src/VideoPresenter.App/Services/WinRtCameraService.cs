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
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private SoftwareBitmap? _latest;
    private TaskCompletionSource<bool>? _firstFrame;
    private volatile bool _running;
    private bool _disposed;
    private int _callbackBusy;
    private long _received, _dropped, _windowStart, _lastFrame, _presented, _presentStart;
    private double _fps, _displayFps;
    private bool _formatLogged;
    private int _deferredInitializations;
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

    public async Task<bool> StartAsync(CameraDevice device, CancellationToken cancellationToken = default)
    {
        RequireUI();
        await _lifecycle.WaitAsync(cancellationToken);
        var clock = Stopwatch.StartNew();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_deferredInitializations > 0)
                throw new InvalidOperationException("上一次驱动初始化仍未返回；请稍后重试或重新插拔设备。");
            await StopCoreAsync();
            Log($"Connect BEGIN name={device.Name}; id={device.SymbolicLink}; apartment={Thread.CurrentThread.GetApartmentState()}");
            Exception? initializationFailure = null;
            foreach (var sharing in new[] { MediaCaptureSharingMode.ExclusiveControl, MediaCaptureSharingMode.SharedReadOnly })
            {
                cancellationToken.ThrowIfCancellationRequested();
                var capture = new MediaCapture();
                var settings = new MediaCaptureInitializationSettings
                {
                    VideoDeviceId = device.SymbolicLink,
                    StreamingCaptureMode = StreamingCaptureMode.Video,
                    MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                    SharingMode = sharing,
                };
                Task initialize;
                try { initialize = capture.InitializeAsync(settings).AsTask(); }
                catch { capture.Dispose(); throw; }
                try
                {
                    Log($"Initialize BEGIN sharing={sharing}");
                    await initialize.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
                    _capture = capture;
                    _capture.Failed += CaptureFailed;
                    Log($"Initialize END sharing={sharing}; elapsed={clock.ElapsedMilliseconds}ms");
                }
                catch (Exception ex)
                {
                    initializationFailure = ex;
                    // WaitAsync timeout/cancellation does not stop native initialization. Do NOT dispose it mid-call.
                    if (!initialize.IsCompleted)
                    {
                        _deferredInitializations++;
                        _ = initialize.ContinueWith(t =>
                        {
                            _ = t.Exception;
                            _ui.TryEnqueue(() => { capture.Dispose(); _deferredInitializations--; Log("Deferred initialization finished; resource released."); });
                        }, TaskScheduler.Default);
                        throw;
                    }
                    capture.Dispose();
                    AppLog.Error("Initialize sharing=" + sharing, ex);
                    if (ex is OperationCanceledException || sharing == MediaCaptureSharingMode.SharedReadOnly) throw;
                    continue;
                }
                var source = _capture.FrameSources.Values
                    .Where(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
                    .OrderByDescending(s => s.Info.MediaStreamType == MediaStreamType.VideoPreview)
                    .FirstOrDefault() ?? throw new InvalidOperationException("设备未提供彩色视频流。");
                Log($"Source id={source.Info.Id}; kind={source.Info.SourceKind}; stream={source.Info.MediaStreamType}");
                foreach (var format in source.SupportedFormats)
                    Log($"Supported {format.VideoFormat?.Width}x{format.VideoFormat?.Height} @{Rate(format):F2} {format.Subtype}");
                if (sharing == MediaCaptureSharingMode.ExclusiveControl)
                {
                    var chosen = source.SupportedFormats.Where(f => f.VideoFormat is not null &&
                        (long)f.VideoFormat.Width * f.VideoFormat.Height <= 1920L * 1080 && Rate(f) <= 60)
                        .OrderByDescending(f => Rate(f) >= 25)
                        .ThenByDescending(f => (long)f.VideoFormat.Width * f.VideoFormat.Height)
                        .ThenByDescending(Rate).FirstOrDefault();
                    if (chosen is not null)
                    {
                        try
                        {
                            await source.SetFormatAsync(chosen).AsTask().WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
                            Log($"Selected {chosen.VideoFormat.Width}x{chosen.VideoFormat.Height} @{Rate(chosen):F2} {chosen.Subtype}");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { AppLog.Error("Set source format; inspect actual current format below", ex); }
                    }
                }
                else Log("SharedReadOnly: SetFormatAsync intentionally skipped; the controller owns source format.");
                var current = source.CurrentFormat;
                Log($"Actual input {current.VideoFormat.Width}x{current.VideoFormat.Height} @{Rate(current):F2} {current.Subtype}");
                double scale = Math.Min(1.0, Math.Min(1920.0 / current.VideoFormat.Width, 1080.0 / current.VideoFormat.Height));
                var size = new BitmapSize { Width = Math.Max(2u, (uint)(current.VideoFormat.Width * scale) / 2 * 2),
                    Height = Math.Max(2u, (uint)(current.VideoFormat.Height * scale) / 2 * 2) };
                // SoftwareBitmapSource requires BGRA8 + premultiplied alpha. NV12 is NOT directly displayable by it.
                foreach (string subtype in new[] { MediaEncodingSubtypes.Bgra8, current.Subtype }.Distinct())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        Log($"Probe BEGIN subtype={subtype}; output={size.Width}x{size.Height}");
                        _reader = await _capture.CreateFrameReaderAsync(source, subtype, size).AsTask()
                            .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                        _reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
                        _reader.FrameArrived += OnFrameArrived;
                        _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        _formatLogged = false;
                        _received = _dropped = _presented = 0;
                        _windowStart = _presentStart = Environment.TickCount64;
                        var result = await _reader.StartAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                        Log($"Reader Start status={result}");
                        if (result != MediaFrameReaderStartStatus.Success)
                            throw new InvalidOperationException("帧读取器启动失败：" + result);
                        await _firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(6), cancellationToken);
                        _running = true;
                        DiagnosticsText = $"已收到可显示帧：{device.Name}；输出 BGRA8/Premultiplied";
                        StatusChanged?.Invoke(this, DiagnosticsText);
                        Log($"Connect END first usable frame verified; elapsed={clock.ElapsedMilliseconds}ms");
                        return true;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { AppLog.Error("Probe subtype=" + subtype, ex); await StopReaderAsync(); }
                }
                break; // Initialized but no usable frames: report the actual failure instead of blaming occupancy.
            }
            throw new InvalidOperationException("没有收到可用视频帧；请查看日期日志中的 Probe/Frame 异常。", initializationFailure);
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
        _running = false;
        Log($"Capture FAILED code=0x{args.Code:X8}; message={args.Message}");
        DiagnosticsText = $"采集失败：{args.Message} (0x{args.Code:X8})";
        _firstFrame?.TrySetException(new InvalidOperationException(DiagnosticsText));
        StatusChanged?.Invoke(this, DiagnosticsText);
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (_disposed || !ReferenceEquals(sender, _reader)) return;
        if (Interlocked.Exchange(ref _callbackBusy, 1) != 0) { Interlocked.Increment(ref _dropped); return; }
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
                if (!ReferenceEquals(sender, _reader)) { normalized.Dispose(); return; }
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
            _firstFrame?.TrySetResult(true);
            _received++;
            long elapsed = Environment.TickCount64 - _windowStart;
            if (elapsed >= 5000)
            {
                Volatile.Write(ref _fps, _received * 1000.0 / elapsed);
                Log($"PERF acquired={_fps:F1}fps; submittedToImage={DisplayFps:F1}fps; droppedBusy={_dropped}; " +
                    $"lastCallback={timer.Elapsed.TotalMilliseconds:F2}ms; managedBytes={GC.GetTotalMemory(false)}");
                _received = 0; _windowStart = Environment.TickCount64;
            }
            FrameAvailable?.Invoke(this, EventArgs.Empty); // MainWindow performs bounded, single-flight dispatch.
        }
        catch (Exception ex)
        {
            if (!_formatLogged) { _formatLogged = true; AppLog.Error("Frame normalization", ex); }
            _firstFrame?.TrySetException(ex);
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

    private async Task StopReaderAsync()
    {
        var reader = _reader; _reader = null;
        if (reader is null) return;
        reader.FrameArrived -= OnFrameArrived;
        try { await reader.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) { AppLog.Error("Stop frame reader", ex); }
        reader.Dispose();
    }

    private async Task StopCoreAsync()
    {
        _running = false;
        await StopReaderAsync();
        if (_capture is not null) { _capture.Failed -= CaptureFailed; _capture.Dispose(); _capture = null; }
        lock (_frameGate) { _latest?.Dispose(); _latest = null; }
        _fps = _displayFps = 0;
        Log("Stop completed");
    }

    public void Dispose()
    {
        // Normal window close must await StopAsync first. This fallback never blocks the UI thread.
        _disposed = true; _running = false;
        if (_reader is not null) { _reader.FrameArrived -= OnFrameArrived; _reader.Dispose(); _reader = null; }
        if (_capture is not null) { _capture.Failed -= CaptureFailed; _capture.Dispose(); _capture = null; }
        lock (_frameGate) { _latest?.Dispose(); _latest = null; }
    }

    private static double Rate(MediaFrameFormat format) => format.FrameRate.Denominator == 0 ? 0 :
        (double)format.FrameRate.Numerator / format.FrameRate.Denominator;
}