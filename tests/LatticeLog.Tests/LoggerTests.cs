using System.Diagnostics;
using LatticeLog.Core;
using LatticeLog.Storage;

namespace LatticeLog.Tests;

public class LoggerTests
{
    private sealed class MemorySink : IRecordSink
    {
        public readonly List<LogRecord> Records = [];
        public int Delay;
        public bool Fail;
        public ulong LastSequence { get; private set; }
        public long WriteBatch(IReadOnlyList<LogRecord> batch)
        {
            if (Delay > 0) Thread.Sleep(Delay);
            if (Fail) throw new IOException("disk full (simulated)");
            lock (Records)
            {
                foreach (var r in batch) { r.Sequence = ++LastSequence; Records.Add(r); }
            }
            return batch.Count * 10;
        }
        public AckLevel Flush(AckLevel level) => level;
        public void Dispose() { }
    }

    [Fact]
    public async Task Durable_flush_confirms_all_records_from_many_threads()
    {
        var root = TestData.TempDir();
        using (var logger = LatticeLogger.CreateLocal(root, o => { o.Durability = DurabilityMode.Durable; o.MinimumLevel = Severity.Trace; o.FlushOnProcessExit = false; }))
        {
            var threads = Enumerable.Range(0, 8).Select(t => Task.Run(() =>
            {
                for (var i = 0; i < 2000; i++) logger.Info("msg", new LogAttribute("thread", t), new LogAttribute("i", i));
            })).ToArray();
            await Task.WhenAll(threads);
            var res = await logger.FlushAsync(AckLevel.FsyncConfirmed);
            Assert.Equal(AckLevel.FsyncConfirmed, res.Level);
            Assert.Null(res.Error);
            var stats = logger.GetStats();
            Assert.Equal(16000, stats.Accepted);
            Assert.Equal(16000, stats.Written);
            Assert.Equal(16000UL, stats.LastSequence);
        }
        var rep = new ScanReport();
        var all = StoreReader.Open(root).ReadAll(rep).ToList();
        Assert.True(rep.IsClean);
        Assert.Equal(16000, all.Count);
        // Per-producer order is preserved.
        foreach (var g in all.GroupBy(r => r.Attributes[0].Value.AsInt64()))
        {
            var idx = g.Select(r => r.Attributes[1].Value.AsInt64()).ToList();
            Assert.Equal(idx.OrderBy(x => x), idx);
        }
    }

    [Fact]
    public void Saturation_drop_policy_counts_every_drop()
    {
        var sink = new MemorySink { Delay = 50 };
        var logger = new LatticeLogger(sink, new LatticeLoggerOptions
        {
            QueueCapacity = 16,
            BatchMaxRecords = 4,
            Saturation = SaturationPolicy.DropNewest,
            FlushOnProcessExit = false,
        });
        var accepted = 0;
        for (var i = 0; i < 500; i++) if (logger.Info("x")) accepted++;
        logger.Dispose();
        var s = logger.GetStats();
        Assert.Equal(500, s.Accepted + s.DroppedSaturation);
        Assert.Equal(accepted, s.Accepted);
        Assert.True(s.DroppedSaturation > 0);
        Assert.Equal(s.Accepted, sink.Records.Count);
    }

    [Fact]
    public void Saturation_throw_policy_throws()
    {
        var sink = new MemorySink { Delay = 200 };
        using var logger = new LatticeLogger(sink, new LatticeLoggerOptions { QueueCapacity = 16, BatchMaxRecords = 4, Saturation = SaturationPolicy.Throw, FlushOnProcessExit = false });
        Assert.Throws<LoggerSaturatedException>(() => { for (var i = 0; i < 1000; i++) logger.Info("x"); });
    }

    [Fact]
    public void Block_policy_eventually_accepts_everything_when_writer_keeps_up()
    {
        var sink = new MemorySink { Delay = 1 };
        var logger = new LatticeLogger(sink, new LatticeLoggerOptions { QueueCapacity = 32, BatchMaxRecords = 8, Saturation = SaturationPolicy.Block, FlushOnProcessExit = false });
        for (var i = 0; i < 2000; i++) Assert.True(logger.Info("x"));
        logger.Dispose();
        Assert.Equal(2000, sink.Records.Count);
        Assert.Equal(0, logger.GetStats().DroppedSaturation);
    }

