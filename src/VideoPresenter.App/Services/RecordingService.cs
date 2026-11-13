// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// Required Notice: Copyright (C) 2026 UNSA Studio
//
// 本文件属于视频展台（VideoPresenter）。分发时请随文件一并提供许可条款：
// https://polyformproject.org/licenses/noncommercial/1.0.0
//
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace VideoPresenter.App.Services;

/// <summary>
/// 录像服务：把展台画面录制成 MP4（H.264）。
///
/// <para><b>技术路线</b></para>
/// <para>
/// 使用 Windows Media Foundation 的 <c>IMFSinkWriter</c> 直接把帧写进 MP4 容器，
/// 由系统自带的 H.264 编码器（MFT）完成压缩 —— 不引入任何第三方编码库，
/// 生成的 MP4 在所有播放器上都能放。
/// </para>
///
/// <para><b>为什么要后台线程 + 帧队列</b></para>
/// <para>
/// <c>WriteSample</c> 可能被编码器阻塞（尤其在低端机器上）。如果在 UI 线程写帧，
/// 会导致界面卡顿、预览掉帧。因此这里用一个容量很小的有界队列：
/// 后台线程消费，队列满时<b>直接丢帧</b>而不是阻塞采集 —— 也就是"宁可丢帧，
/// 也不能让预览卡住"。
/// </para>
/// </summary>
public sealed class RecordingService : IDisposable
{
    // ───────────────────────── MF 常量 ─────────────────────────

    private const int MF_VERSION = 0x0002_0070;

    private static readonly Guid MF_MT_MAJOR_TYPE = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
    private static readonly Guid MF_MT_SUBTYPE = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
    private static readonly Guid MF_MT_FRAME_SIZE = new("1652C33D-D6B2-4012-B834-72030849A37D");
    private static readonly Guid MF_MT_FRAME_RATE = new("C459A2E8-3D2C-4E44-B132-FEE5156C7BB0");
    private static readonly Guid MF_MT_AVG_BITRATE = new("20332624-FB0D-4D9E-BD0D-CBF6786C102E");
    private static readonly Guid MF_MT_INTERLACE_MODE = new("E2724BB8-E676-4806-B4B2-A8D6EFB44CCD");

    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");

    private const int MF_MT_INTERLACE_MODE_PROGRESSIVE = 2;

    // ───────────────────────── 状态 ─────────────────────────

    private readonly object _gate = new();

    private IMFSinkWriter? _writer;
    private uint _streamIndex;
    private int _width;
    private int _height;
    private int _fps;
    private long _frameIndex;
    private bool _disposed;

    private System.Collections.Concurrent.BlockingCollection<byte[]>? _queue;
    private Thread? _writeThread;
    private long _droppedFrames;

    // ───────────────────── 时间轴（REC-003）─────────────────────
    //
    // 用 Stopwatch 的真实经过时间打时间戳，而不是"帧序号 × 帧间隔"。
    // 原因：队列满会丢帧、相机抖动会漏帧，用帧序号算时间会让视频
    // 越录越快（录 60 秒的视频在播放器里只有 45 秒，声音/动作全部加速）。
    private readonly Stopwatch _clock = new();
    private long _nextSampleTime;      // 100ns 单位
    private readonly object _clockGate = new();
    /// <summary>本实例是否持有一次 MFStartup 引用。</summary>
    private bool _mfHeld;

    // ───────────────────── MF 启动（REC-001）─────────────────────
    //
    // MFStartup 必须在使用任何 MF API 之前调用一次（含 SinkWriter / MediaType），
    // 否则 MFCreateSinkWriterFromURL 直接返回 MF_E_NOT_INITIALIZED，
    // 表现就是"点录像没有任何反应，日志里一行 0xC00D36B4"。
    // 这是进程级初始化，用引用计数保证多实例/重入时只调用一次。
    private static readonly object MfGate = new();
    private static int MfRefCount;
    private static bool MfStarted;

