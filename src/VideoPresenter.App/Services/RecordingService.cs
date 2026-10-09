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

    private static readonly Guid MF_MT_MAJOR_TYPE = new("48EBA18E-F8C9-4687-BF11-0A74CD669FA8");
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
    public void Stop()
    {
        Thread? thread;
        IMFSinkWriter? writer;

        lock (_gate)
        {
            if (!IsRecording) return;

            IsRecording = false;

            _queue?.CompleteAdding();
            thread = _writeThread;
            writer = _writer;
        }

        try { thread?.Join(5000); }
        catch { /* 超时也继续收尾 */ }

        try
        {
            // Finalize 会把索引写进文件 —— 没有它 MP4 无法播放
            writer?.Finalize_();
        }
        catch (Exception ex)
        {
            Log($"[VP-REC] Finalize 失败：{ex.Message}");
        }

        long dropped = DroppedFrames;
        Log($"[VP-REC] 录制结束：{OutputPath}（时长 {(DateTime.Now - StartedAt).TotalSeconds:F1}s，丢帧 {dropped}）");

        lock (_gate)
        {
            Cleanup();
            _writeThread = null;
        }
    }

    private void WriteLoop()
    {
        var writer = _writer;
        if (writer is null) return;

        long duration = 10_000_000L / Math.Max(_fps, 1);   // 100ns 单位

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

                    long time = _frameIndex * duration;
                    sample.SetSampleTime(time);
                    sample.SetSampleDuration(duration);

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

        Log($"[VP-REC] 写入线程退出，共写入 {_frameIndex} 帧");
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
    }

    // ══════════════════════════ 辅助 ══════════════════════════

    private static IMFMediaType CreateVideoType(Guid subType, int width, int height, int fps, int bitrate)
    {
        if (MFCreateMediaType(out var type) < 0 || type is null)
            throw new InvalidOperationException("MFCreateMediaType 失败");

        Guid major = MF_MT_MAJOR_TYPE, video = MFMediaType_Video;
        Guid sub = MF_MT_SUBTYPE, subValue = subType;
        Guid frameSizeKey = MF_MT_FRAME_SIZE;
        Guid frameRateKey = MF_MT_FRAME_RATE;
        Guid interlaceKey = MF_MT_INTERLACE_MODE;

        type.SetGUID(ref major, ref video);
        type.SetGUID(ref sub, ref subValue);
        type.SetUINT64(ref frameSizeKey, ((ulong)(uint)width << 32) | (uint)height);
        type.SetUINT64(ref frameRateKey, ((ulong)(uint)fps << 32) | 1);
        type.SetUINT32(ref interlaceKey, MF_MT_INTERLACE_MODE_PROGRESSIVE);

        if (bitrate > 0)
        {
            Guid bitrateKey = MF_MT_AVG_BITRATE;
            type.SetUINT32(ref bitrateKey, (uint)bitrate);
        }

        return type;
    }

    private static void Log(string message)
    {
        Debug.WriteLine(message);

        try
        {
            string file = WinRtCameraService.LogFilePath;
            var fi = new FileInfo(file);
            if (fi.Exists && fi.Length > 1024 * 1024) fi.Delete();

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.AppendAllText(file,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch { }
    }

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