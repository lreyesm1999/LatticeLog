using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using LatticeLog.Storage;
using LatticeLog.Storage.Json;

namespace LatticeLog.Core;

/// <summary>
/// Default <see cref="ILatticeLogger"/>: producers enqueue into a bounded MPSC queue; one dedicated writer
/// thread batches, redacts, persists and acknowledges. Acks never claim more than was physically reached (spec §3.1).
/// </summary>
public sealed class LatticeLogger : ILatticeLogger, IDisposable, IAsyncDisposable
{
    private sealed class FlushRequest(long ticket, AckLevel level, long errorsAtRequest)
    {
        public readonly long Ticket = ticket;
        public readonly AckLevel Level = level;
        public readonly long ErrorsAtRequest = errorsAtRequest;
        public readonly TaskCompletionSource<FlushResult> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly LatticeLoggerOptions _options;
    private readonly IRecordSink _sink;
    private readonly bool _recycle; // the sink does not retain records: pooled ones go back to LogRecordPool
    // Records waiting for the writer. Ring position + 1 is the record's acknowledgement ticket: tickets are dense, so
    // "every ticket <= T is resolved" is simply "the writer consumed and persisted T positions".
    private readonly BoundedRing<LogRecord> _ring;
    private readonly ConcurrentQueue<FlushRequest> _flushRequests = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly ManualResetEventSlim _space = new(true);
    private readonly Thread _writer;
    private readonly ContextSnapshot _root;
    private readonly AsyncLocal<ContextSnapshot?> _ambient = new();
    private readonly AsyncLocal<OperationFrame?> _operation = new();
    private readonly double _monotonicScale = 1_000_000_000.0 / Stopwatch.Frequency;
    private readonly int _minLevel;
    private readonly AckLevel _modeAck;

    private volatile int _writerSleeping;
    private volatile bool _closing;
    private volatile bool _stopped;
    private long _sampleCounter;

    // Counters (accepted = ring tail, no per-record interlocked increment)
    private long _filtered, _sampled, _droppedSat, _droppedDisposed, _droppedWrite, _written, _writtenOs, _fsynced, _bytes, _writeErrors;
    private long _ackedOs, _ackedFsync; // tickets watermark per level (writer thread only, read with Volatile)
    private long _lastFsyncTimestamp;
    private long _bytesAtLastFsync; // writer thread only
    private bool _backgroundFsync;  // writer thread only: the sink accepted background fsync requests

    public LatticeLogger(IRecordSink sink, LatticeLoggerOptions? options = null)
    {
        _options = options ?? new LatticeLoggerOptions();
        _options.Validate();
        _sink = sink;
        _recycle = !sink.RetainsRecords;
        _minLevel = (int)_options.MinimumLevel;
        _modeAck = _options.Durability switch
        {
            DurabilityMode.Durable => AckLevel.FsyncConfirmed,
            DurabilityMode.Balanced => AckLevel.WrittenToOs,
            _ => AckLevel.WrittenToOs,
        };
        _ring = new BoundedRing<LogRecord>(_options.EffectiveQueueCapacity);
        _root = new ContextSnapshot(ContextKind.Resource, null, _options.Resource.ToArray(), null);
        _lastFsyncTimestamp = Stopwatch.GetTimestamp();
        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "LatticeLog.Writer" };
        _writer.Start();
        if (_options.FlushOnProcessExit)
        {
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        }
    }

    /// <summary>Creates a logger writing to a local store directory (created if missing).</summary>
    public static LatticeLogger CreateLocal(string directory, Action<LatticeLoggerOptions>? configure = null, StoreWriterOptions? storeOptions = null)
    {
        var options = new LatticeLoggerOptions();
        configure?.Invoke(options);
        options.Validate();
        var store = StoreWriter.Open(directory, storeOptions);
        foreach (var r in store.Recovery)
            options.InternalDiagnostics?.Invoke($"recovered segment {r.SegmentId}: truncated {r.TruncatedBytes} bytes, {r.Issues.Count} issues");
        return new LatticeLogger(store, options);
    }

