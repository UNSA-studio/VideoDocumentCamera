// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// Required Notice: Copyright (C) 2026 UNSA Studio
using System.IO;
using System.Security.Principal;

namespace VideoPresenter.App.Services;

/// <summary>Application-owned data stays beside the executable. Never silently fall back to C:.</summary>
public static class AppPaths
{
    public static string InstallRoot => Path.GetFullPath(AppContext.BaseDirectory);
    public static string UsersRoot => Path.Combine(InstallRoot, "Users");
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
}
