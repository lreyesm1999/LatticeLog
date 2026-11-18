using LatticeLog.Storage;
using LatticeLog.Storage.Format;

namespace LatticeLog.Tests;

/// <summary>Fault injection: torn writes, crashes, corruption, fuzzing (P05-T10, P05-T15, P06-T06, P06-T08).</summary>
public class DurabilityTests
{
    private static (string Path, List<LogRecord> Expected, byte[] Bytes) UnsealedSegment(int seed, int count, int perBlock)
    {
        var td = new TestData(seed);
        var records = td.Records(count);
        TestData.AssignSequences(records);
        var expected = records.Select(r => r.ShallowClone()).ToList();
        var dir = TestData.TempDir();
        var path = Path.Combine(dir, "u.llog");
        var w = new SegmentWriter(path, 1, new SegmentWriterOptions { MaxRecordsPerBlock = perBlock });
        w.Append(records, 0, records.Count);
        w.FlushToOs();
        w.Dispose(); // simulated crash: no footer
        return (path, expected, File.ReadAllBytes(path));
    }

    [Fact]
    public void Truncation_at_every_offset_never_yields_invalid_records()
    {
        var (path, expected, bytes) = UnsealedSegment(11, 60, 7);
        var dir = Path.GetDirectoryName(path)!;
        // Every offset within 40 bytes of each frame boundary (where torn writes matter most) plus 400 random offsets.
        var cuts = new SortedSet<int> { Llog.FileHeaderSize, bytes.Length };
        using (var full = SegmentReader.Open(path))
        {
            var idx = full.RebuildIndex(new ScanReport());
            foreach (var b in idx.Blocks)
                for (var d = -40; d <= 40; d++)
                {
                    var c = (int)b.Offset + d;
                    if (c >= Llog.FileHeaderSize && c <= bytes.Length) cuts.Add(c);
                    var e = (int)(b.Offset + b.Length) + d;
                    if (e >= Llog.FileHeaderSize && e <= bytes.Length) cuts.Add(e);
                }
        }
        var rnd = new Random(5);
        for (var i = 0; i < 400; i++) cuts.Add(rnd.Next(Llog.FileHeaderSize, bytes.Length + 1));
        foreach (var cut in cuts)
        {
            var p = Path.Combine(dir, $"cut-{cut}.llog");
            File.WriteAllBytes(p, bytes.AsSpan(0, cut).ToArray());
            using var reader = SegmentReader.Open(p);
            Assert.False(reader.IsSealed);
            var report = new ScanReport();
            var got = reader.ReadBlocks(report).SelectMany(b => b.Records).ToList();
            Assert.True(got.Count <= expected.Count);
            for (var i = 0; i < got.Count; i++) Assert.Equal(expected[i], got[i]);
            if (cut < bytes.Length && got.Count < expected.Count && cut > Llog.FileHeaderSize)
            {
                // A cut inside a block must be reported unless it falls exactly on a block boundary.
                var boundaryOk = report.IsClean;
                if (boundaryOk) Assert.Equal(cut, new SegmentReader[] { reader }[0].FindValidPrefixEnd(new ScanReport()));
            }
            File.Delete(p);
        }
    }

