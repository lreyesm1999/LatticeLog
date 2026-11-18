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
    private readonly struct Item(LogRecord record, long ticket)
    {
        public readonly LogRecord Record = record;
        public readonly long Ticket = ticket;
    }

    private sealed class FlushRequest(long ticket, AckLevel level, long errorsAtRequest)
    {
        public readonly long Ticket = ticket;
        public readonly AckLevel Level = level;
        public readonly long ErrorsAtRequest = errorsAtRequest;
        public readonly TaskCompletionSource<FlushResult> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly LatticeLoggerOptions _options;
    private readonly IRecordSink _sink;
    private readonly ConcurrentQueue<Item> _queue = new();
    private readonly ConcurrentQueue<long> _resolvedTickets = new();
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

    private long _ticket;
    private long _queueCount;
    private volatile int _writerSleeping;
    private volatile bool _closing;
    private volatile bool _stopped;
    private long _sampleCounter;

    // Counters
    private long _accepted, _filtered, _sampled, _droppedSat, _droppedDisposed, _droppedWrite, _written, _writtenOs, _fsynced, _bytes, _writeErrors;
    private long _ackedOs, _ackedFsync; // tickets watermark per level (writer thread only, read with Volatile)
    private long _lastFsyncTimestamp;

    public LatticeLogger(IRecordSink sink, LatticeLoggerOptions? options = null)
    {
        _options = options ?? new LatticeLoggerOptions();
        _options.Validate();
        _sink = sink;
        _minLevel = (int)_options.MinimumLevel;
        _modeAck = _options.Durability switch
        {
            DurabilityMode.Durable => AckLevel.FsyncConfirmed,
            DurabilityMode.Balanced => AckLevel.WrittenToOs,
            _ => AckLevel.WrittenToOs,
        };
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
            return false;
        }
        if (_closing)
        {
            Interlocked.Increment(ref _droppedDisposed);
            return false;
        }
        if (_options.SamplingKeepOneOf > 1 && record.Severity <= _options.SamplingMaxSeverity
            && Interlocked.Increment(ref _sampleCounter) % _options.SamplingKeepOneOf != 0)
        {
            Interlocked.Increment(ref _sampled);
            return false;
        }
        Enrich(record);
        var ticket = Interlocked.Increment(ref _ticket);
        if (!TryReserveSlot())
        {
            _resolvedTickets.Enqueue(ticket);
            Interlocked.Increment(ref _droppedSat);
            WakeWriter();
            if (_options.Saturation == SaturationPolicy.Throw) throw new LoggerSaturatedException($"LatticeLog queue full ({_options.QueueCapacity} records).");
            return false;
        }
        _queue.Enqueue(new Item(record, ticket));
        Interlocked.Increment(ref _accepted);
        if (_writerSleeping == 1 && (_options.Durability == DurabilityMode.Durable || Volatile.Read(ref _queueCount) >= _options.BatchMaxRecords)) WakeWriter();
        return true;
    }

    private bool TryReserveSlot()
    {
        var cap = _options.QueueCapacity;
        if (Interlocked.Increment(ref _queueCount) <= cap) return true;
        Interlocked.Decrement(ref _queueCount);
        if (_options.Saturation != SaturationPolicy.Block) return false;
        var deadline = Stopwatch.GetTimestamp() + (long)(_options.BlockTimeout.TotalSeconds * Stopwatch.Frequency);
        WakeWriter();
        while (true)
        {
            if (Interlocked.Increment(ref _queueCount) <= cap) return true;
            Interlocked.Decrement(ref _queueCount);
            var remaining = deadline - Stopwatch.GetTimestamp();
            if (remaining <= 0 || _closing) return false;
            _space.Reset();
            if (Volatile.Read(ref _queueCount) < cap) continue;
            WakeWriter();
            _space.Wait(TimeSpan.FromMilliseconds(Math.Min(50, remaining * 1000.0 / Stopwatch.Frequency)));
        }
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
        var req = new FlushRequest(Volatile.Read(ref _ticket), level, Volatile.Read(ref _writeErrors));
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
        Interlocked.Read(ref _accepted), Interlocked.Read(ref _filtered), Interlocked.Read(ref _sampled),
        Interlocked.Read(ref _droppedSat), Interlocked.Read(ref _droppedDisposed), Interlocked.Read(ref _droppedWrite),
        Interlocked.Read(ref _written), Interlocked.Read(ref _writtenOs), Interlocked.Read(ref _fsynced),
        Math.Max(0, Volatile.Read(ref _queueCount)), Interlocked.Read(ref _bytes), Interlocked.Read(ref _writeErrors), _sink.LastSequence);

    // ---------------------------------------------------------------------------------- writer thread

    private void WriterLoop()
    {
        var batch = new List<LogRecord>(_options.BatchMaxRecords);
        var batchTickets = new List<long>(_options.BatchMaxRecords);
        var completedOutOfOrder = new SortedSet<long>();
        long watermark = 0; // all tickets <= watermark are resolved (written or dropped)
        var delay = _options.EffectiveBatchDelay;
        while (true)
        {
            batch.Clear();
            batchTickets.Clear();
            while (batch.Count < _options.BatchMaxRecords && _queue.TryDequeue(out var item))
            {
                batch.Add(item.Record);
                batchTickets.Add(item.Ticket);
            }
            if (batch.Count > 0)
            {
                Interlocked.Add(ref _queueCount, -batch.Count);
                _space.Set();
                PersistBatch(batch);
                foreach (var t in batchTickets) completedOutOfOrder.Add(t);
            }
            while (_resolvedTickets.TryDequeue(out var t)) completedOutOfOrder.Add(t);
            while (completedOutOfOrder.Count > 0 && completedOutOfOrder.Min == watermark + 1)
            {
                completedOutOfOrder.Remove(watermark + 1);
                watermark++;
            }
            var achieved = AckAfterBatch(batch.Count > 0, watermark);
            CompleteFlushRequests(watermark, achieved);

            if (_closing && _queue.IsEmpty && Volatile.Read(ref _queueCount) <= 0 && watermark >= Volatile.Read(ref _ticket))
            {
                CompleteFlushRequests(watermark, achieved, final: true);
                return;
            }
            if (!_queue.IsEmpty && Volatile.Read(ref _queueCount) >= _options.BatchMaxRecords) continue;
            if (batch.Count > 0 && _options.Durability == DurabilityMode.Durable && !_queue.IsEmpty) continue;
            _writerSleeping = 1;
            if (_queue.IsEmpty || _options.Durability != DurabilityMode.Durable) _wake.Wait(delay);
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
        try
        {
            var bytes = _sink.WriteBatch(batch);
            Interlocked.Add(ref _bytes, bytes);
            Interlocked.Add(ref _written, batch.Count);
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
        var needFsync = false;
        if (_options.Durability == DurabilityMode.Durable && wroteSomething) needFsync = true;
        if (_options.Durability == DurabilityMode.Balanced && wroteSomething
            && Stopwatch.GetElapsedTime(_lastFsyncTimestamp) >= _options.BalancedFsyncInterval) needFsync = true;
        foreach (var req in _flushRequests)
        {
            if (req.Level >= AckLevel.FsyncConfirmed && req.Ticket <= watermark && Volatile.Read(ref _ackedFsync) < req.Ticket) needFsync = true;
        }
        var achieved = AckLevel.WrittenToOs; // the store sink writes unbuffered: a returned WriteBatch means the OS has the bytes
        if (needFsync)
        {
            try
            {
                achieved = _sink.Flush(AckLevel.FsyncConfirmed);
                if (achieved >= AckLevel.FsyncConfirmed)
                {
                    Volatile.Write(ref _ackedFsync, watermark);
                    Interlocked.Exchange(ref _fsynced, Interlocked.Read(ref _written));
                }
                _lastFsyncTimestamp = Stopwatch.GetTimestamp();
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
            var late = 0;
            while (_queue.TryDequeue(out _)) late++;
            if (late > 0)
            {
                Interlocked.Add(ref _droppedDisposed, late);
                Diagnose($"{late} records arrived during shutdown and were not persisted");
            }
        }
        if (!finished)
        {
            Diagnose($"shutdown timed out after {timeout}; {Volatile.Read(ref _queueCount)} records not persisted");
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
