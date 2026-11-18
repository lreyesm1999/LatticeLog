using LatticeLog.Query;
using LatticeLog.Storage;

namespace LatticeLog.Tests;

/// <summary>Explicit retention: whole sealed segments only, journaled, never the active segment.</summary>
public class RetentionTests
{
    private const long Day = 86_400_000_000_000L;
    private static readonly long Now = 1_800_000_000_000_000_000L;

    /// <summary>One batch per "day": day d records have timestamps now − (10 − d) days.</summary>
    private static (string Root, StoreWriter Writer) TenDays(StoreWriterOptions? options = null)
    {
        var root = TestData.TempDir();
        var w = StoreWriter.Open(root, options ?? new StoreWriterOptions { MinFreeDiskBytes = 0, SegmentMaxBytes = 1 });
        for (var d = 0; d < 10; d++)
        {
            var batch = new List<LogRecord>();
            for (var i = 0; i < 50; i++)
                batch.Add(new LogRecord { Severity = Severity.Info, EventName = "tick", Attributes = [new("day", d)], TimestampUnixNanos = Now - (10 - d) * Day + i });
            w.WriteBatch(batch); // SegmentMaxBytes = 1: every batch rotates into its own segment
        }
        return (root, w);
    }

    private static List<long> Days(string root) => StoreReader.Open(root).ReadAll(new ScanReport()).Select(r => r.Attributes[0].Value.AsInt64()).Distinct().ToList();

    [Fact]
    public void Max_age_deletes_only_segments_entirely_older_than_the_cutoff_and_never_the_active_one()
    {
        var (root, w) = TenDays();
        using (w)
        {
            w.Flush(AckLevel.FsyncConfirmed);
            var dry = w.ApplyRetention(new RetentionPolicy { MaxAge = TimeSpan.FromDays(5.5) }, Now, dryRun: true);
            Assert.True(dry.DryRun);
            Assert.Equal(10, Days(root).Count); // dry run deletes nothing
            var result = w.ApplyRetention(new RetentionPolicy { MaxAge = TimeSpan.FromDays(5.5) }, Now);
            Assert.Equal(dry.Deleted.Select(d => d.SegmentId), result.Deleted.Select(d => d.SegmentId));
            Assert.Equal([0L, 1, 2, 3, 4], result.Deleted.Select(d => d.Records == 50 ? (long)d.FirstSequence / 50 : -1).ToList());
            Assert.Equal([5L, 6, 7, 8, 9], Days(root));
            // Everything older than the cutoff, including the active segment's day: still nothing deletes the active one.
            var all = w.ApplyRetention(new RetentionPolicy { MaxAge = TimeSpan.FromHours(1) }, Now);
            Assert.Equal([9L], Days(root));
            Assert.Equal(4, all.Deleted.Count);
            w.WriteBatch([new LogRecord { Severity = Severity.Info, EventName = "after", Attributes = [new("day", 99)], TimestampUnixNanos = Now }]);
        }
        Assert.Equal([9L, 99], Days(root)); // writing continues normally; sequences stay monotonic
        var manifest = Manifest.Load(root).Entries.Where(e => (string?)e["op"] == "segment_deleted").ToList();
        Assert.Equal(9, manifest.Count);
        Assert.All(manifest, e => Assert.NotNull(e["max_time"]));
    }

    [Fact]
    public void Max_bytes_deletes_oldest_first_until_under_the_limit()
    {
        var (root, w) = TenDays();
        using (w)
        {
            w.Flush(AckLevel.FsyncConfirmed);
            var sizes = StoreReader.Open(root).SegmentIds.Select(id => new FileInfo(StoreLayout.SegmentPath(root, id)).Length).ToList();
            var limit = sizes.Skip(sizes.Count - 3).Sum(); // room for the newest three segments
            var r = w.ApplyRetention(new RetentionPolicy { MaxBytes = limit }, Now);
            Assert.Equal(7, r.Deleted.Count);
            Assert.Equal([7L, 8, 9], Days(root));
        }
    }

    [Fact]
    public void Journaled_deletion_survives_a_crash_before_the_file_is_removed()
    {
        var (root, w) = TenDays();
        ulong victim;
        using (w)
        {
            w.Flush(AckLevel.FsyncConfirmed);
            victim = StoreReader.Open(root).SegmentIds[0];
        }
        // Simulate: journal written, process died before File.Delete.
        Manifest.Append(root, new System.Text.Json.Nodes.JsonObject { ["op"] = "segment_deleted", ["id"] = victim, ["reason"] = "test" });
        Assert.True(File.Exists(StoreLayout.SegmentPath(root, victim)));
        Assert.DoesNotContain(0L, Days(root)); // readers already ignore it
        using (StoreWriter.Open(root, new StoreWriterOptions { MinFreeDiskBytes = 0 })) { }
        Assert.False(File.Exists(StoreLayout.SegmentPath(root, victim))); // recovery removed the leftover
    }

    [Fact]
    public void Automatic_retention_on_rotation_keeps_a_bounded_store_and_queries_tolerate_deletions()
    {
        var root = TestData.TempDir();
        var policy = new RetentionPolicy { MaxBytes = 40_000 };
        using var w = StoreWriter.Open(root, new StoreWriterOptions { MinFreeDiskBytes = 0, SegmentMaxBytes = 8_000, Retention = policy, BackgroundDurability = false });
        var td = new TestData(4);
        var failures = 0;
        for (var i = 0; i < 60; i++)
        {
            w.WriteBatch(td.Records(40));
            try
            {
                var engine = QueryEngine.Open(root);
                engine.Execute(new QueryRequest { Limit = 10_000 });
                engine.Aggregate(new QueryRequest(), "level");
            }
            catch (Exception) { failures++; }
        }
        Assert.Equal(0, failures);
        var total = Directory.GetFiles(Path.Combine(root, StoreLayout.SegmentsDir), "*.llog").Sum(f => new FileInfo(f).Length);
        Assert.True(total <= 40_000 + 2 * 8_000 + 64_000, $"store grew to {total} bytes"); // limit + the active/just-rotated segments
        Assert.Contains(Manifest.Load(root).Entries, e => (string?)e["op"] == "segment_deleted");
        var seqs = StoreReader.Open(root).ReadAll(new ScanReport()).Select(r => r.Sequence).ToList();
        Assert.Equal(seqs.OrderBy(s => s), seqs);
        Assert.Equal(w.LastSequence, seqs[^1]); // newest data always kept
    }
}
