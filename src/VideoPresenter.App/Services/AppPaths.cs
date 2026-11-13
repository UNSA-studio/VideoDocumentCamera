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
        TryImportLegacyData();

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

    /// <summary>
    /// 迁移提示。非 null 表示本次启动把旧数据从别的目录搬过来了，
    /// App 会在启动日志里写明，设置页也能展示——不许静默搬。
    /// </summary>
    public static string? MigrationNote { get; private set; }

    /// <summary>
    /// 把旧数据目录里的内容搬回当前数据目录。
    ///
    /// <para><b>为什么需要这一步</b></para>
    /// <para>
    /// 2026-11-12 那次混淆故障期间，程序误判"安装目录不可写"，
    /// 于是数据被写进了 %LOCALAPPDATA%。混淆修好之后程序又回到便携模式，
    /// 但它只看 &lt;安装目录&gt;\Users —— 用户之前拍的照片、保存的设置
    /// 会"凭空消失"。所以启动时若便携目录还是空的，就去旧位置捞一次。
    /// </para>
    /// <para>
    /// 只搬不删：源目录保持原样，用户可以自己核对后再清理。
    /// 只在目标为空时才搬，绝不覆盖新版已经写入的数据。
    /// </para>
    /// </summary>
    private static void TryImportLegacyData()
    {
        try
        {
            string target = Path.Combine(UsersRoot, UserKey);

            // 目标已有实质内容 → 这是老用户，不需要迁移
            if (HasPayload(target)) return;

            foreach (string legacyRoot in EnumerateLegacyRoots())
            {
                if (string.Equals(Path.GetFullPath(legacyRoot), Path.GetFullPath(UsersRoot),
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                string source = Path.Combine(legacyRoot, UserKey);
                if (!HasPayload(source)) continue;

                CopyDirectory(source, target);
                MigrationNote = $"已从旧数据目录迁移：{source} → {target}";
                return;
            }
        }
        catch (Exception ex)
        {
            // 迁移是尽力而为：失败绝不能挡住启动，否则又变成"打不开的程序"
            MigrationNote = $"旧数据迁移失败（不影响启动）：{ex.Message}";
        }
    }

    /// <summary>候选的旧数据根目录，按可信度排序。</summary>
    private static IEnumerable<string> EnumerateLegacyRoots()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
            yield return Path.Combine(local, "UNSA Studio", "VideoDocumentCamera", "Data", "Users");

        yield return Path.Combine(Path.GetTempPath(), "VideoDocumentCamera", "Users");
    }

    /// <summary>目录里有没有"真东西"（Media 下有文件，或存在 settings.json）。</summary>
    private static bool HasPayload(string profileDir)
    {
        if (!Directory.Exists(profileDir)) return false;
        if (File.Exists(Path.Combine(profileDir, "Config", "settings.json"))) return true;
        try
        {
            return Directory.EnumerateFiles(profileDir, "*", SearchOption.AllDirectories).Any();
        }
        catch
        {
            return false;
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(source, target, StringComparison.OrdinalIgnoreCase));
        }
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string dest = file.Replace(source, target, StringComparison.OrdinalIgnoreCase);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            // 目标已存在就跳过，绝不覆盖当前版本的数据
            if (!File.Exists(dest)) File.Copy(file, dest);
        }
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