    public ContextSnapshot RootContext => _root;
    public ContextSnapshot CurrentContext => _ambient.Value ?? _root;

    public bool IsEnabled(Severity severity) => (int)severity >= _minLevel;

    public bool Write(LogRecord record)
    {
        if ((int)record.Severity < _minLevel)
        {
            Interlocked.Increment(ref _filtered);
            LogRecordPool.Return(record); // never enqueued: nobody else references it (no-op unless pooled)
            return false;
        }
        if (_closing)
        {
            Interlocked.Increment(ref _droppedDisposed);
            LogRecordPool.Return(record);
            return false;
        }
        if (_options.SamplingKeepOneOf > 1 && record.Severity <= _options.SamplingMaxSeverity
            && Interlocked.Increment(ref _sampleCounter) % _options.SamplingKeepOneOf != 0)
        {
            Interlocked.Increment(ref _sampled);
            LogRecordPool.Return(record);
            return false;
        }
        Enrich(record);
        if (!_ring.TryEnqueue(record) && !EnqueueSaturated(record)) return false;
        if (_writerSleeping == 1 && (_options.Durability == DurabilityMode.Durable || _ring.Count >= _options.BatchMaxRecords)) WakeWriter();
        return true;
    }

    /// <summary>Slow path when the ring is full: apply the saturation policy; every drop is counted.</summary>
    private bool EnqueueSaturated(LogRecord record)
    {
        if (_options.Saturation == SaturationPolicy.Block)
        {
            var deadline = Stopwatch.GetTimestamp() + (long)(_options.BlockTimeout.TotalSeconds * Stopwatch.Frequency);
            WakeWriter();
            while (true)
            {
                if (_ring.TryEnqueue(record)) return true;
                var remaining = deadline - Stopwatch.GetTimestamp();
                if (remaining <= 0 || _closing) break;
                _space.Reset();
                if (_ring.TryEnqueue(record)) return true; // space freed between the failed attempt and the reset
                WakeWriter();
                _space.Wait(TimeSpan.FromMilliseconds(Math.Min(50, remaining * 1000.0 / Stopwatch.Frequency)));
            }
        }
        Interlocked.Increment(ref _droppedSat);
        LogRecordPool.Return(record);
        WakeWriter();
        if (_options.Saturation == SaturationPolicy.Throw) throw new LoggerSaturatedException($"LatticeLog queue full ({_ring.Capacity} records).");
        return false;
    }

    private void Enrich(LogRecord r)
    {
        if (r.TimestampUnixNanos == 0) r.TimestampUnixNanos = UnixTime.NowUnixNanos();
        if (_options.CaptureMonotonicTime && r.MonotonicNanos == 0) r.MonotonicNanos = (long)(Stopwatch.GetTimestamp() * _monotonicScale);
        r.Context ??= _ambient.Value ?? _root;
        if (r.OperationId is null && _operation.Value is { } op)
        {
            r.OperationId = op.OperationId;
            r.ParentOperationId ??= op.Parent?.OperationId;
        }
        if (_options.CaptureActivity && r.TraceId.IsEmpty && Activity.Current is { } act && act.IdFormat == ActivityIdFormat.W3C)
        {
            Span<byte> b = stackalloc byte[16];
            act.TraceId.CopyTo(b);
            r.TraceId = TraceId.FromBytes(b);
            act.SpanId.CopyTo(b[..8]);
            r.SpanId = new SpanId(BinaryPrimitives.ReadUInt64BigEndian(b));
            r.TraceFlags = (byte)act.ActivityTraceFlags;
        }
    }

    private void WakeWriter() => _wake.Set();

    public ValueTask<FlushResult> FlushAsync(AckLevel level = AckLevel.FsyncConfirmed, CancellationToken cancellationToken = default)
    {
        if (_stopped)
        {
            var last = _sink.LastSequence;
            return ValueTask.FromResult(new FlushResult(AckLevel.FsyncConfirmed, last, false, null));
        }
        var req = new FlushRequest(_ring.Tail, level, Volatile.Read(ref _writeErrors));
        _flushRequests.Enqueue(req);
        WakeWriter();
        if (!cancellationToken.CanBeCanceled) return new ValueTask<FlushResult>(req.Tcs.Task);
        return new ValueTask<FlushResult>(req.Tcs.Task.WaitAsync(cancellationToken));
    }

