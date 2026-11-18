using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using LatticeLog.Ingest;
using LatticeLog.Storage;

namespace LatticeLog.Server;

public enum SubmitStatus { Acknowledged, SequenceGap, Overloaded, PersistFailed }

public sealed record SubmitResult(SubmitStatus Status, IngestAck? Ack = null, ulong CommittedThrough = 0, string? Error = null);

public sealed record IngestServiceOptions
{
    public required string StoreDirectory { get; init; }
    public StoreWriterOptions Store { get; init; } = new();
    /// <summary>Batches waiting for the writer; beyond this, requests get 503 + Retry-After (network backpressure).</summary>
    public int QueueCapacity { get; init; } = 256;
    /// <summary>Batches written before one shared fsync (group commit).</summary>
    public int MaxGroup { get; init; } = 64;
    public TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromSeconds(30);
    public Action<string>? Diagnostics { get; init; }
}

/// <summary>
/// Single-writer ingestion core (ADR-0009): deduplicates by (producer, local sequence), stores each batch with an
/// <c>ingest</c> provenance context, acknowledges after one shared flush per group, and rebuilds its dedup state from
/// the stored data after a restart.
/// </summary>
public sealed class IngestService : IDisposable
{
    private sealed class Pending(DecodedBatch batch, AckLevel ack)
    {
        public readonly DecodedBatch Batch = batch;
        public readonly AckLevel Ack = ack;
        public readonly TaskCompletionSource<SubmitResult> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IngestAck? Provisional;
        public SubmitResult? Immediate;
    }

    public const string CheckpointFile = "INGEST-CHECKPOINT.json";

    private readonly IngestServiceOptions _options;
    private readonly StoreWriter _store;
    private readonly BlockingCollection<Pending> _queue;
    private readonly Thread _writer;
    private readonly Dictionary<string, ulong> _cursors = new(StringComparer.Ordinal);
    private readonly object _cursorLock = new();
    private DateTime _lastCheckpoint = DateTime.UtcNow;
    private volatile string? _failure;
    private bool _aborted;