    [Fact]
    public async Task Ring_with_many_producers_delivers_each_record_once_in_producer_order()
    {
        // 8 producers, a 16-slot ring and 2-record batches: the ring wraps ~10,000 times and producers constantly block.
        var sink = new MemorySink();
        var logger = new LatticeLogger(sink, new LatticeLoggerOptions
        {
            QueueCapacity = 16,
            BatchMaxRecords = 2,
            Saturation = SaturationPolicy.Block,
            Durability = DurabilityMode.Fast,
            BatchMaxDelay = TimeSpan.FromMilliseconds(1),
            FlushOnProcessExit = false,
        });
        const int producers = 8, perProducer = 20_000;
        var tasks = Enumerable.Range(0, producers).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < perProducer; i++)
                Assert.True(logger.Info("r", new LogAttribute("t", t), new LogAttribute("i", i)));
        })).ToArray();
        await Task.WhenAll(tasks);
        var flushed = await logger.FlushAsync(AckLevel.WrittenToOs);
        Assert.Equal(AckLevel.WrittenToOs, flushed.Level);
        Assert.Equal((ulong)(producers * perProducer), flushed.LastSequence);
        logger.Dispose();

        Assert.Equal(producers * perProducer, sink.Records.Count);
        var next = new int[producers];
        foreach (var r in sink.Records)
        {
            Assert.True(r.TryGetAttribute("t", out var t));
            Assert.True(r.TryGetAttribute("i", out var i));
            Assert.Equal(next[t.AsInt64()]++, i.AsInt64()); // per-producer order kept, nothing duplicated or skipped
        }
        var s = logger.GetStats();
        Assert.Equal(producers * perProducer, s.Accepted);
        Assert.Equal(0, s.DroppedSaturation);
        Assert.Equal(0, s.QueueDepth);
    }

    [Fact]
    public async Task Pooled_records_are_recycled_without_corrupting_persisted_data()
    {
        // StoreWriter does not retain records, so the logger recycles pooled ones while producers keep renting.
        var root = TestData.TempDir();
        var shared = new[] { new LogAttribute("tenant", "acme"), new LogAttribute("region", "eu") };
        var descriptor = new EventDescriptor(77, "pool.check", 1, Severity.Info, null,
            [new EventField("t", ValueKind.Int64), new EventField("i", ValueKind.Int64), new EventField("password", ValueKind.String, Sensitivity.Secret)]);
        const int producers = 6, perProducer = 3000;
        using (var logger = LatticeLogger.CreateLocal(root, o => { o.Durability = DurabilityMode.Fast; o.FlushOnProcessExit = false; o.QueueCapacity = 64; o.BatchMaxRecords = 16; }))
        {
            var tasks = Enumerable.Range(0, producers).Select(t => Task.Run(() =>
            {
                for (var i = 0; i < perProducer; i++)
                {
                    logger.WriteEvent(descriptor, null, Value.From(t), Value.From(i), Value.From($"pw-{t}-{i}"));
                    logger.Info("free", shared);
                }
            })).ToArray();
            await Task.WhenAll(tasks);
            Assert.Equal(AckLevel.FsyncConfirmed, (await logger.FlushAsync(AckLevel.FsyncConfirmed)).Level);
        }
        // The caller's array was referenced, never cleared or reused by the pool.
        Assert.Equal(new LogAttribute("tenant", "acme"), shared[0]);
        Assert.Equal(new LogAttribute("region", "eu"), shared[1]);

        var rep = new ScanReport();
        var all = StoreReader.Open(root).ReadAll(rep).ToList();
        Assert.True(rep.IsClean);
        Assert.Equal(2 * producers * perProducer, all.Count);
        var next = new int[producers];
        foreach (var r in all)
        {
            if (r.EffectiveEventName == "pool.check")
            {
                Assert.Equal(3, r.Attributes.Length);
                var t = (int)r.Attributes[0].Value.AsInt64();
                Assert.Equal(next[t]++, r.Attributes[1].Value.AsInt64());
                Assert.Equal(LatticeLog.Storage.Json.RedactionPolicy.Mask, r.Attributes[2].Value.AsString()); // secret redacted, never leaked across reuse
                Assert.True(r.Flags.HasFlag(RecordFlags.Redacted));
            }
            else
            {
                Assert.Equal("free", r.Body.AsString());
                Assert.Equal(shared, r.Attributes);
                Assert.Null(r.EffectiveEventName);
                Assert.False(r.Flags.HasFlag(RecordFlags.Redacted));
            }
        }
        Assert.All(next, n => Assert.Equal(perProducer, n));
    }

    [Fact]
    public async Task Write_errors_are_reported_in_flush_and_stats()
    {
        var sink = new MemorySink { Fail = true };
        await using var logger = new LatticeLogger(sink, new LatticeLoggerOptions { FlushOnProcessExit = false });
        logger.Info("a");
        logger.Info("b");
        var r = await logger.FlushAsync(AckLevel.WrittenToOs);
        Assert.NotNull(r.Error);
        Assert.Equal(2, logger.GetStats().DroppedWriteError);
    }

    [Fact]
    public void Level_filter_and_sampling_are_counted()
    {
        var sink = new MemorySink();
        var logger = new LatticeLogger(sink, new LatticeLoggerOptions { MinimumLevel = Severity.Debug, SamplingKeepOneOf = 4, SamplingMaxSeverity = Severity.Debug, FlushOnProcessExit = false });
        for (var i = 0; i < 100; i++) logger.Trace("t");
        for (var i = 0; i < 100; i++) logger.Debug("d");
        for (var i = 0; i < 10; i++) logger.Error("e");
        logger.Dispose();
        var s = logger.GetStats();
        Assert.False(logger.IsEnabled(Severity.Trace));
        Assert.Equal(75, s.SampledOut);
        Assert.Equal(35, sink.Records.Count);
    }

    [Fact]
    public void Scopes_and_operations_flow_through_async_code()
    {
        var sink = new MemorySink();
        var options = new LatticeLoggerOptions { FlushOnProcessExit = false };
        options.Resource.Add(new LogAttribute("service.name", "inventory"));
        var logger = new LatticeLogger(sink, options);
        using (logger.BeginOperation("ReserveStock", "op-1", new LogAttribute("product", 42L)))
        {
            Task.Run(async () =>
            {
                await Task.Yield();
                using (logger.BeginScope(ContextKind.Request, "inner", new LogAttribute("attempt", 2L)))
                using (logger.BeginOperation("CallWarehouse", "op-2"))
                    logger.Info("inside");
            }).Wait();
            logger.Info("outer");
        }
        logger.Info("after");
        logger.Dispose();
        var inside = sink.Records.Single(r => r.Body.AsString() == "inside");
        Assert.Equal("op-2", inside.OperationId);
        Assert.Equal("op-1", inside.ParentOperationId);
        Assert.Equal("inventory", inside.ServiceName);
        Assert.True(inside.Context!.TryGet("attempt", out var a) && a.AsInt64() == 2);
        Assert.True(inside.Context!.TryGet("product", out var p) && p.AsInt64() == 42);
        var outer = sink.Records.Single(r => r.Body.AsString() == "outer");
        Assert.Equal("op-1", outer.OperationId);
        Assert.False(outer.Context!.TryGet("attempt", out _));
        var after = sink.Records.Single(r => r.Body.AsString() == "after");
        Assert.Null(after.OperationId);
        Assert.Same(logger.RootContext, after.Context);
    }

    [Fact]
    public void Activity_trace_context_is_captured()
    {
        var sink = new MemorySink();
        var logger = new LatticeLogger(sink, new LatticeLoggerOptions { FlushOnProcessExit = false });
        using var source = new ActivitySource("test");
        using var listener = new ActivityListener { ShouldListenTo = _ => true, Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded };
        ActivitySource.AddActivityListener(listener);
        string traceHex, spanHex;
        using (var act = source.StartActivity("op")!)
        {
            traceHex = act.TraceId.ToHexString();
            spanHex = act.SpanId.ToHexString();
            logger.Info("with trace");
        }
        logger.Dispose();
        var r = Assert.Single(sink.Records);
        Assert.Equal(traceHex, r.TraceId.ToString());
        Assert.Equal(spanHex, r.SpanId.ToString());
    }

    [Fact]
    public void Secrets_are_redacted_before_persistence()
    {
        var sink = new MemorySink();
        var logger = new LatticeLogger(sink, new LatticeLoggerOptions { FlushOnProcessExit = false });
        var d = new EventDescriptor(5, "user.login", 1, Severity.Info, null, [new("user", ValueKind.String), new("pin", ValueKind.String, Sensitivity.Secret)]);
        logger.WriteEvent(d, null, "alice", "1234");
        logger.Info("token issued", new LogAttribute("access_token", "abc.def"), new LogAttribute("user", "bob"));
        logger.Dispose();
        Assert.Equal("[REDACTED]", sink.Records[0].Attributes[1].Value.AsString());
        Assert.Equal("alice", sink.Records[0].Attributes[0].Value.AsString());
        Assert.True(sink.Records[0].Flags.HasFlag(RecordFlags.Redacted));
        Assert.Equal("[REDACTED]", sink.Records[1].Attributes[0].Value.AsString());
        Assert.Equal("bob", sink.Records[1].Attributes[1].Value.AsString());
    }

    [Fact]
    public void Records_after_dispose_are_counted_not_lost_silently()
    {
        var sink = new MemorySink();
        var logger = new LatticeLogger(sink, new LatticeLoggerOptions { FlushOnProcessExit = false });
        logger.Dispose();
        Assert.False(logger.Info("late"));
        Assert.Equal(1, logger.GetStats().DroppedAfterDispose);
    }

    [Fact]
    public void Options_validation_gives_actionable_errors()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new LatticeLoggerOptions { QueueCapacity = 1 }.Validate());
        Assert.Contains("QueueCapacity", ex.Message);
        Assert.Throws<ArgumentException>(() => new LatticeLoggerOptions { QueueCapacity = 100, BatchMaxRecords = 200 }.Validate());
    }
}