    /// <summary>Synchronous flush with timeout (used by shutdown hooks).</summary>
    public FlushResult Flush(AckLevel level, TimeSpan timeout)
    {
        var task = FlushAsync(level).AsTask();
        return task.Wait(timeout) ? task.Result : new FlushResult(AckLevel.None, _sink.LastSequence, true, "flush timed out");
    }

    public IDisposable BeginScope(ContextKind kind, string? name, params LogAttribute[] attributes)
    {
        var previous = _ambient.Value;
        _ambient.Value = new ContextSnapshot(kind, name, attributes, previous ?? _root);
        return new ScopeHandle(this, previous, restoreOperation: false, null);
    }

    public IDisposable BeginOperation(string name, string? operationId = null, params LogAttribute[] attributes)
    {
        var previous = _ambient.Value;
        var prevOp = _operation.Value;
        var id = operationId ?? Guid.NewGuid().ToString("N")[..16];
        var attrs = new LogAttribute[attributes.Length + 1];
        attrs[0] = new LogAttribute("operation.name", name);
        attributes.CopyTo(attrs, 1);
        _ambient.Value = new ContextSnapshot(ContextKind.Operation, name, attrs, previous ?? _root);
        _operation.Value = new OperationFrame(id, prevOp);
        return new ScopeHandle(this, previous, restoreOperation: true, prevOp);
    }

    private sealed record OperationFrame(string OperationId, OperationFrame? Parent);