    public IngestService(IngestServiceOptions options)
    {
        _options = options;
        _store = StoreWriter.Open(options.StoreDirectory, options.Store);
        RebuildCursors();
        _queue = new BlockingCollection<Pending>(options.QueueCapacity);
        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "LatticeLog.Collector.Writer" };
        _writer.Start();
    }

    public string StoreId => _store.Info.StoreId;
    public ulong LastSequence => _store.LastSequence;
    public string? Failure => _failure;

    public ulong CommittedThrough(string producer)
    {
        lock (_cursorLock) return _cursors.GetValueOrDefault(producer);
    }

    public int ProducerCount { get { lock (_cursorLock) return _cursors.Count; } }

    public Task<SubmitResult> SubmitAsync(DecodedBatch batch, AckLevel ack)
    {
        if (_failure is { } f) return Task.FromResult(new SubmitResult(SubmitStatus.PersistFailed, Error: f));
        var p = new Pending(batch, ack);
        if (!_queue.TryAdd(p)) return Task.FromResult(new SubmitResult(SubmitStatus.Overloaded, Error: "writer queue full"));
        return p.Done.Task;
    }

    // ------------------------------------------------------------------ dedup state

    /// <summary>
    /// committed_through[p] = max(first_local_seq + records actually stored with that ingest context - 1), starting
    /// from the checkpoint (taken only at group boundaries, so no batch straddles it).
    /// </summary>
    private void RebuildCursors()
    {
        ulong fromSeq = 0;
        var path = Path.Combine(_options.StoreDirectory, CheckpointFile);
        if (File.Exists(path))
        {
            try
            {
                var n = JsonNode.Parse(File.ReadAllText(path))!;
                if ((string?)n["store_id"] == _store.Info.StoreId)
                {
                    var s = ulong.Parse((string)n["store_seq"]!, CultureInfo.InvariantCulture);
                    if (s <= _store.LastSequence)
                    {
                        foreach (var (k, v) in n["cursors"]!.AsObject()) _cursors[k] = ulong.Parse((string)v!, CultureInfo.InvariantCulture);
                        fromSeq = s;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or FormatException or System.Text.Json.JsonException or NullReferenceException or InvalidOperationException)
            {
                _cursors.Clear();
                fromSeq = 0;
                Diagnose("checkpoint unreadable, rebuilding from all data: " + ex.Message);
            }
        }
        var counts = new Dictionary<(string Producer, ulong First), ulong>();
        var reader = StoreReader.Open(_options.StoreDirectory);
        var report = new ScanReport();
        foreach (var block in reader.ReadBlocks(report, (_, idx) => idx is null || !idx.HasRecords || idx.LastSequence > fromSeq,
                     b => b.Type != Storage.Format.BlockType.Records || b.LastSequence > fromSeq))
            foreach (var r in block.Records)
            {
                if (r.Sequence <= fromSeq || !TryGetIngest(r, out var producer, out var first)) continue;
                counts[(producer, first)] = counts.GetValueOrDefault((producer, first)) + 1;
            }
        foreach (var ((producer, first), count) in counts)
        {
            var through = first + count - 1;
            if (through > _cursors.GetValueOrDefault(producer)) _cursors[producer] = through;
        }
        if (!report.IsClean) Diagnose($"rebuild found {report.Issues.Count} storage issue(s); lost records will be accepted again if resent");
        Diagnose($"dedup state rebuilt: {_cursors.Count} producer(s), scanned from server sequence {fromSeq}");
    }

    private static bool TryGetIngest(LogRecord r, out string producer, out ulong first)
    {
        for (var c = r.Context; c is not null; c = c.Parent)
        {
            if (c.Kind != ContextKind.Custom || c.Name != IngestProtocol.IngestContextName) continue;
            string? p = null;
            long f = 0;
            foreach (var a in c.Attributes)
            {
                if (a.Key == IngestProtocol.AttrProducer && a.Value.Kind == ValueKind.String) p = a.Value.AsString();
                else if (a.Key == IngestProtocol.AttrFirstLocalSeq && a.Value.Kind == ValueKind.Int64) f = a.Value.AsInt64();
            }
            if (p is not null && f > 0) { producer = p; first = (ulong)f; return true; }
        }
        producer = "";
        first = 0;
        return false;
    }

    private void WriteCheckpoint()
    {
        JsonObject cursors;
        lock (_cursorLock)
        {
            cursors = new JsonObject();
            foreach (var (k, v) in _cursors) cursors[k] = v.ToString(CultureInfo.InvariantCulture);
        }
        var json = new JsonObject
        {
            ["store_id"] = _store.Info.StoreId,
            ["store_seq"] = _store.LastSequence.ToString(CultureInfo.InvariantCulture),
            ["cursors"] = cursors,
        }.ToJsonString();
        var path = Path.Combine(_options.StoreDirectory, CheckpointFile);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(Encoding.UTF8.GetBytes(json));
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
        _lastCheckpoint = DateTime.UtcNow;
    }

    // ------------------------------------------------------------------ writer

    private void WriterLoop()
    {
        var group = new List<Pending>(_options.MaxGroup);
        foreach (var first in _queue.GetConsumingEnumerable())
        {
            group.Clear();
            group.Add(first);
            while (group.Count < _options.MaxGroup && _queue.TryTake(out var more)) group.Add(more);
            ProcessGroup(group);
        }
    }

    private void ProcessGroup(List<Pending> group)
    {
        if (_failure is { } failed)
        {
            foreach (var p in group) p.Done.TrySetResult(new SubmitResult(SubmitStatus.PersistFailed, Error: failed));
            return;
        }
        // Cursor changes become visible only after the shared flush succeeded.
        var tentative = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var wrote = false;
        var needFlush = false;
        var level = AckLevel.WrittenToOs;
        foreach (var p in group)
        {
            var b = p.Batch;
            var committed = tentative.TryGetValue(b.ProducerId, out var t) ? t : CommittedThrough(b.ProducerId);
            if (b.LastLocalSeq <= committed)
            {
                // Already stored, perhaps by a request that asked for less: the shared flush below brings everything
                // written so far to this request's level before answering.
                if (p.Ack > level) level = p.Ack;
                needFlush = true;
                p.Provisional = new IngestAck("duplicate", b.ProducerId, committed, 0, b.Records.Count, AckLevel.None, 0, 0, 0);
                continue;
            }
            ulong gapBefore = 0;
            if (b.FirstLocalSeq > committed + 1)
            {
                if (!b.GapAcknowledged)
                {
                    p.Immediate = new SubmitResult(SubmitStatus.SequenceGap, CommittedThrough: committed, Error: "sequence_gap");
                    continue;
                }
                gapBefore = b.FirstLocalSeq - committed - 1;
            }
            var skip = b.FirstLocalSeq <= committed ? (int)(committed - b.FirstLocalSeq + 1) : 0;
            var toStore = skip == 0 ? b.Records : b.Records.GetRange(skip, b.Records.Count - skip);
            var firstStored = b.FirstLocalSeq + (ulong)skip;
            WrapProvenance(toStore, b.ProducerId, firstStored);
            var serverFirst = _store.LastSequence + 1;
            try
            {
                _store.WriteBatch(toStore);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Fail($"write failed: {ex.Message}");
                break;
            }
            wrote = true;
            if (p.Ack > level) level = p.Ack;
            tentative[b.ProducerId] = b.LastLocalSeq;
            p.Provisional = new IngestAck(skip == 0 ? "stored" : "partial_duplicate", b.ProducerId, b.LastLocalSeq, toStore.Count, skip,
                AckLevel.None, serverFirst, _store.LastSequence, gapBefore);
        }

        var achieved = AckLevel.WrittenToOs;
        if (_failure is null && (wrote || needFlush))
        {
            try { achieved = _store.Flush(level); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Fail($"flush failed: {ex.Message}");
            }
        }
        if (_failure is { } f)
        {
            foreach (var p in group) p.Done.TrySetResult(p.Immediate ?? new SubmitResult(SubmitStatus.PersistFailed, Error: f));
            return;
        }
        lock (_cursorLock)
            foreach (var (k, v) in tentative)
                if (v > _cursors.GetValueOrDefault(k)) _cursors[k] = v;
        foreach (var p in group)
        {
            if (p.Immediate is { } imm) { p.Done.TrySetResult(imm); continue; }
            p.Done.TrySetResult(new SubmitResult(SubmitStatus.Acknowledged, p.Provisional! with { Ack = achieved }));
        }
        if (wrote && DateTime.UtcNow - _lastCheckpoint >= _options.CheckpointInterval)
        {
            try { WriteCheckpoint(); } catch (IOException ex) { Diagnose("checkpoint failed: " + ex.Message); }
        }
    }

    private static void WrapProvenance(List<LogRecord> records, string producer, ulong firstStored)
    {
        var wrappers = new Dictionary<ContextSnapshot, ContextSnapshot>(ReferenceEqualityComparer.Instance);
        ContextSnapshot? rootWrapper = null;
        LogAttribute[] attrs = [new(IngestProtocol.AttrProducer, producer), new(IngestProtocol.AttrFirstLocalSeq, (long)firstStored)];
        foreach (var r in records)
        {
            if (r.Context is null)
                r.Context = rootWrapper ??= new ContextSnapshot(ContextKind.Custom, IngestProtocol.IngestContextName, attrs, null);
            else
            {
                if (!wrappers.TryGetValue(r.Context, out var w))
                    wrappers[r.Context] = w = new ContextSnapshot(ContextKind.Custom, IngestProtocol.IngestContextName, attrs, r.Context);
                r.Context = w;
            }
        }
    }

    /// <summary>After a write or flush failure the in-memory state may be ahead of the disk: stop accepting until a
    /// restart rebuilds the truth from the stored data.</summary>
    private void Fail(string message)
    {
        _failure ??= message;
        Diagnose("FAILED, refusing writes until restart: " + message);
    }

    private void Diagnose(string message)
    {
        try { _options.Diagnostics?.Invoke("[LatticeLog.Collector] " + message); } catch { }
    }

    /// <summary>Test-only crash simulation: stop without writing a checkpoint (the next start rebuilds from data).</summary>
    public void Abort()
    {
        _aborted = true;
        Dispose();
    }

    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.CompleteAdding();
        _writer.Join();
        if (!_aborted && _failure is null)
        {
            try { WriteCheckpoint(); } catch (IOException ex) { Diagnose("final checkpoint failed: " + ex.Message); }
        }
        _store.Dispose();
        _queue.Dispose();
    }
}
