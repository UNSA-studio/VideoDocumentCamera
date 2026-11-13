// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// Required Notice: Copyright (C) 2026 UNSA Studio
using System.IO;
using System.Security.Principal;

namespace VideoPresenter.App.Services;

/// <summary>
/// 应用数据根目录解析。
///
/// 布局约定（便携优先，但绝不因为只读而启动失败）：
///   ① 安装目录可写 → &lt;安装目录&gt;\Users —— 数据跟着程序走，整目录拷走即完整
///   ② 安装目录确实写不进去（只读介质、组策略收紧 ACL、被其他程序占用）
///      → %LOCALAPPDATA%\UNSA Studio\VideoDocumentCamera\Data\Users
///   ③ 连用户配置目录也写不了（极端情况）
///      → %TEMP%\VideoDocumentCamera\Users
///
/// ⚠ 历史教训（2026-11-12）：不要把"装在 Program Files"当成不可写的判据。
///   实测 D:\Program Files 下普通用户照样可写，而且当时那次"不可写"是
///   Obfuscar 混淆改坏了本文件的路径字面量导致的误报。
///   因此这里唯一可信的判据是 CanWrite() 的真实探针写入，不看路径猜。
///
/// 回退不静默：<see cref="FallbackReason"/> 非空时启动会写 WARN 日志，
/// 设置页展示真实数据目录，用户自己能找到文件。
/// 三个候选都写不了才抛异常——那时确实无处可放，必须让用户看见错误。
/// </summary>
public static class AppPaths
{
    public static string InstallRoot => Path.GetFullPath(AppContext.BaseDirectory);

    private static string? _usersRoot;
    private static string? _fallbackReason;
    private static bool _portable = true;

    /// <summary>数据根目录（Users 树的父目录），首次访问时探测确定。</summary>
    public static string UsersRoot => _usersRoot ??= ResolveUsersRoot();

    /// <summary>true 表示数据在安装目录旁（便携）；false 表示已回退到用户目录。</summary>
    public static bool IsPortable => _portable;

    /// <summary>回退原因；null 表示未回退。</summary>
    public static string? FallbackReason => _fallbackReason;

    private static readonly string ResolvedUserKey = ResolveUserKey();
    public static string UserKey => ResolvedUserKey;

    private static string ResolveUserKey()
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? throw new InvalidOperationException("Cannot determine Windows user SID.");
        }
        // Used only by the cross-platform diagnostics test harness.
        return "test-user";
    }

    private static string ResolveUsersRoot()
    {
        string portable = Path.Combine(InstallRoot, "Users");
        if (CanWrite(portable))
        {
            _portable = true;
            _fallbackReason = null;
            return portable;
        }

        string reason = $"安装目录不可写：{portable}";
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
        {
            string candidate = Path.Combine(local, "UNSA Studio", "VideoDocumentCamera", "Data", "Users");
            if (CanWrite(candidate))
            {
                _portable = false;
                _fallbackReason = reason;
                return candidate;
            }
        }

        string temp = Path.Combine(Path.GetTempPath(), "VideoDocumentCamera", "Users");
        if (CanWrite(temp))
        {
            _portable = false;
            _fallbackReason = reason + "；临时目录兜底，重启后可能被清理";
            return temp;
        }

        // 无处可写：交回便携路径，让 Initialize 抛出具名错误。
        _portable = true;
        _fallbackReason = reason + "；且无任何可写回退目录";
        return portable;
    }

    /// <summary>目录可创建且能真实落盘一个探针文件，才算可写。</summary>
    private static bool CanWrite(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".write-test-" + Guid.NewGuid().ToString("N"));
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(1);
                stream.Flush(true);
            }
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string Profile => Path.Combine(UsersRoot, UserKey);
    public static string Config => Path.Combine(Profile, "Config");
    public static string SettingsFile => Path.Combine(Config, "settings.json");
    public static string Logs => Path.Combine(Profile, "Logs");
    public static string Archives => Path.Combine(Logs, "Archives");
    public static string Crashes => Path.Combine(Profile, "Crashes");
    public static string NativeDumps => Path.Combine(UsersRoot, "Shared", "CrashDumps");
    public static string Photos => Path.Combine(Profile, "Media", "Photos");
    public static string Scans => Path.Combine(Profile, "Media", "Scans");
    public static string Recordings => Path.Combine(Profile, "Media", "Recordings");
    public static string Cache => Path.Combine(Profile, "Cache");
    public static string Temp => Path.Combine(Profile, "Temp");
    public static string Migration => Path.Combine(Profile, "Migration");
    public static string SessionFile => Path.Combine(Profile, "session.json");

    public static void Initialize()
    {
        foreach (var path in new[] { Config, Logs, Archives, Crashes, Photos, Scans, Recordings, Cache, Temp, Migration })
            Directory.CreateDirectory(path);
        Environment.SetEnvironmentVariable("TEMP", Temp);
        Environment.SetEnvironmentVariable("TMP", Temp);
        Environment.SetEnvironmentVariable("TMPDIR", Temp);
        string probe = Path.Combine(Profile, ".write-test-" + Guid.NewGuid().ToString("N"));
        using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.WriteByte(1);
            stream.Flush(true);
        }
        File.Delete(probe);
    }

    public static bool IsInsideUsers(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(UsersRoot)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    public static string PhotoDirectory(string? configured)
    {
        string path = !string.IsNullOrWhiteSpace(configured) && IsInsideUsers(configured) ? configured : Photos;
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>供启动日志使用的一句话说明，让用户一眼看出数据到底写在哪。</summary>
    public static string DescribeLayout() => IsPortable
        ? $"数据目录（便携）：{UsersRoot}"
        : $"数据目录（已回退）：{UsersRoot}；原因：{FallbackReason}";
}