    private sealed class ScopeHandle(LatticeLogger owner, ContextSnapshot? previous, bool restoreOperation, OperationFrame? previousOp) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            owner._ambient.Value = previous;
            if (restoreOperation) owner._operation.Value = previousOp;
        }
    }

    public LoggerStats GetStats() => new(
        _ring.Tail, Interlocked.Read(ref _filtered), Interlocked.Read(ref _sampled),
        Interlocked.Read(ref _droppedSat), Interlocked.Read(ref _droppedDisposed), Interlocked.Read(ref _droppedWrite),
        Interlocked.Read(ref _written), Interlocked.Read(ref _writtenOs), Interlocked.Read(ref _fsynced),
        _ring.Count, Interlocked.Read(ref _bytes), Interlocked.Read(ref _writeErrors), _sink.LastSequence);

    // ---------------------------------------------------------------------------------- writer thread

    private void WriterLoop()
    {
        var batch = new List<LogRecord>(_options.BatchMaxRecords);
        var delay = _options.EffectiveBatchDelay;
        var shortWait = TimeSpan.FromMilliseconds(1);
        var stalledSpins = 0;
        while (true)
        {
            batch.Clear();
            while (batch.Count < _options.BatchMaxRecords && _ring.TryDequeue(out var record)) batch.Add(record);
            // A producer is between its claim and its write (nanoseconds, unless it was preempted): retry soon instead
            // of sleeping a whole batch delay, but never spin for long.
            var stalled = batch.Count < _options.BatchMaxRecords && _ring.NextIsClaimedButUnpublished();
            stalledSpins = stalled ? stalledSpins + 1 : 0;
            if (stalledSpins > 64) stalled = false;
            if (batch.Count > 0)
            {
                _space.Set();
                PersistBatch(batch);
                if (_recycle)
                    for (var i = 0; i < batch.Count; i++) LogRecordPool.Return(batch[i]);
            }
            // Every position below the head was consumed and its batch persisted (or counted as a write error).
            var watermark = _ring.Head;
            var achieved = AckAfterBatch(batch.Count > 0, watermark);
            CompleteFlushRequests(watermark, achieved);

            if (_closing && _ring.Count == 0)
            {
                CompleteFlushRequests(watermark, achieved, final: true);
                return;
            }
            var pending = _ring.Count;
            if (pending >= _options.BatchMaxRecords) continue;
            if (batch.Count > 0 && _options.Durability == DurabilityMode.Durable && pending > 0 && !stalled) continue;
            _writerSleeping = 1;
            if (stalled) Thread.Yield();
            else if (stalledSpins > 0 || (pending > 0 && _options.Durability == DurabilityMode.Durable)) _wake.Wait(shortWait);
            else _wake.Wait(delay);
            _writerSleeping = 0;
            _wake.Reset();
        }
    }

    private void PersistBatch(List<LogRecord> batch)
    {
        var redaction = _options.PersistRedaction;
        if (redaction.Enabled)
        {
            foreach (var r in batch) RedactForPersistence(r, redaction);
        }
        var t0 = Stopwatch.GetTimestamp();
        try
        {
            var bytes = _sink.WriteBatch(batch);
            Interlocked.Add(ref _bytes, bytes);
            Interlocked.Add(ref _written, batch.Count);
            SlowOperation($"write batch of {batch.Count} records", t0);
        }
        catch (Exception ex)
        {
            Interlocked.Add(ref _droppedWrite, batch.Count);
            Interlocked.Increment(ref _writeErrors);
            Diagnose($"write failed, {batch.Count} records not persisted: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void RedactForPersistence(LogRecord r, RedactionPolicy policy)
    {
        var attrs = r.Attributes;
        LogAttribute[]? copy = null;
        for (var i = 0; i < attrs.Length; i++)
        {
            var nv = policy.Apply(r, attrs[i].Key, attrs[i].Value);
            if (!nv.Equals(attrs[i].Value))
            {
                copy ??= (LogAttribute[])attrs.Clone();
                copy[i] = attrs[i] with { Value = nv };
            }
        }
        if (copy is not null)
        {
            r.Attributes = copy;
            r.Flags |= RecordFlags.Redacted;
        }
    }

    private AckLevel AckAfterBatch(bool wroteSomething, long watermark)
    {
        // Synchronous fsync: Durable mode (its contract) and explicit FlushAsync(FsyncConfirmed) requests.
        var needFsync = _options.Durability == DurabilityMode.Durable && wroteSomething;
        foreach (var req in _flushRequests)
        {
            if (req.Level >= AckLevel.FsyncConfirmed && req.Ticket <= watermark && Volatile.Read(ref _ackedFsync) < req.Ticket) needFsync = true;
        }
        // Balanced periodic fsync: handed to the sink's durability thread when it has one, so a slow disk does not stall
        // the writer thread (and, once the queue fills, the producers). Measured in reports/latency-v1.md.
        if (!needFsync && _options.Durability == DurabilityMode.Balanced && wroteSomething
            && (Stopwatch.GetElapsedTime(_lastFsyncTimestamp) >= _options.BalancedFsyncInterval
                || (_options.BalancedFsyncBytes > 0 && Interlocked.Read(ref _bytes) - _bytesAtLastFsync >= _options.BalancedFsyncBytes)))
        {
            if (_sink.RequestBackgroundFsync())
            {
                _backgroundFsync = true;
                _lastFsyncTimestamp = Stopwatch.GetTimestamp();
                _bytesAtLastFsync = Interlocked.Read(ref _bytes);
            }
            else needFsync = true;
        }
        if (_backgroundFsync) UpdateFsyncedFromSink();
        var achieved = AckLevel.WrittenToOs; // the store sink writes unbuffered: a returned WriteBatch means the OS has the bytes
        if (needFsync)
        {
            try
            {
                var tf = Stopwatch.GetTimestamp();
                achieved = _sink.Flush(AckLevel.FsyncConfirmed);
                SlowOperation("fsync", tf);
                if (achieved >= AckLevel.FsyncConfirmed)
                {
                    Volatile.Write(ref _ackedFsync, watermark);
                    Interlocked.Exchange(ref _fsynced, Interlocked.Read(ref _written));
                }
                _lastFsyncTimestamp = Stopwatch.GetTimestamp();
                _bytesAtLastFsync = Interlocked.Read(ref _bytes);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _writeErrors);
                achieved = AckLevel.None;
                Diagnose($"fsync failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        if (achieved >= AckLevel.WrittenToOs)
        {
            Volatile.Write(ref _ackedOs, watermark);
            Interlocked.Exchange(ref _writtenOs, Interlocked.Read(ref _written));
        }
        return achieved;
    }

    /// <summary>Stats only: records of this logger known durable = written minus those above the sink's durable sequence.</summary>
    private void UpdateFsyncedFromSink()
    {
        var last = _sink.LastSequence;
        var durable = Math.Min(_sink.DurableSequence, last);
        var fsynced = Math.Max(0, Interlocked.Read(ref _written) - (long)(last - durable));
        if (fsynced > Interlocked.Read(ref _fsynced)) Interlocked.Exchange(ref _fsynced, fsynced);
    }

    private void CompleteFlushRequests(long watermark, AckLevel achieved, bool final = false)
    {
        var count = _flushRequests.Count;
        for (var i = 0; i < count; i++)
        {
            if (!_flushRequests.TryDequeue(out var req)) break;
            var reached = req.Level >= AckLevel.FsyncConfirmed ? Volatile.Read(ref _ackedFsync) >= req.Ticket : Volatile.Read(ref _ackedOs) >= req.Ticket;
            if (req.Ticket <= watermark && (reached || final))
            {
                var errors = Interlocked.Read(ref _writeErrors) - req.ErrorsAtRequest;
                var level = reached ? req.Level : (achieved < req.Level ? achieved : req.Level);
                req.Tcs.TrySetResult(new FlushResult(level, _sink.LastSequence, false,
                    errors > 0 ? $"{errors} write/fsync error(s) since flush was requested; see stats.DroppedWriteError" : null));
            }
            else _flushRequests.Enqueue(req);
        }
    }

    /// <summary>Writer-thread operations slower than the threshold are reported: they are what stalls producers once the queue fills.</summary>
    private void SlowOperation(string what, long startTimestamp)
    {
        if (_options.InternalDiagnostics is null) return;
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
        if (elapsed >= _options.SlowWriterOperationThreshold) Diagnose($"writer: {what} took {elapsed.TotalMilliseconds:F1} ms (queue {_ring.Count}/{_ring.Capacity})");
    }

    private void Diagnose(string message)
    {
        try { _options.InternalDiagnostics?.Invoke("[LatticeLog] " + message); }
        catch { /* diagnostics must never break the writer */ }
    }

    // ---------------------------------------------------------------------------------- shutdown

    private void OnProcessExit(object? sender, EventArgs e) => Shutdown(_options.ShutdownTimeout);

    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        // Best effort: persist what is queued; the process is going down.
        Flush(AckLevel.FsyncConfirmed, _options.ShutdownTimeout);
    }

    private int _shutdown;

    /// <summary>Stops accepting, drains the queue, fsyncs and seals the active segment within <paramref name="timeout"/>.</summary>
    public bool Shutdown(TimeSpan timeout)
    {
        if (Interlocked.Exchange(ref _shutdown, 1) != 0) return true;
        _closing = true;
        WakeWriter();
        var finished = _writer.Join(timeout);
        if (finished)
        {
            // Producers that raced with shutdown: their records were accepted after the writer stopped. Count them, never hide them.
            var late = 0L;
            while (_ring.TryDequeue(out _)) late++;
            late += _ring.Count; // claimed by a producer but never published: will not be persisted either
            if (late > 0)
            {
                Interlocked.Add(ref _droppedDisposed, late);
                Diagnose($"{late} records arrived during shutdown and were not persisted");
            }
        }
        if (!finished)
        {
            Diagnose($"shutdown timed out after {timeout}; {_ring.Count} records not persisted");
        }
        else
        {
            try { _sink.Flush(AckLevel.FsyncConfirmed); } catch (Exception ex) { Diagnose("final fsync failed: " + ex.Message); }
        }
        _stopped = true;
        if (_options.FlushOnProcessExit)
        {
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        }
        if (finished) _sink.Dispose();
        return finished;
    }

    public void Dispose() => Shutdown(_options.ShutdownTimeout);

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