    /// <summary>
    /// 确保 Media Foundation 已初始化。返回 false 表示初始化失败（此时不要继续）。
    /// </summary>
    private static bool EnsureMfStarted()
    {
        lock (MfGate)
        {
            if (MfStarted) { MfRefCount++; return true; }

            try
            {
                // Fatal 会抛异常，用返回码形式自己判断
                int hr = MFStartup(MF_VERSION, 0);
                if (hr < 0)
                {
                    Log($"[VP-REC] MFStartup 失败 0x{hr:X8}");
                    return false;
                }
                MfStarted = true;
                MfRefCount = 1;
                Log("[VP-REC] MFStartup 成功");
                return true;
            }
            catch (DllNotFoundException ex)
            {
                Log($"[VP-REC] mfplat.dll 不可用：{ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Log($"[VP-REC] MFStartup 异常：{ex.Message}");
                return false;
            }
        }
    }

    private static void ReleaseMf()
    {
        lock (MfGate)
        {
            if (!MfStarted) return;
            MfRefCount--;
            if (MfRefCount > 0) return;

            try { MFShutdown(); }
            catch (Exception ex) { Log($"[VP-REC] MFShutdown 异常：{ex.Message}"); }
            MfStarted = false;
            MfRefCount = 0;
        }
    }

    public bool IsRecording { get; private set; }
    public string? OutputPath { get; private set; }
    public DateTime StartedAt { get; private set; }

    public TimeSpan Duration => IsRecording ? DateTime.Now - StartedAt : TimeSpan.Zero;

    /// <summary>因队列拥塞而丢弃的帧数（诊断用）。</summary>
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

    // ══════════════════════════ 开始 / 停止 ══════════════════════════

    /// <summary>
    /// 开始录制。
    /// </summary>
    /// <param name="outputPath">输出 MP4 路径。</param>
    /// <param name="width">帧宽（必须与写入的帧一致）。</param>
    /// <param name="height">帧高。</param>
    /// <param name="fps">帧率。</param>
    /// <param name="bitrate">平均码率（bit/s）。展台场景 1080p 建议 8 Mbps。</param>
    public bool Start(string outputPath, int width, int height, int fps = 30, int bitrate = 8_000_000)
    {
        lock (_gate)
        {
            if (IsRecording) return false;

            _clock.Reset();
            _clock.Start();

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            }
            catch (Exception ex)
            {
                Log($"[VP-REC] 无法创建输出目录：{ex.Message}");
                return false;
            }

            try
            {
                _width = width;
                _height = height;
                _fps = fps;
                _frameIndex = 0;
                Interlocked.Exchange(ref _droppedFrames, 0);

                lock (_clockGate)
                {
                    _nextSampleTime = 0;
                }

                // ⓪ MFStartup —— 必须先于任何 MF 调用（REC-001）
                if (!EnsureMfStarted())
                {
                    Log("[VP-REC] 跳过启动：Media Foundation 未初始化");
                    return false;
                }
                _mfHeld = true;

                // ① 创建 SinkWriter（直接写文件）
                int hr = MFCreateSinkWriterFromURL(outputPath, IntPtr.Zero, null, out _writer);
                if (hr < 0 || _writer is null)
                {
                    Log($"[VP-REC] MFCreateSinkWriterFromURL 失败 0x{hr:X8}");
                    return false;
                }

                // ② 输出类型：H.264 / 指定分辨率帧率码率
                var outType = CreateVideoType(MFVideoFormat_H264, width, height, fps, bitrate);
                hr = _writer.AddStream(outType, out _streamIndex);
                Marshal.ReleaseComObject(outType);

                if (hr < 0)
                {
                    Log($"[VP-REC] AddStream 失败 0x{hr:X8}");
                    Cleanup();
                    return false;
                }

                // ③ 输入类型：BGRA32（与采集通路保持一致，编码器内部会转换）
                var inType = CreateVideoType(MFVideoFormat_RGB32, width, height, fps, 0);
                hr = _writer.SetInputMediaType(_streamIndex, inType, null);
                Marshal.ReleaseComObject(inType);

                if (hr < 0)
                {
                    Log($"[VP-REC] SetInputMediaType 失败 0x{hr:X8}");
                    Cleanup();
                    return false;
                }

                hr = _writer.BeginWriting();
                if (hr < 0)
                {
                    Log($"[VP-REC] BeginWriting 失败 0x{hr:X8}");
                    Cleanup();
                    return false;
                }

                // ④ 后台写入线程
                _queue = new System.Collections.Concurrent.BlockingCollection<byte[]>(maxQueueFrames);

                _writeThread = new Thread(WriteLoop)
                {
                    IsBackground = true,
                    Name = "VP.RecordWrite",
                    Priority = ThreadPriority.BelowNormal,
                };
                _writeThread.Start();

                OutputPath = outputPath;
                StartedAt = DateTime.Now;
                IsRecording = true;

                Log($"[VP-REC] 开始录制：{outputPath}（{width}×{height}@{fps}, {bitrate / 1_000_000}Mbps）");
                return true;
            }
            catch (Exception ex)
            {
                Log($"[VP-REC] 开始录制异常：{ex}");
                Cleanup();
                return false;
            }
        }
    }