    [Fact]
    public void Store_recovers_torn_tail_and_continues()
    {
        var root = TestData.TempDir();
        var td = new TestData(5);
        var batch1 = td.Records(500);
        using (var w = StoreWriter.Open(root))
        {
            w.WriteBatch(batch1);
            w.Flush(AckLevel.FsyncConfirmed);
            // simulate a crash: skip Dispose (no seal) but release the lock by abandoning the object
            typeof(StoreWriter).GetField("_lock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(w).As<FileStream>().Dispose();
            var cur = typeof(StoreWriter).GetField("_current", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            ((SegmentWriter)cur.GetValue(w)!).Dispose();
            cur.SetValue(w, null);
        }
        // Append garbage simulating a torn frame.
        var seg = Directory.GetFiles(Path.Combine(root, "segments"), "*.llog").Single();
        using (var fs = new FileStream(seg, FileMode.Append)) fs.Write(new byte[] { 3, 1, 0, 0, 0x10, 0, 0 });
        using (var w = StoreWriter.Open(root))
        {
            Assert.Single(w.Recovery);
            Assert.Equal(7, w.Recovery[0].TruncatedBytes);
            Assert.NotNull(w.Recovery[0].TailCopy);
            Assert.Equal(500UL, w.LastSequence);
            var batch2 = new TestData(6).Records(10);
            w.WriteBatch(batch2);
            Assert.Equal(510UL, w.LastSequence);
        }
        var reader = StoreReader.Open(root);
        var rep = new ScanReport();
        var all = reader.ReadAll(rep).ToList();
        Assert.True(rep.IsClean, string.Join("\n", rep.Issues));
        Assert.Equal(510, all.Count);
        Assert.Equal(Enumerable.Range(1, 510).Select(i => (ulong)i), all.Select(r => r.Sequence));
        Assert.Contains(Manifest.Load(root).Entries, e => (string?)e["op"] == "segment_recovered");
    }

    [Fact]
    public void Second_writer_process_is_rejected()
    {
        var root = TestData.TempDir();
        using var w = StoreWriter.Open(root);
        Assert.Throws<StoreLockedException>(() => StoreWriter.Open(root));
    }

    [Fact]
    public void Random_corruption_is_detected_and_never_returns_garbage()
    {
        var td = new TestData(99);
        var records = td.Records(800);
        TestData.AssignSequences(records);
        var expected = records.ToDictionary(r => r.Sequence, r => r.ShallowClone());
        var dir = TestData.TempDir();
        var path = Path.Combine(dir, "c.llog");
        using (var w = new SegmentWriter(path, 1, new SegmentWriterOptions { MaxRecordsPerBlock = 50 }))
        {
            w.Append(records, 0, records.Count);
            w.Seal();
        }
        var original = File.ReadAllBytes(path);
        var rnd = new Random(1234);
        for (var iter = 0; iter < 300; iter++)
        {
            var bytes = (byte[])original.Clone();
            var flips = rnd.Next(1, 4);
            for (var f = 0; f < flips; f++)
            {
                var pos = rnd.Next(Llog.FileHeaderSize, bytes.Length);
                bytes[pos] ^= (byte)(1 << rnd.Next(8));
            }
            var p = Path.Combine(dir, $"fz-{iter}.llog");
            File.WriteAllBytes(p, bytes);
            try
            {
                using var reader = SegmentReader.Open(p);
                var report = new ScanReport();
                var got = reader.ReadBlocks(report).SelectMany(b => b.Records).ToList();
                foreach (var r in got)
                {
                    Assert.True(expected.TryGetValue(r.Sequence, out var e), $"iter {iter}: unexpected sequence {r.Sequence}");
                    Assert.True(e!.Equals(r), $"iter {iter}: corrupted record returned: {TestData.Describe(e, r)}");
                }
                if (got.Count < records.Count) Assert.False(report.IsClean, $"iter {iter}: lost records without a reported issue");
            }
            catch (LlogFormatException) { /* acceptable: explicit refusal */ }
            File.Delete(p);
        }
    }

    [Fact]
    public void Partial_read_skips_corrupt_block_and_reports_lost_records()
    {
        var td = new TestData(8);
        var records = td.Records(300);
        TestData.AssignSequences(records);
        var path = Path.Combine(TestData.TempDir(), "p.llog");
        SegmentIndex idx;
        using (var w = new SegmentWriter(path, 1, new SegmentWriterOptions { MaxRecordsPerBlock = 100 }))
        {
            w.Append(records, 0, records.Count);
            idx = w.Seal();
        }
        var blocks = idx.Blocks.Where(b => b.Type == BlockType.Records).ToList();
        var bytes = File.ReadAllBytes(path);
        bytes[blocks[1].Offset + Llog.FrameHeaderSize + 5] ^= 0xFF; // corrupt payload of the 2nd block
        File.WriteAllBytes(path, bytes);
        using var reader = SegmentReader.Open(path);
        var report = new ScanReport();
        var got = reader.ReadBlocks(report).SelectMany(b => b.Records).ToList();
        Assert.Equal(200, got.Count);
        var issue = Assert.Single(report.Issues);
        Assert.Equal(IssueKind.CorruptPayload, issue.Kind);
        Assert.Equal(100, issue.LostRecords);
    }

    [Fact]
    public void Quota_refuses_writes_instead_of_deleting()
    {
        var root = TestData.TempDir();
        using var w = StoreWriter.Open(root, new StoreWriterOptions { MaxStoreBytes = 4096, MinFreeDiskBytes = 0 });
        var td = new TestData(1);
        Assert.Throws<StoreQuotaExceededException>(() =>
        {
            for (var i = 0; i < 100; i++) w.WriteBatch(td.Records(50));
        });
    }

    [Fact]
    public void Rotation_produces_ordered_sealed_segments()
    {
        var root = TestData.TempDir();
        var td = new TestData(2);
        using (var w = StoreWriter.Open(root, new StoreWriterOptions { SegmentMaxBytes = 20_000 }))
        {
            for (var i = 0; i < 20; i++) w.WriteBatch(td.Records(100));
            Assert.True(w.SegmentsSealed > 2);
        }
        var reader = StoreReader.Open(root);
        Assert.All(reader.SegmentIds, id => Assert.NotNull(reader.Indexes[id]));
        var rep = new ScanReport();
        var seqs = reader.ReadAll(rep).Select(r => r.Sequence).ToList();
        Assert.Equal(2000, seqs.Count);
        Assert.Equal(seqs.OrderBy(s => s), seqs);
    }
}

internal static class ObjectExtensions
{
    public static T As<T>(this object? o) => (T)o!;
}
