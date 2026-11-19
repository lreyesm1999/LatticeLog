using System.Diagnostics;
using LatticeLog.Core;
using LatticeLog.Storage;

namespace LatticeLog.Tests;

/// <summary>
/// fsync and segment sealing run on the store's durability thread (P14; motivation in reports/latency-v1.md §3).
/// A slow or failing disk is simulated with the internal fault-injection hook.
/// </summary>
public class BackgroundDurabilityTests
{
    private static List<LogRecord> Batch(int seed, int n) => new TestData(seed).Records(n);

    [Fact]
    public void Slow_background_fsync_does_not_block_writes_and_durability_only_advances_when_done()
    {
        var root = TestData.TempDir();
        using var w = StoreWriter.Open(root, new StoreWriterOptions { MinFreeDiskBytes = 0 });
        using var release = new ManualResetEventSlim();
        w.DurabilityFaultInjection = _ => release.Wait(TimeSpan.FromSeconds(10)); // the disk "hangs" until released
        w.WriteBatch(Batch(1, 200));
        Assert.True(w.RequestBackgroundFsync());
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++) w.WriteBatch(Batch(2 + i, 100)); // keeps writing while the fsync hangs
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"writes blocked behind a background fsync: {sw.Elapsed}"); // the fsync hangs 10 s
        Assert.Equal(0UL, w.DurableSequence); // nothing claimed durable while the fsync is in flight
        release.Set();
        var deadline = Stopwatch.StartNew();
        while (w.DurableSequence < 200 && deadline.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(5);
        Assert.Equal(200UL, w.DurableSequence); // covers what was written before the request, not later writes
        Assert.Equal(AckLevel.FsyncConfirmed, w.Flush(AckLevel.FsyncConfirmed));
        Assert.Equal(w.LastSequence, w.DurableSequence);
    }

    [Fact]
    public void Rotation_seals_in_background_and_explicit_flush_waits_for_pending_seals()
    {
        var root = TestData.TempDir();
        using var gate = new ManualResetEventSlim(); // outlives the writer: its final seal on Dispose still runs the hook
        using (var w = StoreWriter.Open(root, new StoreWriterOptions { MinFreeDiskBytes = 0, SegmentMaxBytes = 20_000 }))
        {
            // Every seal blocks until the gate opens. If rotation sealed inline, the writes below could not finish
            // with the gate closed (each seal would wait the full 10 s), so finishing proves the seals are deferred.
            // No wall-clock budget: the assertion does not depend on how fast the machine is.
            var sealsStarted = 0;
            w.DurabilityFaultInjection = kind =>
            {
                if (kind != "seal") return;
                Interlocked.Increment(ref sealsStarted);
                gate.Wait(TimeSpan.FromSeconds(10));
            };
            for (var i = 0; i < 20; i++) w.WriteBatch(Batch(10 + i, 100));
            Assert.True(w.SegmentsSealed > 2);
            Assert.True(Volatile.Read(ref sealsStarted) <= 1, "the writer waited for seals it should have handed off");
            Assert.True(w.PendingDurabilityWork > 0, "rotations should still be queued behind the blocked seal");
            gate.Set();
            Assert.Equal(AckLevel.FsyncConfirmed, w.Flush(AckLevel.FsyncConfirmed));
            Assert.Equal(0, w.PendingDurabilityWork);
            Assert.Equal(w.SegmentsSealed, Volatile.Read(ref sealsStarted));
            Assert.Equal(w.LastSequence, w.DurableSequence);
        }
        var reader = StoreReader.Open(root);
        Assert.All(reader.SegmentIds, id => Assert.NotNull(reader.Indexes[id])); // every segment sealed, footer readable
        var rep = new ScanReport();
        var detail = string.Join("\n", reader.SegmentIds.Select(id =>
        {
            var x = reader.Indexes[id]!;
            using var s = SegmentReader.Open(reader.SegmentPath(id));
            return $"seg {id}: {x.RecordCount} records, seq {x.FirstSequence}-{x.LastSequence}, read {s.ReadBlocks(new ScanReport()).Sum(b => b.Records.Count)}";
        })) + "\nfiles: " + string.Join(", ", Directory.GetFiles(Path.Combine(root, StoreLayout.SegmentsDir)).Select(Path.GetFileName))
          + "\nmanifest: " + string.Join(" | ", Manifest.Load(root).Entries.Select(e => e.ToJsonString()));
        Assert.True(2000 == reader.ReadAll(rep).Count(), detail);
        Assert.True(rep.IsClean);
        Assert.Equal(Manifest.Load(root).Entries.Count(e => (string?)e["op"] == "segment_sealed"), reader.SegmentIds.Count);
    }

    [Fact]
    public void Periodic_fsync_requests_coalesce_while_the_disk_is_slow()
    {
        var root = TestData.TempDir();
        using var w = StoreWriter.Open(root, new StoreWriterOptions { MinFreeDiskBytes = 0 });
        using var release = new ManualResetEventSlim();
        w.DurabilityFaultInjection = _ => release.Wait(TimeSpan.FromSeconds(10));
        for (var i = 0; i < 50; i++)
        {
            w.WriteBatch(Batch(30 + i, 10));
            w.RequestBackgroundFsync();
        }
        Assert.True(w.PendingDurabilityWork <= 2, $"fsync backlog grew to {w.PendingDurabilityWork}"); // one running + one queued
        release.Set();
        Assert.Equal(AckLevel.FsyncConfirmed, w.Flush(AckLevel.FsyncConfirmed));
        Assert.Equal(500UL, w.DurableSequence);
    }

    [Fact]
    public void Background_failure_is_sticky_and_reported_by_the_next_flush()
    {
        var root = TestData.TempDir();
        using var w = StoreWriter.Open(root, new StoreWriterOptions { MinFreeDiskBytes = 0 });
        w.WriteBatch(Batch(3, 50));
        w.DurabilityFaultInjection = _ => throw new IOException("simulated device error");
        Assert.True(w.RequestBackgroundFsync());
        var ex = Assert.Throws<IOException>(() => w.Flush(AckLevel.FsyncConfirmed));
        Assert.Contains("simulated device error", ex.Message);
        Assert.Equal(0UL, w.DurableSequence);
        w.DurabilityFaultInjection = null;
        w.WriteBatch(Batch(4, 10));
        w.RequestBackgroundFsync();
        Assert.Throws<IOException>(() => w.Flush(AckLevel.FsyncConfirmed)); // never silently "recovers" the lost guarantee
        Assert.Equal(0UL, w.DurableSequence);
    }

    [Fact]
    public async Task Balanced_logger_keeps_writing_while_the_disk_hangs_and_flush_still_confirms_honestly()
    {
        var root = TestData.TempDir();
        var store = StoreWriter.Open(root, new StoreWriterOptions { MinFreeDiskBytes = 0 });
        using var release = new ManualResetEventSlim();
        store.DurabilityFaultInjection = _ => release.Wait(TimeSpan.FromSeconds(10));
        await using var logger = new LatticeLogger(store, new LatticeLoggerOptions
        {
            Durability = DurabilityMode.Balanced,
            BalancedFsyncInterval = TimeSpan.FromMilliseconds(1),
            QueueCapacity = 1024,
            BatchMaxRecords = 64,
            FlushOnProcessExit = false,
        });
        for (var i = 0; i < 20_000; i++) logger.Info("x", new LogAttribute("i", i)); // ~20 queue capacities
        var written = await logger.FlushAsync(AckLevel.WrittenToOs);
        Assert.Equal(AckLevel.WrittenToOs, written.Level); // reached while the periodic fsync hangs
        Assert.Equal(0, logger.GetStats().DroppedSaturation);
        Assert.Equal(20_000UL, written.LastSequence);
        var durable = logger.FlushAsync(AckLevel.FsyncConfirmed).AsTask();
        await Task.Delay(200);
        Assert.False(durable.IsCompleted); // an explicit fsync request waits for the disk
        release.Set();
        Assert.Equal(AckLevel.FsyncConfirmed, (await durable).Level);
    }
}
