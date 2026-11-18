using LatticeLog.Storage;
using LatticeLog.Storage.Format;

namespace LatticeLog.Tests;

/// <summary>Incremental store reader used by the forwarder: only new frames are decoded between polls.</summary>
public class StoreTailTests
{
    private static List<LogRecord> Batch(long first, int n)
        => Enumerable.Range(0, n).Select(i => new LogRecord
        {
            Severity = Severity.Info, EventName = "e" + ((first + i) % 7), Attributes = [new("n", first + i)],
            TimestampUnixNanos = 1_700_000_000_000_000_000 + first + i,
            Context = new ContextSnapshot(ContextKind.Resource, null, [new("service.name", "svc" + ((first + i) % 3))], null),
        }).ToList();

    [Fact]
    public async Task Follows_a_live_writer_across_rotations_exactly_once_without_full_scans()
    {
        var root = TestData.TempDir();
        using var w = StoreWriter.Open(root, new StoreWriterOptions { MinFreeDiskBytes = 0, SegmentMaxBytes = 16 * 1024 });
        using var tail = new StoreTail(root);
        const int total = 20_000;
        var writer = Task.Run(() =>
        {
            for (long n = 0; n < total; n += 100)
            {
                w.WriteBatch(Batch(n, 100));
                if (n % 2_000 == 0) w.RequestBackgroundFsync();
            }
        });
        var got = new List<long>();
        ulong cursor = 0;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (got.Count < total && DateTime.UtcNow < deadline)
        {
            var batch = tail.ReadAfter(cursor, 333, out var gap);
            Assert.False(gap);
            foreach (var r in batch) got.Add(r.Attributes[0].Value.AsInt64());
            if (batch.Count > 0) cursor = batch[^1].Sequence;
            else await Task.Delay(1);
        }
        await writer;
        Assert.Equal(Enumerable.Range(0, total).Select(i => (long)i), got); // every record once, in order
        Assert.True(w.SegmentsSealed > 3, "test needs rotations");
        Assert.Equal(0, tail.FullScans);
        Assert.InRange(tail.FramesRead, total / 100, total / 100 * 3); // each frame decoded about once (+ catalog frames)
    }

    [Fact]
    public void Rewinding_the_cursor_and_small_limits_return_correct_runs()
    {
        var root = TestData.TempDir();
        using (var w = StoreWriter.Open(root, new StoreWriterOptions { MinFreeDiskBytes = 0, SegmentMaxBytes = 8 * 1024 }))
            for (long n = 0; n < 2_000; n += 250) w.WriteBatch(Batch(n, 250));
        using var tail = new StoreTail(root);
        var first = tail.ReadAfter(0, 7, out _);
        Assert.Equal([1UL, 2, 3, 4, 5, 6, 7], first.Select(r => r.Sequence));
        var next = tail.ReadAfter(7, 5, out _);
        Assert.Equal([8UL, 9, 10, 11, 12], next.Select(r => r.Sequence));
        var rewound = tail.ReadAfter(3, 4, out var gap); // a server answered with a lower committed_through
        Assert.False(gap);
        Assert.Equal([4UL, 5, 6, 7], rewound.Select(r => r.Sequence));
        var all = tail.ReadAfter(0, 10_000, out _);
        Assert.Equal(Enumerable.Range(1, 2_000).Select(i => (ulong)i), all.Select(r => r.Sequence));
    }

    [Fact]
    public void Corruption_falls_back_to_a_full_scan_and_reports_the_gap()
    {
        var root = TestData.TempDir();
        using (var w = StoreWriter.Open(root, new StoreWriterOptions { MinFreeDiskBytes = 0, Segment = new SegmentWriterOptions { MaxRecordsPerBlock = 100 } }))
            w.WriteBatch(Batch(0, 1_000)); // one segment, 10 record blocks
        var seg = Directory.GetFiles(Path.Combine(root, StoreLayout.SegmentsDir), "*.llog").Single();
        var reader = SegmentReader.Open(seg);
        var third = reader.Index!.Blocks.Where(b => b.Type == BlockType.Records).ElementAt(2);
        reader.Dispose();
        var bytes = File.ReadAllBytes(seg);
        bytes[third.Offset + Llog.FrameHeaderSize + 3] ^= 0xFF; // records 201..300 become unreadable
        File.WriteAllBytes(seg, bytes);

        using var tail = new StoreTail(root) { StallLimit = 3 };
        var collected = new List<ulong>();
        ulong cursor = 0;
        var gaps = 0;
        for (var poll = 0; poll < 50 && cursor < 1_000; poll++)
        {
            var batch = tail.ReadAfter(cursor, 10_000, out var gap);
            if (gap) gaps++;
            collected.AddRange(batch.Select(r => r.Sequence));
            if (batch.Count > 0) cursor = batch[^1].Sequence;
        }
        Assert.Equal(1, gaps);
        Assert.Equal(Enumerable.Range(1, 200).Concat(Enumerable.Range(301, 700)).Select(i => (ulong)i), collected);
        Assert.True(tail.FullScans >= 1);
    }
}
