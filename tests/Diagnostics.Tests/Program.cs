using System.Formats.Tar;
using System.IO.Compression;
using VideoPresenter.App.Services;

static void Check(bool ok, string name)
{
    if (!ok) throw new Exception("FAIL " + name);
    Console.WriteLine("PASS " + name);
}
await NativeLifecycleTests.RunAsync();
AppPaths.Initialize();
string root = Path.Combine(AppPaths.Temp, "diagnostics-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    Check(AppPaths.IsInsideUsers(AppPaths.Photos), "photos beneath Users");
    Check(!AppPaths.IsInsideUsers(Path.GetFullPath(Path.Combine(AppPaths.UsersRoot, "..", "outside"))), "reject traversal");
    Check(!AppPaths.IsInsideUsers(AppPaths.UsersRoot + "-other/test"), "reject sibling prefix");
    // 数据根目录必须可用；回退发生时必须留下可读的原因，不能静默改位置
    Check(Directory.Exists(AppPaths.UsersRoot), "users root exists");
    Check(AppPaths.IsPortable || AppPaths.FallbackReason is not null, "fallback is explained");
    Check(AppPaths.DescribeLayout().Length > 0, "layout is describable");
    string logs = Path.Combine(root, "Logs");
    Directory.CreateDirectory(logs);
    File.WriteAllText(Path.Combine(logs, "2026-09-01.log"), "test α 一\n");
    File.WriteAllText(Path.Combine(logs, "2026-09-30.log"), "last day\n");
    File.WriteAllText(Path.Combine(logs, "2026-10-01.log"), "current month\n");
    Check(MonthlyLogArchiver.Archive(logs, new DateTime(2026, 10, 28)) == 2, "archive completed month");
    Check(File.Exists(Path.Combine(logs, "2026-10-01.log")), "retain current month");
    using (var stream = new GZipStream(File.OpenRead(Path.Combine(logs, "Archives", "2026-09.tar.gz")), CompressionMode.Decompress))
    using (var tar = new TarReader(stream))
    {
        var found = new Dictionary<string, string>();
        TarEntry? entry;
        while ((entry = tar.GetNextEntry()) is not null)
        {
            using var reader = new StreamReader(entry.DataStream!, leaveOpen: true);
            found.Add(entry.Name, reader.ReadToEnd());
        }
        Check(found.Count == 2 && found["2026-09-01.log"] == "test α 一\n", "archive Unicode integrity");
    }
    Check(MonthlyLogArchiver.Archive(logs, new DateTime(2026, 10, 28)) == 0, "idempotent retry");
    File.WriteAllText(Path.Combine(logs, "2026-09-15.log"), "late import\n");
    Check(MonthlyLogArchiver.Archive(logs, new DateTime(2026, 10, 28)) == 1, "merge late import");
    File.WriteAllText(Path.Combine(logs, "2026-09-01.log"), "conflict");
    bool conflict = false;
    try { MonthlyLogArchiver.Archive(logs, new DateTime(2026, 10, 28)); } catch (IOException) { conflict = true; }
    Check(conflict && File.Exists(Path.Combine(logs, "2026-09-01.log")), "conflict retains originals");
    string token = Guid.NewGuid().ToString("N");
    Parallel.For(0, 100, i => AppLog.Write(token + "-" + i));
    Check(File.ReadAllText(AppLog.CurrentFile).Split(token).Length - 1 == 100, "100 concurrent log records");
    Check(Path.GetFileName(AppLog.CurrentFile) == DateTime.Now.ToString("yyyy-MM-dd") + ".log", "daily filename");
    AppLog.Fatal("fatal-test", new IOException("outer-test", new InvalidOperationException("inner-test")));
    string text = File.ReadAllText(AppLog.CurrentFile);
    Check(text.Contains("outer-test") && text.Contains("inner-test") && text.Contains("HRESULT=0x"), "fatal exception details");
    Console.WriteLine("ALL DIAGNOSTICS TESTS PASSED");
}
finally { Directory.Delete(root, recursive: true); }
