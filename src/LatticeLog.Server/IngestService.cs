using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using LatticeLog.Ingest;
using LatticeLog.Storage;

namespace LatticeLog.Server;

public enum SubmitStatus { Acknowledged, SequenceGap, Overloaded, PersistFailed }

/// <summary>Writer counters: batches per flush shows how much group commit amortizes fsync.</summary>
public sealed record IngestStats(long Groups, long BatchesStored, long BatchesDuplicate, long RecordsStored, long Flushes, int QueueDepth);

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
    // Replicas (P19): for records relayed from another server, the original producer's batch currently being received
    // (first local sequence, records seen so far). Lets a replica deduplicate producers that fail over to it directly.
    private readonly Dictionary<string, (ulong First, ulong Count)> _upstream = new(StringComparer.Ordinal);
    private readonly object _cursorLock = new();
    // Held by the writer thread while it processes a group, and by admin operations that touch the store writer.
    private readonly object _storeGate = new();
    private readonly SemaphoreSlim _adminGate = new(1, 1);
    private DateTime _lastCheckpoint = DateTime.UtcNow;
    private volatile string? _failure;
    private bool _aborted;

    public IngestService(IngestServiceOptions options)
    {
        _options = options;
        _store = StoreWriter.Open(options.StoreDirectory, options.Store);
        RebuildCursors();
        _queue = new BlockingCollection<Pending>(options.QueueCapacity);
        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "LatticeLog.Ingest.Writer" };
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

    private long _groups, _batchesStored, _batchesDuplicate, _recordsStored, _flushes;

    public IngestStats GetStats() => new(Interlocked.Read(ref _groups), Interlocked.Read(ref _batchesStored), Interlocked.Read(ref _batchesDuplicate),
        Interlocked.Read(ref _recordsStored), Interlocked.Read(ref _flushes), _queue.Count);

    /// <summary>
    /// Per-batch work that does not need the dedup state, done on the request thread so the single writer only
    /// deduplicates and writes: arrival time and the provenance context (re-done by the writer only when it trims).
    /// </summary>
    public static void Prepare(DecodedBatch batch) => WrapProvenance(batch.Records, batch.ProducerId, batch.FirstLocalSeq, replacePrepared: false);

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
                        if (n["upstream"] is JsonObject up)
                            foreach (var (k, v) in up)
                                _upstream[k] = (ulong.Parse((string)v!["first"]!, CultureInfo.InvariantCulture), ulong.Parse((string)v["count"]!, CultureInfo.InvariantCulture));
                        fromSeq = s;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or FormatException or System.Text.Json.JsonException or NullReferenceException or InvalidOperationException)
            {
                _cursors.Clear();
                _upstream.Clear();
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
                if (TryGetUpstream(r, out var up, out var upFirst)) TrackUpstream(_upstream, _cursors, up, upFirst);
            }
        foreach (var ((producer, first), count) in counts)
        {
            var through = first + count - 1;
            if (through > _cursors.GetValueOrDefault(producer)) _cursors[producer] = through;
        }
        if (!report.IsClean) Diagnose($"rebuild found {report.Issues.Count} storage issue(s); lost records will be accepted again if resent");
        Diagnose($"dedup state rebuilt: {_cursors.Count} producer(s), scanned from server sequence {fromSeq}");
    }

    private static bool TryGetIngest(LogRecord r, out string producer, out ulong first) => TryGetIngestLayer(r, 0, out producer, out first);

    /// <summary>The provenance layer added by the server this record was relayed from (second "ingest" layer).</summary>
    private static bool TryGetUpstream(LogRecord r, out string producer, out ulong first) => TryGetIngestLayer(r, 1, out producer, out first);

    /// <summary>
    /// Records of an upstream batch arrive in order; a new first sequence starts a new batch. committed = first +
    /// records seen − 1. Works across replication batches and across a checkpoint (the partial state is saved).
    /// </summary>
    private static void TrackUpstream(Dictionary<string, (ulong First, ulong Count)> state, Dictionary<string, ulong> cursors, string producer, ulong first)
    {
        var s = state.TryGetValue(producer, out var cur) ? cur : (First: 0UL, Count: 0UL);
        if (s.First == first) s.Count++;
        else if (first > s.First) s = (first, 1);
        else return; // older batch replayed (already counted)
        state[producer] = s;
        var through = s.First + s.Count - 1;
        if (through > cursors.GetValueOrDefault(producer)) cursors[producer] = through;
    }

    private static bool TryGetIngestLayer(LogRecord r, int depth, out string producer, out ulong first)
    {
        var seen = 0;
        for (var c = r.Context; c is not null; c = c.Parent)
        {
            if (c.Kind != ContextKind.Custom || c.Name != IngestProtocol.IngestContextName) continue;
            if (seen++ < depth) continue;
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
        JsonObject cursors, upstream;
        lock (_cursorLock)
        {
            cursors = new JsonObject();
            foreach (var (k, v) in _cursors) cursors[k] = v.ToString(CultureInfo.InvariantCulture);
            upstream = new JsonObject();
            foreach (var (k, (first, count)) in _upstream)
                upstream[k] = new JsonObject { ["first"] = first.ToString(CultureInfo.InvariantCulture), ["count"] = count.ToString(CultureInfo.InvariantCulture) };
        }
        var json = new JsonObject
        {
            ["store_id"] = _store.Info.StoreId,
            ["store_seq"] = _store.LastSequence.ToString(CultureInfo.InvariantCulture),
            ["cursors"] = cursors,
            ["upstream"] = upstream,
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
        lock (_storeGate) ProcessGroupLocked(group);
    }

    private void ProcessGroupLocked(List<Pending> group)
    {
        Interlocked.Increment(ref _groups);
        if (_failure is { } failed)
        {
            foreach (var p in group) p.Done.TrySetResult(new SubmitResult(SubmitStatus.PersistFailed, Error: failed));
            return;
        }
        // Cursor changes become visible only after the shared flush succeeded.
        var tentative = new Dictionary<string, ulong>(StringComparer.Ordinal);
        Dictionary<string, (ulong First, ulong Count)>? tentativeUpstream = null;
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
                Interlocked.Increment(ref _batchesDuplicate);
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
            // Prepared on the request thread with first = FirstLocalSeq; only a trimmed batch needs new provenance, and
            // then only the layer this server added is replaced (a relayed record may carry an upstream "ingest" layer).
            if (!IsPrepared(b)) WrapProvenance(toStore, b.ProducerId, firstStored, replacePrepared: false);
            else if (skip > 0) WrapProvenance(toStore, b.ProducerId, firstStored, replacePrepared: true);
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
            Interlocked.Increment(ref _batchesStored);
            Interlocked.Add(ref _recordsStored, toStore.Count);
            if (p.Ack > level) level = p.Ack;
            tentative[b.ProducerId] = b.LastLocalSeq;
            foreach (var r in toStore)
            {
                if (!TryGetUpstream(r, out var up, out var upFirst)) continue;
                if (tentativeUpstream is null)
                    lock (_cursorLock) tentativeUpstream = new Dictionary<string, (ulong, ulong)>(_upstream, StringComparer.Ordinal);
                if (!tentative.ContainsKey(up)) tentative[up] = CommittedThrough(up);
                TrackUpstream(tentativeUpstream, tentative, up, upFirst);
            }
            p.Provisional = new IngestAck(skip == 0 ? "stored" : "partial_duplicate", b.ProducerId, b.LastLocalSeq, toStore.Count, skip,
                AckLevel.None, serverFirst, _store.LastSequence, gapBefore);
        }

        var achieved = AckLevel.WrittenToOs;
        if (_failure is null && (wrote || needFlush))
        {
            try { achieved = _store.Flush(level); Interlocked.Increment(ref _flushes); }
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
        {
            foreach (var (k, v) in tentative)
                if (v > _cursors.GetValueOrDefault(k)) _cursors[k] = v;
            if (tentativeUpstream is not null)
                foreach (var (k, v) in tentativeUpstream) _upstream[k] = v;
        }
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

    private static bool IsPrepared(DecodedBatch b)
        => b.Records.Count > 0 && b.Records[0].Context is { Kind: ContextKind.Custom, Name: IngestProtocol.IngestContextName } c
           && c.Attributes.Count == 2 && c.Attributes[0].Value.Kind == ValueKind.String && c.Attributes[0].Value.AsString() == b.ProducerId
           && c.Attributes[1].Value.Kind == ValueKind.Int64 && (ulong)c.Attributes[1].Value.AsInt64() == b.FirstLocalSeq;

    private static void WrapProvenance(List<LogRecord> records, string producer, ulong firstStored, bool replacePrepared)
    {
        // Arrival time (P17-T11): observed_time = when the collector received the record, unless the producer already
        // set it. "time" stays the event time chosen by the producer.
        var arrival = UnixTime.NowUnixNanos();
        foreach (var r in records)
            if (r.ObservedUnixNanos == 0) r.ObservedUnixNanos = arrival;
        var wrappers = new Dictionary<ContextSnapshot, ContextSnapshot>(ReferenceEqualityComparer.Instance);
        ContextSnapshot? rootWrapper = null;
        LogAttribute[] attrs = [new(IngestProtocol.AttrProducer, producer), new(IngestProtocol.AttrFirstLocalSeq, (long)firstStored)];
        foreach (var r in records)
        {
            // Re-wrapping a trimmed batch: replace the layer Prepare added (never stack two of ours).
            var original = replacePrepared && r.Context is { Kind: ContextKind.Custom, Name: IngestProtocol.IngestContextName } prepared ? prepared.Parent : r.Context;
            if (original is null)
                r.Context = rootWrapper ??= new ContextSnapshot(ContextKind.Custom, IngestProtocol.IngestContextName, attrs, null);
            else
            {
                if (!wrappers.TryGetValue(original, out var w))
                    wrappers[original] = w = new ContextSnapshot(ContextKind.Custom, IngestProtocol.IngestContextName, attrs, original);
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
        try { _options.Diagnostics?.Invoke("[LatticeLog.Ingest] " + message); } catch { }
    }

    // ------------------------------------------------------------------ online administration (P17-T12)

    public string StoreDirectory => _options.StoreDirectory;

    /// <summary>
    /// Retention while ingesting. The dedup checkpoint is written first: once old segments are gone, the cursors in
    /// the checkpoint are what keeps resent old batches from being stored again.
    /// </summary>
    public async Task<RetentionResult> ApplyRetentionAsync(RetentionPolicy policy, bool dryRun)
    {
        await _adminGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_storeGate)
            {
                if (!dryRun)
                {
                    _store.Flush(AckLevel.FsyncConfirmed); // seals finished, data durable before the checkpoint
                    WriteCheckpoint();
                }
                return _store.ApplyRetention(policy, dryRun: dryRun);
            }
        }
        finally { _adminGate.Release(); }
    }

    /// <summary>
    /// Compaction while ingesting: segments that existed and were sealed when it started are rewritten; the active
    /// segment and anything created later are excluded. Readers keep working (old files stay until the commit).
    /// </summary>
    public async Task<IReadOnlyList<CompactionResult>> CompactAsync(CompactionPolicy policy, long targetBytes, CancellationToken ct)
    {
        await _adminGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ulong excludeFrom;
            lock (_storeGate)
            {
                _store.Flush(AckLevel.FsyncConfirmed); // waits for background seals
                excludeFrom = _store.CurrentSegmentId ?? _store.NextSegmentId;
            }
            var results = await Task.Run(() => (IReadOnlyList<CompactionResult>)Compactor.CompactAll(_options.StoreDirectory, policy, _store.AllocateSegmentId, targetBytes, excludeFrom, ct), ct).ConfigureAwait(false);
            lock (_storeGate) _store.RefreshSizeAccounting();
            return results;
        }
        finally { _adminGate.Release(); }
    }

    /// <summary>
    /// Erasure (P18, "right to be forgotten"): the active segment is sealed first so matching records in it are
    /// covered, the dedup checkpoint is written (erased records must not be re-accepted if a producer resends them),
    /// then every sealed segment with matches is rewritten without them. Returns the records erased (or that would be,
    /// for a dry run). Copies outside this store (producer stores, exports, backups, replicas) are not affected.
    /// </summary>
    public async Task<(long Records, IReadOnlyList<CompactionResult> Rewrites)> EraseAsync(Func<LogRecord, bool> match, bool dryRun, CancellationToken ct)
    {
        await _adminGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ulong excludeFrom;
            lock (_storeGate)
            {
                if (!dryRun)
                {
                    _store.SealCurrent();
                    WriteCheckpoint();
                }
                else _store.Flush(AckLevel.FsyncConfirmed);
                excludeFrom = dryRun ? _store.CurrentSegmentId ?? _store.NextSegmentId : _store.NextSegmentId;
            }
            if (dryRun) return (await Task.Run(() => Compactor.CountMatching(_options.StoreDirectory, match, ulong.MaxValue), ct).ConfigureAwait(false), []);
            var rewrites = await Task.Run(() => (IReadOnlyList<CompactionResult>)Compactor.EraseMatching(_options.StoreDirectory, match, CompactionPolicy.Balanced, _store.AllocateSegmentId, excludeFrom, ct), ct).ConfigureAwait(false);
            lock (_storeGate) _store.RefreshSizeAccounting();
            return (rewrites.Sum(r => r.Erased), rewrites);
        }
        finally { _adminGate.Release(); }
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