    private const int maxQueueFrames = 6;

    /// <summary>
    /// 送入一帧（BGRA32 像素）。
    /// <para>队列满时直接丢帧 —— 宁可少录几帧，也不能让预览卡住。</para>
    /// </summary>
    public void WriteFrame(byte[] bgraPixels)
    {
        if (!IsRecording || _queue is null) return;

        int expected = _width * _height * 4;
        if (bgraPixels.Length < expected) return;

        // 只保留需要的那部分，避免持有更大的缓冲
        var copy = new byte[expected];
        Buffer.BlockCopy(bgraPixels, 0, copy, 0, expected);

        if (!_queue.TryAdd(copy))
        {
            Interlocked.Increment(ref _droppedFrames);
        }
    }

    /// <summary>停止录制并完成文件写入（写入 moov 等索引信息）。</summary>
    /// <remarks>
    /// REC-002 修复要点：Finalize 必须在写入线程<b>完全退出之后</b>调用。
    /// 旧实现虽然 Join 了线程，但 writer 引用是在锁外捕获的，且 Join 超时后
    /// 仍会 Finalize —— 此时写入线程可能正持有 writer 写帧，
    /// 结果是 MP4 索引损坏（文件有大小但播不了）或直接 AccessViolation。
    /// 现在：CompleteAdding → Join（最长 8 秒）→ 确认线程已死 → 才 Finalize。
    /// 线程没退出干净就放弃 Finalize，宁可文件不全也不要崩进程。
    /// </remarks>
    public void Stop()
    {
        Thread? thread;

        lock (_gate)
        {
            if (!IsRecording) return;
            IsRecording = false;
            thread = _writeThread;
            try { _queue?.CompleteAdding(); } catch (ObjectDisposedException) { }
        }

        // 等写入线程把队列里剩下的帧写完
        bool threadExited = true;
        if (thread is not null)
        {
            try
            {
                threadExited = thread.Join(8000);
            }
            catch (Exception ex)
            {
                Log($"[VP-REC] 等待写入线程异常：{ex.Message}");
                threadExited = false;
            }
        }

        if (!threadExited)
        {
            Log("[VP-REC] 写入线程未在 8 秒内退出，跳过 Finalize —— 该文件可能无法播放");
        }
        else
        {
            lock (_gate)
            {
                var writer = _writer;
                if (writer is not null)
                {
                    try
                    {
                        // Finalize 会把 moov 索引写进文件 —— 没有它 MP4 无法播放
                        int hr = writer.Finalize_();
                        if (hr < 0) Log($"[VP-REC] Finalize 返回 0x{hr:X8}");
                    }
                    catch (Exception ex)
                    {
                        Log($"[VP-REC] Finalize 异常：{ex.Message}");
                    }
                }
            }
        }

        long dropped = DroppedFrames;

        lock (_gate)
        {
            long frames = _frameIndex;
            string? path = OutputPath;
            double elapsed = _clock.Elapsed.TotalSeconds;
            _clock.Reset();
            Cleanup();
            _writeThread = null;
            Log($"[VP-REC] 录制结束：{path}（写入 {frames} 帧，丢帧 {dropped}，" +
                $"实际时长 {elapsed:F1}s）");
        }
    }

