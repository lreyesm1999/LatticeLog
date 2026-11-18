using LatticeLog.Storage.Format;

namespace LatticeLog.Storage;

public sealed record BackupResult(string Destination, int Segments, long Bytes, long Records, bool Verified, IReadOnlyList<string> Issues);

/// <summary>
/// Hot backup (P24): copies a store while a writer may still be appending. Sealed segments are immutable and copied
/// as they are; the active segment is copied up to its last complete block (the copy is an unsealed segment that
/// recovery seals on restore). The manifest is copied first, so a segment retired during the copy simply is not in
/// the backup's view. The result is verified by reading it back. Restore = copy the directory back (writer stopped).
/// </summary>
public static class StoreBackup
{
    public static BackupResult Create(string root, string destination)
    {
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new IOException($"backup destination '{destination}' is not empty");
        Directory.CreateDirectory(Path.Combine(destination, StoreLayout.SegmentsDir));
        long bytes = 0;
        foreach (var name in new[] { StoreLayout.StoreFile, StoreLayout.ManifestFile, "INGEST-CHECKPOINT.json" })
        {
            var src = Path.Combine(root, name);
            if (!File.Exists(src)) continue;
            File.Copy(src, Path.Combine(destination, name));
            bytes += new FileInfo(src).Length;
        }
        var reader = StoreReader.Open(root);
        var segments = 0;
        foreach (var id in reader.SegmentIds)
        {
            var src = reader.SegmentPath(id);
            var dst = StoreLayout.SegmentPath(destination, id);
            try
            {
                if (reader.Indexes.GetValueOrDefault(id) is not null)
                {
                    File.Copy(src, dst);
                    bytes += new FileInfo(dst).Length;
                }
                else
                {
                    long end;
                    using (var seg = SegmentReader.Open(src)) end = seg.FindValidPrefixEnd(new ScanReport());
                    using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var output = new FileStream(dst, FileMode.CreateNew, FileAccess.Write);
                    CopyPrefix(input, output, end);
                    output.Flush(flushToDisk: true);
                    bytes += end;
                }
                segments++;
            }
            catch (FileNotFoundException) { /* retired by compaction/retention after the snapshot: not part of it */ }
        }
        // Verify the copy as a reader would.
        var report = new ScanReport();
        var records = StoreReader.Open(destination).ReadAll(report).LongCount();
        return new BackupResult(destination, segments, bytes, records, report.IsClean, report.Issues.Select(i => i.Message).ToList());
    }

    private static void CopyPrefix(Stream input, Stream output, long length)
    {
        var buffer = new byte[1 << 20];
        var left = length;
        while (left > 0)
        {
            var n = input.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
            if (n == 0) throw new IOException("segment shrank during backup");
            output.Write(buffer, 0, n);
            left -= n;
        }
    }
}
