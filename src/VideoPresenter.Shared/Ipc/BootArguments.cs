using System.Globalization;
using System.Text;

namespace VideoPresenter.Shared.Ipc;

/// <summary>
/// 启动器通过命令行传给主程序的握手参数。
/// <para>示例：</para>
/// <code>
/// VideoPresenter.exe --vp-mmf="Local\UNSA.VP.Boot.{guid}" --vp-event="Local\UNSA.VP.Ready.{guid}"
///                    --vp-ppid=12345 --vp-tick=987654 --vp-silent=0
/// </code>
/// </summary>
public sealed class BootArguments
{
    public const string ArgMemoryMap = "--vp-mmf";
    public const string ArgReadyEvent = "--vp-event";
    public const string ArgParentPid = "--vp-ppid";
    public const string ArgLaunchTick = "--vp-tick";
    public const string ArgSilent = "--vp-silent";

    /// <summary>共享内存映射名，例如 <c>Local\UNSA.VP.Boot.{guid}</c>。</summary>
    public string? MemoryMapName { get; init; }

    /// <summary>就绪事件名，例如 <c>Local\UNSA.VP.Ready.{guid}</c>。</summary>
    public string? ReadyEventName { get; init; }

    /// <summary>启动器 PID。</summary>
    public uint ParentPid { get; init; }

    /// <summary>启动器记录的启动时刻。</summary>
    public ulong LaunchTick { get; init; }

    /// <summary>静默启动（不抢焦点，用于"随机点名/自动开课"等场景）。</summary>
    public bool Silent { get; init; }

    /// <summary>是否携带了完整握手参数（供主程序判断是否为"启动器拉起"）。</summary>
    public bool HasHandshake => !string.IsNullOrWhiteSpace(MemoryMapName)
                                && !string.IsNullOrWhiteSpace(ReadyEventName);

    /// <summary>解析命令行。</summary>
    public static BootArguments Parse(string[] args)
    {
        string? mmf = null, evt = null;
        uint ppid = 0;
        ulong tick = 0;
        bool silent = false;

        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            string? val = i + 1 < args.Length ? args[i + 1] : null;

            switch (key)
            {
                case ArgMemoryMap:
                case ArgReadyEvent:
                case ArgParentPid:
                case ArgLaunchTick:
                case ArgSilent:
                    if (val is null) continue;
                    i++;
                    break;
                default:
                    continue;
            }

            switch (key)
            {
                case ArgMemoryMap: mmf = val; break;
                case ArgReadyEvent: evt = val; break;
                case ArgParentPid:
                    uint.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out ppid);
                    break;
                case ArgLaunchTick:
                    ulong.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out tick);
                    break;
                case ArgSilent:
                    silent = val is "1" or "true" or "TRUE";
                    break;
            }
        }

        return new BootArguments
        {
            MemoryMapName = mmf,
            ReadyEventName = evt,
            ParentPid = ppid,
            LaunchTick = tick,
            Silent = silent,
        };
    }

    /// <summary>把参数拼接为可传给 <c>CreateProcessW</c> 的命令行串。</summary>
    public string ToCommandLine()
    {
        var sb = new StringBuilder();
        sb.Append(ArgMemoryMap).Append("=\"").Append(MemoryMapName).Append("\" ");
        sb.Append(ArgReadyEvent).Append("=\"").Append(ReadyEventName).Append("\" ");
        sb.Append(ArgParentPid).Append('=').Append(ParentPid.ToString(CultureInfo.InvariantCulture)).Append(' ');
        sb.Append(ArgLaunchTick).Append('=').Append(LaunchTick.ToString(CultureInfo.InvariantCulture)).Append(' ');
        sb.Append(ArgSilent).Append('=').Append(Silent ? '1' : '0');
        return sb.ToString();
    }
}