    private void WriteLoop()
    {
        var writer = _writer;
        if (writer is null) return;

        long nominal = 10_000_000L / Math.Max(_fps, 1);   // 100ns 单位，理想帧间隔

        try
        {
            foreach (var frame in _queue!.GetConsumingEnumerable())
            {
                if (writer is null) break;

                IMFSample? sample = null;
                IMFMediaBuffer? buffer = null;

                try
                {
                    int hr = MFCreateMemoryBuffer((uint)frame.Length, out buffer);
                    if (hr < 0 || buffer is null) continue;

                    if (buffer.Lock(out IntPtr ptr, out _, out _) < 0) continue;

                    try
                    {
                        Marshal.Copy(frame, 0, ptr, frame.Length);
                    }
                    finally
                    {
                        buffer.Unlock();
                    }

                    buffer.SetCurrentLength((uint)frame.Length);

                    hr = MFCreateSample(out sample);
                    if (hr < 0 || sample is null) continue;

                    sample.AddBuffer(buffer);

                    // ── REC-003：按真实经过时间打时间戳 ──
                    //
                    // 不能用 _frameIndex * nominal：
                    //   丢帧时 _frameIndex 只按"成功写入"递增，时间轴会比真实时间
                    //   短 —— 录 60 秒、丢 15 帧@30fps 的视频，播放器里只有 59.5 秒，
                    //   整体轻微加速；丢得越多越快。丢帧严重时能快到"看着像快进"。
                    //
                    // 正确做法：时间戳 = 该帧入队时的真实时钟，帧间隔取
                    //   "到下一帧的差值"，最后一帧用标称间隔。
                    // 这里简化成每次都取当前时钟并保证单调递增（编码器要求），
                    // 相邻帧为空时不重叠 —— 效果等于把丢帧的时间"如实保留"。
                    long time = _clock.Elapsed.Ticks;      // Stopwatch 就是 100ns
                    lock (_clockGate)
                    {
                        if (time <= _nextSampleTime) time = _nextSampleTime;
                        _nextSampleTime = time + 1;
                    }
                    sample.SetSampleTime(time);
                    sample.SetSampleDuration(nominal);

                    hr = writer.WriteSample(_streamIndex, sample);

                    if (hr < 0)
                    {
                        Log($"[VP-REC] WriteSample 失败 0x{hr:X8}");
                    }
                    else
                    {
                        _frameIndex++;
                    }
                }
                catch (Exception ex)
                {
                    Log($"[VP-REC] 写入帧异常：{ex.Message}");
                }
                finally
                {
                    if (sample is not null) Marshal.ReleaseComObject(sample);
                    if (buffer is not null) Marshal.ReleaseComObject(buffer);
                }
            }
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        catch (Exception ex)
        {
            Log($"[VP-REC] 写入线程异常：{ex}");
        }

        Log($"[VP-REC] 写入线程退出，共写入 {_frameIndex} 帧，末帧时间戳 {_clock.Elapsed.TotalSeconds:F2}s");
    }

    private void Cleanup()
    {
        try { _queue?.Dispose(); } catch { }
        _queue = null;

        if (_writer is not null)
        {
            try { Marshal.ReleaseComObject(_writer); } catch { }
            _writer = null;
        }

        OutputPath = null;

        // 释放 Startup 引用（必须在所有 MF 对象释放之后）
        if (_mfHeld)
        {
            _mfHeld = false;
            ReleaseMf();
        }
    }

    // ══════════════════════════ 辅助 ══════════════════════════

    private static IMFMediaType CreateVideoType(Guid subType, int width, int height, int fps, int bitrate)
    {
        if (MFCreateMediaType(out var type) < 0 || type is null)
            throw new InvalidOperationException("MFCreateMediaType 失败");

        try
        {
        Guid major = MF_MT_MAJOR_TYPE, video = MFMediaType_Video;
        Guid sub = MF_MT_SUBTYPE, subValue = subType;
        Guid frameSizeKey = MF_MT_FRAME_SIZE;
        Guid frameRateKey = MF_MT_FRAME_RATE;
        Guid interlaceKey = MF_MT_INTERLACE_MODE;

        Marshal.ThrowExceptionForHR(type.SetGUID(ref major, ref video));
        Marshal.ThrowExceptionForHR(type.SetGUID(ref sub, ref subValue));
        Marshal.ThrowExceptionForHR(type.SetUINT64(ref frameSizeKey, ((ulong)(uint)width << 32) | (uint)height));
        Marshal.ThrowExceptionForHR(type.SetUINT64(ref frameRateKey, ((ulong)(uint)fps << 32) | 1));
        Marshal.ThrowExceptionForHR(type.SetUINT32(ref interlaceKey, MF_MT_INTERLACE_MODE_PROGRESSIVE));

        if (bitrate > 0)
        {
            Guid bitrateKey = MF_MT_AVG_BITRATE;
            Marshal.ThrowExceptionForHR(type.SetUINT32(ref bitrateKey, (uint)bitrate));
        }

        return type;
        }
        catch
        {
            Marshal.ReleaseComObject(type);
            throw;
        }
    }

    private static void Log(string message) => AppLog.Write(message, category: "Recording");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { Stop(); } catch { }
    }

