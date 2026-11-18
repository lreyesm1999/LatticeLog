// Producer hot-path micro-benchmarks (P03-T06, P03-T15, P14-T03).
// Run: dotnet run -c Release --project benchmarks/LatticeLog.Benchmarks -- --filter '*'
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using LatticeLog;
using LatticeLog.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// In-process toolchain: no separate build of a generated project (also avoids depending on the system `dotnet` on PATH).
var config = DefaultConfig.Instance.AddJob(Job.Default.WithToolchain(InProcessEmitToolchain.Instance).WithWarmupCount(5).WithIterationCount(15));
BenchmarkSwitcher.FromAssembly(typeof(HotPathBenchmarks).Assembly).Run(args, config);

/// <summary>Sink that discards batches: isolates the producer + queue + writer-thread cost from disk I/O.</summary>
public sealed class NullSink : IRecordSink
{
    public ulong LastSequence { get; private set; }
    public long WriteBatch(IReadOnlyList<LogRecord> batch) { LastSequence += (ulong)batch.Count; return 0; }
    public AckLevel Flush(AckLevel level) => level;
    public void Dispose() { }
}

public static partial class BenchEvents
{
    [LatticeEvent(Id = 1, Name = "bench.order.placed", Level = Severity.Info, Message = "Order {orderId} placed: {quantity} x {sku}")]
    public static partial void OrderPlaced(ILatticeLogger logger, long orderId, int quantity, string sku, double amount);

    [LatticeEvent(Id = 2, Name = "bench.debug.detail", Level = Severity.Debug)]
    public static partial void DebugDetail(ILatticeLogger logger, long orderId, int step);
}

[MemoryDiagnoser]
public class HotPathBenchmarks
{
    private LatticeLogger _logger = null!;
    private ILogger _melDisabled = null!;
    private static readonly Action<ILogger, long, int, Exception?> MelDebug = LoggerMessage.Define<long, int>(Microsoft.Extensions.Logging.LogLevel.Debug, new EventId(2), "Detail {OrderId} {Step}");
    private long _i;

    [GlobalSetup]
    public void Setup()
    {
        _logger = new LatticeLogger(new NullSink(), new LatticeLoggerOptions
        {
            MinimumLevel = Severity.Info,
            QueueCapacity = 4_000_000,
            BatchMaxRecords = 4096,
            Saturation = SaturationPolicy.DropNewest,
            FlushOnProcessExit = false,
        });
        _melDisabled = NullLoggerFactory.Instance.CreateLogger("bench");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        var s = _logger.GetStats();
        Console.WriteLine($"// stats: accepted={s.Accepted} written={s.Written} droppedSaturation={s.DroppedSaturation}");
        _logger.Dispose();
    }

    [Benchmark(Description = "typed event, level disabled")]
    public void TypedDisabled() => BenchEvents.DebugDetail(_logger, _i++, 3);

    [Benchmark(Description = "MEL LoggerMessage, disabled (reference)")]
    public void MelDisabled() => MelDebug(_melDisabled, _i++, 3, null);

    [Benchmark(Description = "typed event, enabled (4 fields)")]
    public void TypedEnabled() => BenchEvents.OrderPlaced(_logger, _i++, 2, "SKU-00042", 19.99);

    [Benchmark(Description = "free text + 2 attributes, enabled")]
    public void FreeTextEnabled() => _logger.Info("order placed", new LogAttribute("orderId", _i++), new LogAttribute("sku", "SKU-00042"));
}
