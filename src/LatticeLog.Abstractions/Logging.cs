namespace LatticeLog;

/// <summary>
/// Persistence level actually reached for a set of records (specs/durability.md).
/// Never report a higher level than the one physically achieved.
/// </summary>
public enum AckLevel : byte
{
    /// <summary>Nothing confirmed.</summary>
    None = 0,
    /// <summary>Record accepted in an in-memory buffer. Lost if the process dies.</summary>
    AcceptedInMemory = 1,
    /// <summary>Bytes handed to the operating system (write + flush of user-space buffers). Lost on power failure/OS crash.</summary>
    WrittenToOs = 2,
    /// <summary>fsync / FlushFileBuffers returned successfully for the file holding the records.</summary>
    FsyncConfirmed = 3,
}

/// <summary>Operational durability mode (P00-T07). See docs/product/prd.md §Durability.</summary>
public enum DurabilityMode : byte
{
    /// <summary>Batches are written opportunistically; ack = AcceptedInMemory.</summary>
    Fast = 0,
    /// <summary>Every batch is flushed to the OS; FlushAsync acks WrittenToOs.</summary>
    Balanced = 1,
    /// <summary>Every batch is fsync'ed before its records are acknowledged; FlushAsync acks FsyncConfirmed.</summary>
    Durable = 2,
}

/// <summary>What to do when the in-memory queue is full (P05-T06).</summary>
public enum SaturationPolicy : byte
{
    /// <summary>The producer blocks until space is available (bounded by <c>BlockTimeout</c>, then the record is dropped and counted).</summary>
    Block = 0,
    /// <summary>The new record is dropped and counted in <see cref="LoggerStats.DroppedSaturation"/>.</summary>
    DropNewest = 1,
    /// <summary>The producer gets a <see cref="LoggerSaturatedException"/>.</summary>
    Throw = 2,
}

public sealed class LoggerSaturatedException(string message) : Exception(message);

/// <summary>Snapshot of logger counters. Every intentional or accidental loss is counted (spec §3.1).</summary>
public readonly record struct LoggerStats(
    long Accepted,
    long FilteredByLevel,
    long SampledOut,
    long DroppedSaturation,
    long DroppedAfterDispose,
    long DroppedWriteError,
    long Written,
    long WrittenToOs,
    long FsyncConfirmed,
    long QueueDepth,
    long BytesWritten,
    long WriteErrors,
    ulong LastSequence);

public readonly record struct FlushResult(AckLevel Level, ulong LastSequence, bool TimedOut, string? Error);

/// <summary>Primary logging interface. Thread-safe.</summary>
public interface ILatticeLogger
{
    /// <summary>Cheap check executed before allocating a record. Generated code calls this first.</summary>
    bool IsEnabled(Severity severity);

    /// <summary>
    /// Enqueue a record. Fills timestamp (if 0), ambient context, trace context and sequence.
    /// Returns false if the record was not accepted (filtered, sampled out, dropped). Never blocks
    /// longer than the configured saturation policy allows.
    /// </summary>
    bool Write(LogRecord record);

    /// <summary>Wait until all records accepted before the call reach <paramref name="level"/> (or the configured maximum).</summary>
    ValueTask<FlushResult> FlushAsync(AckLevel level = AckLevel.FsyncConfirmed, CancellationToken cancellationToken = default);

    /// <summary>Begin an immutable context scope. Disposing restores the previous context.</summary>
    IDisposable BeginScope(ContextKind kind, string? name, params LogAttribute[] attributes);

    /// <summary>Begin a logical operation: sets OperationId (and ParentOperationId) for records written inside.</summary>
    IDisposable BeginOperation(string name, string? operationId = null, params LogAttribute[] attributes);

    LoggerStats GetStats();
}

/// <summary>Destination of encoded batches. Implemented by the local store writer and remote exporters.</summary>
public interface IRecordSink : IDisposable
{
    /// <summary>Persist a batch in order. Sequence numbers are assigned by the sink. Returns bytes written.</summary>
    long WriteBatch(IReadOnlyList<LogRecord> batch);

    /// <summary>Make previously written batches reach <paramref name="level"/>. Returns the level achieved.</summary>
    AckLevel Flush(AckLevel level);

    ulong LastSequence { get; }
}

/// <summary>
/// Marks a <c>static partial void</c> method as a typed event. The LatticeLog source generator
/// produces an allocation-light implementation and compile-time validation (P03-T03/T04).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class LatticeEventAttribute : Attribute
{
    public uint Id { get; set; }
    public string Name { get; set; } = "";
    public Severity Level { get; set; } = Severity.Info;
    public ushort Version { get; set; } = 1;
    /// <summary>Optional message template with {placeholders} matching parameter names.</summary>
    public string? Message { get; set; }
    public string? Description { get; set; }
}

/// <summary>Marks a parameter of a typed event as sensitive.</summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class SensitiveAttribute(Sensitivity sensitivity = Sensitivity.Secret) : Attribute
{
    public Sensitivity Sensitivity { get; } = sensitivity;
}
