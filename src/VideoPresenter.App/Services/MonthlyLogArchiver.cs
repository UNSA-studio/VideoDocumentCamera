// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// Required Notice: Copyright (C) 2026 UNSA Studio
using System.Formats.Tar;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace VideoPresenter.App.Services;

/// <summary>Streaming monthly archives; bounded memory and hash verification before deletion.</summary>
public static class MonthlyLogArchiver
{
    private sealed record Digest(long Length, string Sha256);
    public static int Archive(string logDirectory, DateTime today)
    {
        Directory.CreateDirectory(logDirectory);
        string archives = Path.Combine(logDirectory, "Archives");
        Directory.CreateDirectory(archives);
        DateTime monthStart = new(today.Year, today.Month, 1);
        var groups = Directory.EnumerateFiles(logDirectory, "*.log")
            .Where(p => Date(p) < monthStart)
            .GroupBy(p => Date(p).ToString("yyyy-MM", CultureInfo.InvariantCulture)).ToArray();
        int removed = 0;
        foreach (var group in groups)
        {
            string destination = Path.Combine(archives, group.Key + ".tar.gz");
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var existing = File.Exists(destination) ? Manifest(destination) : new Dictionary<string, Digest>();
            var wanted = new Dictionary<string, Digest>(existing);
            var sources = group.ToArray();
            foreach (string path in sources)
            {
                string name = Path.GetFileName(path);
                var digest = FileDigest(path);
                if (wanted.TryGetValue(name, out var previous) && previous != digest)
                    throw new IOException("Archive/source conflict; originals retained: " + name);
                wanted[name] = digest;
            }
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (var gzip = new GZipStream(file, CompressionLevel.Optimal, leaveOpen: true))
                    using (var tar = new TarWriter(gzip, leaveOpen: true))
                    {
                        if (File.Exists(destination))
                        {
                            using var inputFile = File.OpenRead(destination);
                            using var inputGzip = new GZipStream(inputFile, CompressionMode.Decompress);
                            using var inputTar = new TarReader(inputGzip);
                            TarEntry? entry;
                            while ((entry = inputTar.GetNextEntry()) is not null)
                            {
                                Validate(entry);
                                // TarWriter cannot copy a non-seekable gzip entry into another non-seekable gzip stream.
                                // Spool one entry to disk, not an entire month into RAM.
                                using var spool = new FileStream(temporary + ".entry", FileMode.Create, FileAccess.ReadWrite,
                                    FileShare.None, 65536, FileOptions.DeleteOnClose);
                                entry.DataStream!.CopyTo(spool);
                                spool.Position = 0;
                                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, entry.Name) { DataStream = spool });
                            }
                        }
                        foreach (string path in sources.Where(p => !existing.ContainsKey(Path.GetFileName(p))))
                        {
                            using var stream = File.OpenRead(path);
                            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, Path.GetFileName(path)) { DataStream = stream });
                        }
                    }
                    file.Flush(true);
                }
                var verified = Manifest(temporary);
                if (verified.Count != wanted.Count || wanted.Any(p => !verified.TryGetValue(p.Key, out var value) || p.Value != value))
                    throw new IOException("Archive verification failed; originals retained.");
                File.Move(temporary, destination, overwrite: true);
                verified = Manifest(destination);
                foreach (string path in sources)
                    if (verified.TryGetValue(Path.GetFileName(path), out var value) && value == FileDigest(path))
                    {
                        File.Delete(path);
                        removed++;
                    }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return removed;
    }
    private static DateTime Date(string path) => DateTime.TryParseExact(Path.GetFileNameWithoutExtension(path),
        "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : DateTime.MaxValue;
    private static Digest FileDigest(string path)
    {
        using var file = File.OpenRead(path);
        return new(file.Length, Convert.ToHexString(SHA256.HashData(file)));
    }
    private static void Validate(TarEntry entry)
    {
        if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null ||
            entry.Name.IndexOfAny(new[] { '/', '\\' }) >= 0 || !entry.Name.EndsWith(".log", StringComparison.Ordinal))
            throw new InvalidDataException("Unexpected archive entry; originals retained.");
    }
    private static Dictionary<string, Digest> Manifest(string path)
    {
        var result = new Dictionary<string, Digest>(StringComparer.Ordinal);
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        TarEntry? entry;
        while ((entry = tar.GetNextEntry()) is not null)
        {
            Validate(entry);
            result.Add(entry.Name, new(entry.Length, Convert.ToHexString(SHA256.HashData(entry.DataStream!))));
        }
        return result;
    }
}