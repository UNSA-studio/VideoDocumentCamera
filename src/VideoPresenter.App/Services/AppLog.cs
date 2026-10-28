// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// Required Notice: Copyright (C) 2026 UNSA Studio
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace VideoPresenter.App.Services;

/// <summary>Daily, append-only diagnostic log. Fatal records are flushed synchronously.</summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static readonly Lazy<Mutex> FileMutex = new(() => new Mutex(false, "UNSA_VDC_Log_" + Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.Profile)))[..24]));
    private static readonly string SessionId = Guid.NewGuid().ToString("N");
    private static readonly int Pid = Environment.ProcessId;
    private static readonly string Started = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);
    private static System.Threading.Timer? _maintenance;
    private static int _archiveBusy;
    private static int _initialized;
    private static string Marker => Path.Combine(AppPaths.Temp, "Sessions", $"{Pid}-{SessionId}.json");
    public static string CurrentFile => Path.Combine(AppPaths.Logs, DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");
    public static string? LastWriteError { get; private set; }

    public static void Initialize()
    {
        AppPaths.Initialize(); // Throw visibly on ACL/disk errors. No hidden AppData fallback.
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Marker)!);
        foreach (string stale in Directory.EnumerateFiles(Path.GetDirectoryName(Marker)!, "*.json"))
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(stale));
                int oldPid = json.RootElement.GetProperty("pid").GetInt32();
                string oldStart = json.RootElement.GetProperty("processStartUtc").GetString()!;
                bool alive;
                try
                {
                    using var process = Process.GetProcessById(oldPid);
                    alive = process.StartTime.ToUniversalTime().ToString("O") == oldStart;
                }
                catch (ArgumentException) { alive = false; }
                if (alive) continue;
                Write("Previous session ended without a clean shutdown. This alone does not identify a crash cause. Marker=" + File.ReadAllText(stale), "WARN", "Session");
                File.Move(stale, Path.Combine(AppPaths.Crashes, Path.GetFileName(stale)), overwrite: true);
            }
            catch (Exception ex) { Error("Cannot inspect previous session marker " + stale, ex); }
        }
        using (var process = Process.GetCurrentProcess())
            File.WriteAllText(Marker, JsonSerializer.Serialize(new { pid = Pid, session = SessionId,
                started = Started, processStartUtc = process.StartTime.ToUniversalTime().ToString("O") }));
        Write($"START version={Assembly.GetEntryAssembly()?.GetName().Version}; runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; " +
            $"os={Environment.OSVersion}; arch={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}; " +
            $"processors={Environment.ProcessorCount}; base={AppPaths.InstallRoot}; userData={AppPaths.Profile}; nativeDumps={AppPaths.NativeDumps}", "INFO", "Session");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Fatal("AppDomain.UnhandledException terminating=" + e.IsTerminating, e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Error("TaskScheduler.UnobservedTaskException", e.Exception); e.SetObserved(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Write("ProcessExit (clean shutdown requires the explicit close record)", "INFO", "Session");
        ArchiveInBackground();
        _maintenance = new System.Threading.Timer(_ => ArchiveInBackground(), null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        // Import only the small legacy config/log, never delete the originals or silently move photos.
        ImportLegacy();
    }

    public static void Write(string message, string level = "INFO", string category = "App")
    {
        try
        {
            DateTimeOffset now = DateTimeOffset.Now;
            string header = $"{now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] [{category}] [pid={Pid} tid={Environment.CurrentManagedThreadId} session={SessionId}] ";
            string text = header + message.Replace("\r\n", "\n").Replace("\n", "\n" + header + "| ") + Environment.NewLine;
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Logs);
                bool owns;
                try { owns = FileMutex.Value.WaitOne(TimeSpan.FromSeconds(3)); }
                catch (AbandonedMutexException) { owns = true; }
                if (!owns) throw new IOException("Timed out acquiring daily log mutex.");
                try
                {
                // Always close the stream; a crash loses at most the current in-flight write.
                using var stream = new FileStream(Path.Combine(AppPaths.Logs, now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log"),
                    FileMode.Append, FileAccess.Write, FileShare.Read);
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                stream.Write(bytes);
                stream.Flush(flushToDisk: level == "FATAL");
                LastWriteError = null;
                }
                finally { FileMutex.Value.ReleaseMutex(); }
            }
        }
        catch (Exception ex)
        {
            LastWriteError = ex.ToString();
            System.Diagnostics.Debug.WriteLine("LOG WRITE FAILED (no C: fallback): " + ex);
        }
    }

    public static void Error(string operation, Exception ex) => Write(
        operation + $"; HRESULT=0x{ex.HResult:X8}\n" + ex, "ERROR", "Exception");

    public static void Fatal(string operation, Exception? ex)
    {
        string detail = operation + (ex is null ? "" : $"; HRESULT=0x{ex.HResult:X8}\n{ex}");
        Write(detail, "FATAL", "Crash");
        try
        {
            Directory.CreateDirectory(AppPaths.Crashes);
            string path = Path.Combine(AppPaths.Crashes, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Pid}-{Guid.NewGuid():N}.txt");
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            byte[] bytes = Encoding.UTF8.GetBytes($"started={Started}\npid={Pid}\nsession={SessionId}\n{detail}\n");
            stream.Write(bytes);
            stream.Flush(true);
        }
        catch (Exception failure) { System.Diagnostics.Debug.WriteLine("CRASH WRITE FAILED: " + failure); }
    }

    public static void CompleteSession()
    {
        Write("CLEAN_SHUTDOWN", "INFO", "Session");
        try { if (File.Exists(Marker)) File.Delete(Marker); }
        catch (Exception ex) { Error("Remove session marker", ex); }
        _maintenance?.Dispose();
    }

    private static void ArchiveInBackground()
    {
        if (Interlocked.Exchange(ref _archiveBusy, 1) != 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                using var mutex = new Mutex(false, "UNSA_VDC_Archive_" + Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.Profile)))[..24]);
                bool owns;
                try { owns = mutex.WaitOne(TimeSpan.Zero); } catch (AbandonedMutexException) { owns = true; }
                if (!owns) return;
                try
                {
                    int count = MonthlyLogArchiver.Archive(AppPaths.Logs, DateTime.Now);
                    if (count > 0) Write($"Archived and verified {count} daily log(s) of completed months.", "INFO", "Archive");
                }
                finally { mutex.ReleaseMutex(); }
            }
            catch (Exception ex) { Error("Monthly archive failed; source logs retained", ex); }
            finally { Interlocked.Exchange(ref _archiveBusy, 0); }
        });
    }

    private static void ImportLegacy()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UNSA Studio", "VideoDocumentCamera");
            string once = Path.Combine(AppPaths.Migration, "legacy-import-completed.txt");
            if (File.Exists(once)) return;
            if (!File.Exists(AppPaths.SettingsFile) && File.Exists(Path.Combine(legacy, "settings.json")))
                File.Copy(Path.Combine(legacy, "settings.json"), AppPaths.SettingsFile);
            if (File.Exists(Path.Combine(legacy, "logs", "vdc.log")))
                File.Copy(Path.Combine(legacy, "logs", "vdc.log"), Path.Combine(AppPaths.Migration, "legacy-vdc.log"), overwrite: true);
            File.WriteAllText(once, "Imported legacy config/log only. Originals and old pictures were not deleted.\n" + DateTimeOffset.Now.ToString("O"));
            Write("Legacy config/log import checked. Old photo/video files were not moved or deleted.", "INFO", "Migration");
        }
        catch (Exception ex) { Error("Legacy import", ex); }
    }
}