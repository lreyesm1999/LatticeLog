using LatticeLog.Storage.Json;

namespace LatticeLog.Core;

public sealed class LatticeLoggerOptions
{
    /// <summary>Records below this severity are rejected before allocation by generated code (counted as FilteredByLevel when they reach Write).</summary>
    public Severity MinimumLevel { get; set; } = Severity.Info;

    public DurabilityMode Durability { get; set; } = DurabilityMode.Balanced;

    public SaturationPolicy Saturation { get; set; } = SaturationPolicy.Block;

    /// <summary>Maximum records waiting in memory. Bounds memory use (P05-T04).</summary>
    public int QueueCapacity { get; set; } = 65_536;

    /// <summary>Maximum time a producer blocks under <see cref="SaturationPolicy.Block"/> before the record is dropped and counted.</summary>
    public TimeSpan BlockTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public int BatchMaxRecords { get; set; } = 1024;

    /// <summary>Maximum time a record waits in memory before being written. Null = mode default (Fast 100 ms, Balanced 10 ms, Durable 1 ms).</summary>
    public TimeSpan? BatchMaxDelay { get; set; }

    /// <summary>Balanced mode: periodic fsync interval.</summary>
    public TimeSpan BalancedFsyncInterval { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Resource attributes (service.name, service.version, deployment.environment, host.name...).</summary>
    public List<LogAttribute> Resource { get; } = [];

    /// <summary>Copy trace/span ids from System.Diagnostics.Activity.Current when the record has none (P03-T12).</summary>
    public bool CaptureActivity { get; set; } = true;

    /// <summary>Record a producer-local monotonic timestamp for duration computations (spec §3.4).</summary>
    public bool CaptureMonotonicTime { get; set; } = true;

    /// <summary>
    /// Redaction applied on the writer thread before persistence (P03-T10, P18-T03): schema fields marked Secret and
    /// attribute keys matching sensitive patterns are replaced by "[REDACTED]" and the record is flagged.
    /// </summary>
    public RedactionPolicy PersistRedaction { get; set; } = RedactionPolicy.Default;

    /// <summary>
    /// Optional deterministic sampling: keep 1 of N records at or below <see cref="SamplingMaxSeverity"/>. 1 = no sampling.
    /// Sampled-out records are counted (P03-T08).
    /// </summary>
    public int SamplingKeepOneOf { get; set; } = 1;

    public Severity SamplingMaxSeverity { get; set; } = Severity.Debug;

    /// <summary>Flush with a bounded timeout on ProcessExit / unhandled exceptions (P03-T14).</summary>
    public bool FlushOnProcessExit { get; set; } = true;

    /// <summary>Internal diagnostics sink. Never routed back into the logger (no recursion, P14-T07).</summary>
    public Action<string>? InternalDiagnostics { get; set; }

    internal TimeSpan EffectiveBatchDelay => BatchMaxDelay ?? Durability switch
    {
        DurabilityMode.Fast => TimeSpan.FromMilliseconds(100),
        DurabilityMode.Balanced => TimeSpan.FromMilliseconds(10),
        _ => TimeSpan.FromMilliseconds(1),
    };

    /// <summary>Validates options with actionable messages (P14-T05).</summary>
    public void Validate()
    {
        if (QueueCapacity is < 16 or > 100_000_000) throw new ArgumentOutOfRangeException(nameof(QueueCapacity), QueueCapacity, "QueueCapacity must be between 16 and 100,000,000.");
        if (BatchMaxRecords is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(BatchMaxRecords), BatchMaxRecords, "BatchMaxRecords must be between 1 and 1,000,000.");
        if (BatchMaxRecords > QueueCapacity) throw new ArgumentException("BatchMaxRecords cannot exceed QueueCapacity.", nameof(BatchMaxRecords));
        if (BlockTimeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(BlockTimeout), "BlockTimeout cannot be negative.");
        if (EffectiveBatchDelay <= TimeSpan.Zero || EffectiveBatchDelay > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(BatchMaxDelay), "BatchMaxDelay must be in (0, 1 min].");
        if (SamplingKeepOneOf < 1) throw new ArgumentOutOfRangeException(nameof(SamplingKeepOneOf), "SamplingKeepOneOf must be >= 1.");
        if (ShutdownTimeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ShutdownTimeout));
        foreach (var a in Resource)
            if (string.IsNullOrWhiteSpace(a.Key)) throw new ArgumentException("Resource attribute keys cannot be empty.", nameof(Resource));
    }
}