    // ══════════════════════════ MF P/Invoke ══════════════════════════

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(int version, int dwFlags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMediaType(out IMFMediaType ppMFType);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMemoryBuffer(uint cbMaxLength, out IMFMediaBuffer ppBuffer);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateSample(out IMFSample ppIMFSample);

    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int MFCreateSinkWriterFromURL(
        string pwszOutputURL,
        IntPtr pByteStream,
        IMFAttributes? pAttributes,
        out IMFSinkWriter ppSinkWriter);

    // ───────────────────────── COM 接口 ─────────────────────────
    //  vtable 顺序必须与 Windows SDK 头文件一致。

    [ComImport, Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
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

    [ComImport, Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaType : IMFAttributes
    {
        // 继承 IMFAttributes 的全部成员
    }

    [ComImport, Guid("045FA593-8799-42B8-BC8D-8968C6453507"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr ppbBuffer, out uint pcbMaxLength, out uint pcbCurrentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out uint pcbCurrentLength);
        [PreserveSig] int SetCurrentLength(uint cbCurrentLength);
        [PreserveSig] int GetMaxLength(out uint pcbMaxLength);
    }

    [ComImport, Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
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

    [ComImport, Guid("3137F1CD-FE5E-4805-A5D8-FB477448CB3D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSinkWriter
    {
        [PreserveSig] int AddStream(IMFMediaType pTargetMediaType, out uint pdwStreamIndex);
        [PreserveSig] int SetInputMediaType(uint dwStreamIndex, IMFMediaType pInputMediaType, IMFAttributes? pEncodingParameters);
        [PreserveSig] int BeginWriting();
        [PreserveSig] int WriteSample(uint dwStreamIndex, IMFSample pSample);
        [PreserveSig] int SendStreamTick(uint dwStreamIndex, long llTimestamp);
        [PreserveSig] int PlaceMarker(uint dwStreamIndex, IntPtr pvContext);
        [PreserveSig] int NotifyEndOfSegment(uint dwStreamIndex);
        [PreserveSig] int Flush(uint dwStreamIndex);

        // 注：方法名与 vtable 顺序有关，与名字无关。
        // "Finalize" 是 C# 关键字冲突点，这里取名 Finalize_。
        [PreserveSig] int Finalize_();

        [PreserveSig] int GetServiceForStream(uint dwStreamIndex, ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
        [PreserveSig] int GetStatistics(uint dwStreamIndex, IntPtr pStats);
    }
}