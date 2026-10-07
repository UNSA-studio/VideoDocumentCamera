// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// Required Notice: Copyright (C) 2026 UNSA Studio
//
// 本文件属于视频展台（VideoPresenter）。分发时请随文件一并提供许可条款：
// https://polyformproject.org/licenses/noncommercial/1.0.0
//
using System.Runtime.InteropServices;

namespace VideoPresenter.Shared.Ipc;

/// <summary>
/// 启动器 &lt;-&gt; 主程序 共享内存头部布局。
/// <para>
/// ⚠ 该结构体必须与 C++ 端 <c>src/VideoPresenter.Launcher/VpIpc.h</c> 中的
/// <c>VP_SHARED_HEADER</c> <b>逐字节一致</b>（含 Pack=1）。任何改动需两端同步。
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
public struct VpSharedHeader
{
    /// <summary>魔数 'V','P','S','1'（小端 0x31535056），用于校验映射有效性。</summary>
    public uint Magic;

    /// <summary>协议版本。不匹配时主程序拒绝握手（防止新旧版本混装）。</summary>
    public uint Version;

    /// <summary>当前握手状态，见 <see cref="VpBootState"/>。volatile，双方可读可写。</summary>
    public int State;

    /// <summary>启动器进程 PID。</summary>
    public uint LauncherPid;

    /// <summary>主程序进程 PID（由主程序回填）。</summary>
    public uint AppPid;

    /// <summary>启动器 Splash 窗口句柄。</summary>
    public ulong LauncherHwnd;

    /// <summary>主程序主窗口句柄（由主程序回填，启动器可据此做前台切换）。</summary>
    public ulong AppHwnd;

    /// <summary>启动时刻，取值 <c>GetTickCount64()</c>，用于统计真实启动耗时。</summary>
    public ulong LaunchTick;

    /// <summary>主程序就绪时刻，取值 <c>GetTickCount64()</c>。</summary>
    public ulong ReadyTick;

    /// <summary>启动器已等待的毫秒数（启动器实时刷新，主程序可读取用于诊断）。</summary>
    public uint WaitedMs;

    /// <summary>是否请求主程序静默启动（不激活窗口）。</summary>
    public int SilentStart;

    /// <summary>保留 / 对齐字段。</summary>
    public int Reserved0;

    /// <summary>当前状态短文本（UTF-16，最多 128 字符，含结尾 0）。</summary>
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string Message;

    /// <summary>固定头部尺寸。尾部剩余空间留给后续扩展的通用载荷区。</summary>
    public const int HeaderSize = 512;

    /// <summary>整块映射的大小（4 KB，正好一页，TLB 友好且零碎片）。</summary>
    public const int MappingSize = 4096;

    /// <summary>载荷区相对映射起始的偏移。</summary>
    public const int PayloadOffset = HeaderSize;

    /// <summary>载荷区长度。</summary>
    public const int PayloadSize = MappingSize - HeaderSize;

    /// <summary>当前协议魔数。</summary>
    public const uint MagicValue = 0x3153_5056; // 'V','P','S','1'

    /// <summary>当前协议版本。</summary>
    public const uint ProtocolVersion = 1;
}

/// <summary>启动握手状态机。</summary>
public enum VpBootState
{
    /// <summary>启动器已创建映射，尚未拉起主程序。</summary>
    Created = 0,

    /// <summary>启动器已发起 CreateProcess。</summary>
    Launching = 1,

    /// <summary>主程序已附加到映射，正在初始化 UI。</summary>
    AppAttached = 2,

    /// <summary>主程序主窗口已可见，握手完成 —— 启动器可自杀。</summary>
    AppReady = 3,

    /// <summary>启动失败（超时 / 进程创建失败 / 版本不匹配）。</summary>
    Failed = 0x7FFF_FFFF
}
