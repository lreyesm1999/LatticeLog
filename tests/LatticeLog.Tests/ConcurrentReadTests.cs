using LatticeLog.Query;
using LatticeLog.Storage;
using LatticeLog.Storage.Format;

namespace LatticeLog.Tests;

/// <summary>Readers on a store that is being written (server: queries during ingestion, P17-T16).</summary>
public class ConcurrentReadTests
{
    [Fact]
    public async Task Queries_and_aggregations_while_the_store_is_written_never_fail()
    {
        var root = TestData.TempDir();
        using var writer = StoreWriter.Open(root, new StoreWriterOptions { MinFreeDiskBytes = 0, SegmentMaxBytes = 32 * 1024 });
        var desc = new EventDescriptor(7, "order.placed", 1, Severity.Info, null, [new EventField("i", ValueKind.Int64)]);
        var ctx = new ContextSnapshot(ContextKind.Resource, null, [new("service.name", "svc")], null);
        using var stop = new CancellationTokenSource();
        var write = Task.Run(() =>
        {
            long n = 0;
            while (!stop.IsCancellationRequested)
            {
                var batch = new List<LogRecord>();
                for (var i = 0; i < 200; i++, n++)
                    batch.Add(new LogRecord { Severity = i % 10 == 0 ? Severity.Error : Severity.Info, Event = i % 3 == 0 ? desc : null, EventName = i % 3 == 0 ? null : "free.text", Attributes = [new("i", n)], Context = ctx, TimestampUnixNanos = 1_700_000_000_000_000_000 + n });
                writer.WriteBatch(batch);
                if (n % 2000 == 0) writer.RequestBackgroundFsync();
            }
        });
        var failures = new List<string>();
        var deadline = DateTime.UtcNow.AddSeconds(4);
        var queries = 0;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var engine = QueryEngine.Open(root);
                foreach (var by in new[] { "event", "level", "service" })
                    engine.Aggregate(new QueryRequest(), by);
                engine.Execute(new QueryRequest { Where = "level >= ERROR", Limit = 50 });
                queries++;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures.Add(ex.ToString());
                if (failures.Count >= 3) break;
            }
        }
        stop.Cancel();
        await write;
        Assert.True(failures.Count == 0, $"{failures.Count} failure(s) in {queries} rounds:\n" + string.Join("\n----\n", failures));
        Assert.True(queries > 5);
    }

    [Fact]
    public void A_new_segment_is_never_visible_before_its_header()
    {
        // The writer stages a new segment as *.llog.tmp and renames it once the header is written, so a reader that
        // lists the directory between "file created" and "header written" cannot pick up an empty segment.
        var root = TestData.TempDir();
        var path = StoreLayout.SegmentPath(root, 42);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path + ".tmp", [1, 2, 3]); // a stale staging file left by a crash is replaced, not an error
        using (var w = new SegmentWriter(path, 42, publishAfterHeader: true))
        {
            Assert.False(File.Exists(path + ".tmp"));
            Assert.True(new FileInfo(path).Length >= Llog.FileHeaderSize);
            using var r = SegmentReader.Open(path); // readable at once, before any record is written
            Assert.False(r.IsSealed);
        }

        // End to end: a writer rotating quickly never leaves staging files behind.
        var store = TestData.TempDir();
        using (var writer = StoreWriter.Open(store, new StoreWriterOptions { MinFreeDiskBytes = 0, SegmentMaxBytes = 1 })) // every batch rotates
            for (var b = 0; b < 20; b++)
                writer.WriteBatch(Enumerable.Range(0, 50).Select(i => new LogRecord { Severity = Severity.Info, EventName = "tick", Attributes = [new("i", b * 50 + i)] }).ToList());
        Assert.Empty(Directory.GetFiles(Path.Combine(store, StoreLayout.SegmentsDir), "*.tmp"));
        Assert.True(Directory.GetFiles(Path.Combine(store, StoreLayout.SegmentsDir), "*.llog").Length > 3);
    }

    [Fact]
    public void Queries_report_an_unreadable_segment_instead_of_failing()
    {
        // A segment shorter than its header (left by an older writer that crashed while creating it) must show up as a
        // coverage issue in every query path, never as an exception.
        var root = TestData.TempDir();
        using (var w = StoreWriter.Open(root, new StoreWriterOptions { MinFreeDiskBytes = 0 }))
            w.WriteBatch(Enumerable.Range(0, 100).Select(i => new LogRecord { Severity = i % 10 == 0 ? Severity.Error : Severity.Info, EventName = "tick", Attributes = [new("i", i)] }).ToList());
        File.WriteAllBytes(StoreLayout.SegmentPath(root, 999), []);

        var engine = QueryEngine.Open(root);
        var agg = engine.Aggregate(new QueryRequest { Where = "attr.i >= 0" }, "level");
        Assert.Equal(100, agg.Total);
        Assert.Contains(agg.Issues, i => i.File.EndsWith(Path.GetFileName(StoreLayout.SegmentPath(root, 999)), StringComparison.Ordinal));
        var found = engine.Execute(new QueryRequest { Where = "level >= ERROR", Limit = 50 });
        Assert.Equal(10, found.Hits.Count);
        var report = new ScanReport();
        Assert.NotNull(engine.GetBySequence(50, report));
        Assert.Null(engine.GetBySequence(5_000, report));
        Assert.Contains(report.Issues, i => i.Kind == IssueKind.UnsupportedFile);
    }
}
