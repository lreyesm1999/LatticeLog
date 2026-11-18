using LatticeLog.Query;
using LatticeLog.Storage;

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
}
