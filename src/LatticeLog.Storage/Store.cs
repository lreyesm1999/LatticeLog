using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LatticeLog.Storage.Format;

namespace LatticeLog.Storage;

/// <summary>Directory layout of a local store (specs/llog-format-v0.md §9).</summary>
public static class StoreLayout
{
    public const string StoreFile = "STORE.json";
    public const string ManifestFile = "MANIFEST.jsonl";
    public const string LockFile = "LOCK";
    public const string SegmentsDir = "segments";
    public const string RecoveredDir = "recovered";

    public static string SegmentPath(string root, ulong id) => Path.Combine(root, SegmentsDir, $"seg-{id:D10}.llog");
    public static string TempSegmentPath(string root, ulong id) => SegmentPath(root, id) + ".tmp";

    public static bool TryParseSegmentId(string fileName, out ulong id)
    {
        id = 0;
        var name = Path.GetFileName(fileName);
        return name.StartsWith("seg-", StringComparison.Ordinal) && name.EndsWith(".llog", StringComparison.Ordinal)
            && ulong.TryParse(name.AsSpan(4, name.Length - 9), NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }

    public static bool IsStore(string root) => File.Exists(Path.Combine(root, StoreFile));
}

public sealed record StoreInfo(string StoreId, int Version, string Created);

/// <summary>
/// Append-only journal of store transitions. Each line is "&lt;crc32 hex&gt; &lt;json&gt;".
/// A torn last line is ignored and reported; it never contains committed state because the
/// writer fsyncs after each append (P05-T08).
/// </summary>
public sealed class Manifest
{
    private readonly string _path;
    public List<JsonObject> Entries { get; } = [];
    public List<string> Problems { get; } = [];

    private Manifest(string path) => _path = path;

    public static Manifest Load(string root)
    {
        var m = new Manifest(Path.Combine(root, StoreLayout.ManifestFile));
        if (!File.Exists(m._path)) return m;
        using var fs = new FileStream(m._path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        var lineNo = 0;
        while (sr.ReadLine() is { } line)
        {
            lineNo++;
            if (line.Length == 0) continue;
            if (line.Length < 10 || line[8] != ' ' || !uint.TryParse(line.AsSpan(0, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var crc))
            {
                m.Problems.Add($"line {lineNo}: malformed");
                continue;
            }
            var json = line[9..];
            if (Checksum.Crc32(Encoding.UTF8.GetBytes(json)) != crc)
            {
                m.Problems.Add($"line {lineNo}: checksum mismatch (torn or corrupt entry ignored)");
                continue;
            }
            try
            {
                if (JsonNode.Parse(json) is JsonObject o) m.Entries.Add(o);
            }
            catch (JsonException) { m.Problems.Add($"line {lineNo}: invalid json"); }
        }
        return m;
    }

    private static readonly object AppendLock = new();

    public static void Append(string root, JsonObject entry)
    {
        lock (AppendLock) AppendCore(root, entry);
    }

    private static void AppendCore(string root, JsonObject entry)
    {
        entry["at"] = UnixTime.FormatRfc3339(UnixTime.NowUnixNanos());
        var json = entry.ToJsonString();
        var line = Checksum.Crc32(Encoding.UTF8.GetBytes(json)).ToString("x8", CultureInfo.InvariantCulture) + " " + json + "\n";
        using var fs = new FileStream(Path.Combine(root, StoreLayout.ManifestFile), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        fs.Write(Encoding.UTF8.GetBytes(line));
        fs.Flush(flushToDisk: true);
    }

    /// <summary>Segment ids replaced by committed compactions or deleted by retention. Readers must not return them.</summary>
    public HashSet<ulong> RetiredSegments()
    {
        var set = new HashSet<ulong>();
        var pendingOutputs = new HashSet<ulong>();
        foreach (var e in Entries)
        {
            var op = (string?)e["op"];
            switch (op)
            {
                case "compaction_started":
                    pendingOutputs.Add((ulong)e["output"]!);
                    break;
                case "compaction_committed":
                    pendingOutputs.Remove((ulong)e["output"]!);
                    if (e["inputs"] is JsonArray inputs) foreach (var i in inputs) set.Add((ulong)i!);
                    break;
                case "compaction_aborted":
                    pendingOutputs.Remove((ulong)e["output"]!);
                    set.Add((ulong)e["output"]!);
                    break;
                case "segment_deleted":
                    set.Add((ulong)e["id"]!);
                    break;
            }
        }
        // Outputs of compactions that never committed are invisible: the inputs remain authoritative.
        set.UnionWith(pendingOutputs);
        return set;
    }
}

public sealed class StoreLockedException(string message) : IOException(message);
public sealed class StoreQuotaExceededException(string message) : IOException(message);

public sealed record StoreWriterOptions
{
    public SegmentWriterOptions Segment { get; init; } = new();
    public long SegmentMaxBytes { get; init; } = 64L * 1024 * 1024;
    public TimeSpan SegmentMaxAge { get; init; } = TimeSpan.FromHours(1);
    /// <summary>Total store size limit. 0 = unlimited. When exceeded, writes fail explicitly (never silent deletion).</summary>
    public long MaxStoreBytes { get; init; }
    /// <summary>Minimum free disk space to keep. Writes fail explicitly below this threshold.</summary>
    public long MinFreeDiskBytes { get; init; } = 64L * 1024 * 1024;
    /// <summary>Operations slower than this (append, fsync, seal, segment creation, manifest) are reported to <see cref="Diagnostics"/>.</summary>
    public TimeSpan SlowOperationThreshold { get; init; } = TimeSpan.FromMilliseconds(50);
    /// <summary>Optional sink for slow-operation reports (writer or durability thread; must not block or throw).</summary>
    public Action<string>? Diagnostics { get; init; }
    /// <summary>
    /// Seal rotated segments and run requested fsyncs on a background thread instead of the writer thread (default).
    /// Synchronous <see cref="StoreWriter.Flush"/> still waits for both. False = everything inline, as before P14.
    /// </summary>
    public bool BackgroundDurability { get; init; } = true;
    /// <summary>Optional retention applied after each rotation (null = never delete anything automatically).</summary>
    public RetentionPolicy? Retention { get; init; }
}

public sealed record RecoveryResult(ulong SegmentId, long TruncatedBytes, string? TailCopy, IReadOnlyList<ScanIssue> Issues);

/// <summary>
/// Single-writer local store (P05). Holds an exclusive OS lock on LOCK so a second process
/// fails fast instead of corrupting data (P05-T12). Not thread-safe: called from one writer thread.
/// </summary>
public sealed class StoreWriter : IRecordSink
{
    private readonly string _root;
    private readonly StoreWriterOptions _options;
    private readonly FileStream _lock;
    private SegmentWriter? _current;
    private ulong _nextSegmentId;
    private ulong _lastSequence;
    private long _sealedBytes;
    private long _currentOpenedAt;
    private bool _disposed;
    private DurabilityWorker? _durability;

    public string Root => _root;
    public StoreInfo Info { get; }
    public ulong LastSequence => _lastSequence;

    /// <summary>Records are encoded during <see cref="WriteBatch"/> and never referenced afterwards.</summary>
    public bool RetainsRecords => false;
    public IReadOnlyList<RecoveryResult> Recovery { get; }
    public long TotalBytes => Interlocked.Read(ref _sealedBytes) + (_current?.Length ?? 0);
    /// <summary>Segments closed for writing (rotated or sealed). With background durability, sealing may still be in progress.</summary>
    public int SegmentsSealed { get; private set; }

    /// <summary>Segment currently open for writing (null between rotations).</summary>
    public ulong? CurrentSegmentId => _current?.SegmentId;

    /// <summary>Id the next segment will get (segments at or above it do not exist yet).</summary>
    public ulong NextSegmentId => Interlocked.Read(ref _nextSegmentId);

    /// <summary>Every record with a sequence &lt;= this value is on stable storage.</summary>
    public ulong DurableSequence => Math.Max(_durability?.DurableSequence ?? 0, Volatile.Read(ref _syncDurable));
    private ulong _syncDurable;

    /// <summary>Test-only: fault injection on the durability thread (see <see cref="DurabilityWorker.BeforeWork"/>).</summary>
    internal Action<string>? DurabilityFaultInjection
    {
        set { if (_durability is not null) _durability.BeforeWork = value; }
    }

    /// <summary>Background durability work still queued or running (seals, fsyncs).</summary>
    public int PendingDurabilityWork => _durability?.Pending ?? 0;

    /// <summary>
    /// Starts an fsync of everything written so far on the durability thread and returns at once (true). Returns false
    /// when background durability is disabled; the caller must then use <see cref="Flush"/>.
    /// </summary>
    public bool RequestBackgroundFsync()
    {
        if (_durability is null) return false;
        if (_current is not null && _lastSequence > 0) _durability.EnqueueFsync(_current, _lastSequence);
        return true;
    }

    private StoreWriter(string root, StoreWriterOptions options, FileStream lockStream, StoreInfo info)
    {
        _root = root;
        _options = options;
        _lock = lockStream;
        Info = info;
        var recovery = new List<RecoveryResult>();
        Recover(recovery);
        Recovery = recovery;
        _syncDurable = _lastSequence; // everything recovered is sealed (fsynced)
        if (options.BackgroundDurability) _durability = new DurabilityWorker(_lastSequence, Slow);
    }

    /// <summary>Creates the store if needed and opens it for writing.</summary>
    public static StoreWriter Open(string root, StoreWriterOptions? options = null)
    {
        options ??= new StoreWriterOptions();
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, StoreLayout.SegmentsDir));
        FileStream lockStream;
        try
        {
            lockStream = new FileStream(Path.Combine(root, StoreLayout.LockFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
        }
        catch (IOException ex)
        {
            throw new StoreLockedException($"Store '{root}' is locked by another writer process ({ex.Message}). Only one writer per directory is supported.");
        }
        try
        {
            var info = EnsureStoreFile(root);
            return new StoreWriter(root, options, lockStream, info);
        }
        catch
        {
            lockStream.Dispose();
            throw;
        }
    }

    public static StoreInfo EnsureStoreFile(string root)
    {
        var path = Path.Combine(root, StoreLayout.StoreFile);
        if (File.Exists(path)) return ReadStoreFile(root);
        var info = new StoreInfo(Guid.NewGuid().ToString("N"), 1, UnixTime.FormatRfc3339(UnixTime.NowUnixNanos()));
        var json = new JsonObject { ["format"] = "latticelog.store", ["version"] = info.Version, ["store_id"] = info.StoreId, ["created"] = info.Created }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        {
            fs.Write(Encoding.UTF8.GetBytes(json));
            fs.Flush(true);
        }
        File.Move(tmp, path, overwrite: false);
        return info;
    }

    public static StoreInfo ReadStoreFile(string root)
    {
        var node = JsonNode.Parse(File.ReadAllText(Path.Combine(root, StoreLayout.StoreFile))) as JsonObject
                   ?? throw new LlogFormatException("STORE.json is not an object.");
        if ((string?)node["format"] != "latticelog.store") throw new LlogFormatException("STORE.json has an unknown format.");
        var version = (int?)node["version"] ?? 0;
        if (version != 1) throw new LlogFormatException($"Unsupported store version {version}.");
        return new StoreInfo((string?)node["store_id"] ?? throw new LlogFormatException("STORE.json without store_id."), version, (string?)node["created"] ?? "");
    }

    private void Recover(List<RecoveryResult> results)
    {
        CompactionRecovery.Complete(_root);
        var manifest = Manifest.Load(_root);
        var retired = manifest.RetiredSegments();
        var deletedByRetention = manifest.Entries.Where(e => (string?)e["op"] == "segment_deleted").Select(e => (ulong)e["id"]!).ToHashSet();
        ulong maxId = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(_root, StoreLayout.SegmentsDir), "seg-*.llog").Order(StringComparer.Ordinal))
        {
            if (!StoreLayout.TryParseSegmentId(file, out var id)) continue;
            maxId = Math.Max(maxId, id);
            if (retired.Contains(id))
            {
                // Retention journaled the deletion but the process stopped before removing the file.
                if (deletedByRetention.Contains(id)) TryDeleteFile(file);
                continue;
            }
            SegmentIndex? index;
            bool sealedOk;
            try
            {
                using var reader = SegmentReader.Open(file);
                index = reader.Index;
                sealedOk = reader.IsSealed;
            }
            catch (LlogFormatException ex)
            {
                Quarantine(_root, file, id, ex.Message);
                continue;
            }
            if (!sealedOk)
            {
                var r = RecoverSegment(_root, file, id);
                results.Add(r);
                using var reader = SegmentReader.Open(file);
                index = reader.Index;
            }
            if (index is not null && index.HasRecords) _lastSequence = Math.Max(_lastSequence, index.LastSequence);
            _sealedBytes += new FileInfo(file).Length;
        }
        _nextSegmentId = maxId + 1;
    }

    /// <summary>Moves an unreadable segment (e.g. crash before its header was complete) to recovered/. Nothing is deleted.</summary>
    private static void Quarantine(string root, string file, ulong id, string reason)
    {
        Directory.CreateDirectory(Path.Combine(root, StoreLayout.RecoveredDir));
        var dest = Path.Combine(root, StoreLayout.RecoveredDir, $"seg-{id:D10}.quarantined-{DateTime.UtcNow:yyyyMMddHHmmssfff}.bin");
        File.Move(file, dest);
        Manifest.Append(root, new JsonObject { ["op"] = "segment_quarantined", ["id"] = id, ["reason"] = reason, ["moved_to"] = Path.GetFileName(dest) });
    }

    /// <summary>Allocates a segment id for compaction outputs while this writer is active.</summary>
    public ulong AllocateSegmentId() => Interlocked.Increment(ref _nextSegmentId) - 1;

    /// <summary>
    /// Recovery of an unsealed segment: the bytes after the last valid block (torn write) are copied to
    /// recovered/ for forensics, the file is cut to its valid prefix, and a footer is appended. Partial
    /// frames are never interpreted as records (P05-T15).
    /// </summary>
    public static RecoveryResult RecoverSegment(string root, string file, ulong id)
    {
        var report = new ScanReport();
        long validEnd;
        long length;
        using (var reader = SegmentReader.Open(file))
        {
            validEnd = reader.FindValidPrefixEnd(report);
            length = reader.Length;
        }
        string? tailCopy = null;
        var truncated = length - validEnd;
        if (truncated > 0)
        {
            Directory.CreateDirectory(Path.Combine(root, StoreLayout.RecoveredDir));
            tailCopy = Path.Combine(root, StoreLayout.RecoveredDir, $"seg-{id:D10}.tail-{DateTime.UtcNow:yyyyMMddHHmmssfff}.bin");
            using (var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var dst = new FileStream(tailCopy, FileMode.CreateNew, FileAccess.Write))
            {
                src.Position = validEnd;
                src.CopyTo(dst);
                dst.Flush(true);
            }
        }
        SegmentIndex index;
        var rebuildReport = new ScanReport();
        using (var fs = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            fs.SetLength(validEnd);
            fs.Flush(true);
        }
        using (var reader = SegmentReader.Open(file)) index = reader.RebuildIndex(rebuildReport);
        AppendFooter(file, index);
        var issues = report.Issues.Concat(rebuildReport.Issues).ToList();
        Manifest.Append(root, new JsonObject
        {
            ["op"] = "segment_recovered",
            ["id"] = id,
            ["truncated_bytes"] = truncated,
            ["tail_copy"] = tailCopy is null ? null : Path.GetFileName(tailCopy),
            ["records"] = index.RecordCount,
            ["issues"] = issues.Count,
        });
        return new RecoveryResult(id, truncated, tailCopy, issues);
    }

    private static void AppendFooter(string file, SegmentIndex index)
    {
        var payload = new ByteWriter();
        index.Encode(payload);
        using var fs = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var footerOffset = fs.Length;
        fs.Position = footerOffset;
        var frame = new byte[Llog.FrameHeaderSize + payload.Length + Llog.TrailerSize];
        new FrameHeader(BlockType.Footer, CodecId.Raw, Compression.None, 0, (uint)payload.Length, (uint)payload.Length, 0, Checksum.Crc32(payload.WrittenSpan)).Write(frame);
        payload.WrittenSpan.CopyTo(frame.AsSpan(Llog.FrameHeaderSize));
        new Trailer((ulong)footerOffset, 0).Write(frame.AsSpan(Llog.FrameHeaderSize + payload.Length));
        fs.Write(frame);
        fs.Flush(true);
        payload.Release();
    }

    /// <summary>Assigns sequence numbers and appends the batch to the current segment.</summary>
    public long WriteBatch(IReadOnlyList<LogRecord> batch)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (batch.Count == 0) return 0;
        CheckSpace();
        RotateIfNeeded();
        SegmentWriter seg;
        if (_current is null)
        {
            var t0 = Stopwatch.GetTimestamp();
            seg = _current = OpenNewSegment();
            Slow("open segment", t0);
        }
        else seg = _current;
        var firstSeq = _lastSequence + 1;
        for (var i = 0; i < batch.Count; i++)
        {
            var r = batch[i];
            r.Sequence = firstSeq + (ulong)i;
            SanitizeNames(r);
        }
        try
        {
            var tAppend = Stopwatch.GetTimestamp();
            var written = seg.Append(batch, 0, batch.Count);
            Slow($"append {batch.Count} records ({written} bytes)", tAppend);
            _lastSequence = firstSeq + (ulong)batch.Count - 1;
            return written;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Records of this batch were NOT persisted; sequence numbers are not consumed.
            for (var i = 0; i < batch.Count; i++) batch[i].Sequence = 0;
            AbandonCurrent();
            throw;
        }
    }

    private static void SanitizeNames(LogRecord r)
    {
        var lossy = false;
        var attrs = r.Attributes;
        for (var i = 0; i < attrs.Length; i++)
        {
            if (Value.HasLoneSurrogate(attrs[i].Key))
            {
                if (!lossy) { attrs = (LogAttribute[])attrs.Clone(); lossy = true; }
                attrs[i] = attrs[i] with { Key = ValueCodec.SanitizeKey(attrs[i].Key) };
            }
        }
        if (lossy) r.Attributes = attrs;
        string? Fix(string? s) { if (s is not null && Value.HasLoneSurrogate(s)) { lossy = true; return ValueCodec.SanitizeKey(s); } return s; }
        r.EventName = Fix(r.EventName);
        r.OperationId = Fix(r.OperationId);
        r.ParentOperationId = Fix(r.ParentOperationId);
        r.CorrelationId = Fix(r.CorrelationId);
        if (lossy) r.Flags |= RecordFlags.LossyImport;
    }

    private void CheckSpace()
    {
        if (_options.MaxStoreBytes > 0 && TotalBytes >= _options.MaxStoreBytes)
            throw new StoreQuotaExceededException($"Store quota of {_options.MaxStoreBytes} bytes reached; refusing to write (no automatic deletion).");
        if (_options.MinFreeDiskBytes > 0)
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_root))!);
                if (drive.IsReady && drive.AvailableFreeSpace < _options.MinFreeDiskBytes)
                    throw new StoreQuotaExceededException($"Free disk space below {_options.MinFreeDiskBytes} bytes; refusing to write.");
            }
            catch (ArgumentException) { /* drive info not available (e.g. UNC); rely on IOException */ }
        }
    }

    /// <summary>Reports an operation that started at <paramref name="startTimestamp"/> if it exceeded the threshold.</summary>
    private void Slow(string operation, long startTimestamp)
    {
        var d = _options.Diagnostics;
        if (d is null) return;
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
        if (elapsed < _options.SlowOperationThreshold) return;
        try { d($"store: {operation} took {elapsed.TotalMilliseconds:F1} ms"); } catch { /* diagnostics never break writes */ }
    }

    private void RotateIfNeeded()
    {
        if (_current is null) return;
        var age = UnixTime.NowUnixNanos() - _currentOpenedAt;
        if (_current.Length >= _options.SegmentMaxBytes || age >= _options.SegmentMaxAge.Ticks * 100)
        {
            CloseCurrent(wait: false);
            if (_options.Retention is { IsEmpty: false } policy)
            {
                try { ApplyRetention(policy); }
                catch (IOException ex) { try { _options.Diagnostics?.Invoke("store: retention failed: " + ex.Message); } catch { } }
            }
        }
    }

    /// <summary>
    /// Applies <paramref name="policy"/> now (writer thread or with the writer idle). Only sealed segments whose seal
    /// has completed are candidates; the active segment and segments still being sealed are skipped.
    /// </summary>
    /// <param name="keepNewest">Never delete the newest segment, so the store keeps its last sequence number (stores fed by
    /// a checkpointed importer).</param>
    public RetentionResult ApplyRetention(RetentionPolicy policy, long? nowUnixNanos = null, bool dryRun = false, bool keepNewest = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var reader = StoreReader.Open(_root);
        var current = _current?.SegmentId;
        var newest = keepNewest && reader.SegmentIds.Count > 0 ? reader.SegmentIds.Max() : (ulong?)null;
        var plan = Retention.Plan(reader, policy, nowUnixNanos ?? UnixTime.NowUnixNanos(), id => id != current && id != newest);
        if (!dryRun)
        {
            Retention.Execute(_root, plan);
            foreach (var d in plan) Interlocked.Add(ref _sealedBytes, -d.Bytes);
        }
        return new RetentionResult(plan, dryRun);
    }

    /// <summary>Recomputes the size of closed segments from disk (after an in-process compaction changed them).</summary>
    public void RefreshSizeAccounting()
    {
        var reader = StoreReader.Open(_root);
        long total = 0;
        foreach (var id in reader.SegmentIds)
        {
            if (id == _current?.SegmentId) continue;
            try { total += new FileInfo(reader.SegmentPath(id)).Length; } catch (IOException) { }
        }
        Interlocked.Exchange(ref _sealedBytes, total);
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private SegmentWriter OpenNewSegment()
    {
        var id = AllocateSegmentId();
        var path = StoreLayout.SegmentPath(_root, id);
        var w = new SegmentWriter(path, id, _options.Segment, publishAfterHeader: true);
        _currentOpenedAt = UnixTime.NowUnixNanos();
        return w;
    }

    /// <summary>Seals the active segment (footer + fsync) and waits for it. Next write opens a new segment.</summary>
    public void SealCurrent() => CloseCurrent(wait: true);

    /// <summary>
    /// Closes the active segment for writing. With background durability the seal runs on the durability thread and
    /// this returns at once unless <paramref name="wait"/> is set; the next write opens a new segment either way.
    /// </summary>
    private void CloseCurrent(bool wait)
    {
        if (_current is null)
        {
            if (wait) _durability?.WaitIdle();
            return;
        }
        var seg = _current;
        _current = null;
        if (seg.RecordCount == 0)
        {
            _durability?.WaitIdle(); // no queued fsync may still reference this handle
            seg.Dispose();
            File.Delete(seg.Path);
            return;
        }
        SegmentsSealed++;
        if (_durability is not null)
        {
            _durability.EnqueueSeal(seg, seg.CurrentIndex.LastSequence, OnSealed);
            if (wait) _durability.WaitIdle();
            return;
        }
        var t0 = Stopwatch.GetTimestamp();
        var index = seg.Seal();
        seg.Dispose();
        Slow($"seal segment {seg.SegmentId} (footer + fsync + close)", t0);
        OnSealed(seg, index);
        Volatile.Write(ref _syncDurable, Math.Max(_syncDurable, index.LastSequence));
    }

    /// <summary>Bookkeeping after a segment was sealed (writer thread or durability thread).</summary>
    private void OnSealed(SegmentWriter seg, SegmentIndex index)
    {
        Interlocked.Add(ref _sealedBytes, seg.Length);
        var t0 = Stopwatch.GetTimestamp();
        Manifest.Append(_root, new JsonObject
        {
            ["op"] = "segment_sealed",
            ["id"] = seg.SegmentId,
            ["records"] = index.RecordCount,
            ["bytes"] = seg.Length,
            ["first_seq"] = index.FirstSequence,
            ["last_seq"] = index.LastSequence,
            ["codec"] = _options.Segment.Codec.ToString(),
        });
        Slow("manifest append (fsync)", t0);
    }

    private void AbandonCurrent()
    {
        var seg = _current;
        _current = null;
        if (seg is null) return;
        _durability?.WaitIdle(); // a queued fsync must not race with closing and recovering this file
        try
        {
            seg.Dispose();
            // Best effort: make the segment immutable with its valid prefix.
            RecoverSegment(_root, seg.Path, seg.SegmentId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left unsealed; next open runs recovery again.
        }
    }

    public AckLevel Flush(AckLevel level)
    {
        if (_current is null)
        {
            if (level < AckLevel.FsyncConfirmed) return AckLevel.WrittenToOs;
            _durability?.WaitIdle();
            if (_durability?.Failure is { } failure) throw new IOException("background durability failed: " + failure);
            return AckLevel.FsyncConfirmed;
        }
        try
        {
            if (level >= AckLevel.FsyncConfirmed)
            {
                var t0 = Stopwatch.GetTimestamp();
                _durability?.WaitIdle(); // rotated segments must be sealed before claiming durability
                if (_durability?.Failure is { } failure) throw new IOException("background durability failed: " + failure);
                _current.Fsync();
                Volatile.Write(ref _syncDurable, _lastSequence);
                Slow("fsync", t0);
                return AckLevel.FsyncConfirmed;
            }
            _current.FlushToOs();
            return AckLevel.WrittenToOs;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AbandonCurrent();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            SealCurrent();
            _durability?.WaitIdle();
        }
        finally
        {
            _durability?.Dispose();
            _lock.Dispose();
        }
    }
}

/// <summary>Read-only view over a store directory. Can run concurrently with a writer (other process or thread).</summary>
public sealed class StoreReader
{
    public string Root { get; }
    public StoreInfo Info { get; }
    /// <summary>Live segments ordered by first sequence (unsealed segments last, by id).</summary>
    public IReadOnlyList<ulong> SegmentIds { get; }
    public IReadOnlyList<string> ManifestProblems { get; }
    /// <summary>Footer index per segment (null when unsealed or footer unreadable).</summary>
    public IReadOnlyDictionary<ulong, SegmentIndex?> Indexes { get; }

    private StoreReader(string root, StoreInfo info, IReadOnlyList<ulong> ids, IReadOnlyList<string> problems, IReadOnlyDictionary<ulong, SegmentIndex?> indexes)
    {
        Root = root; Info = info; SegmentIds = ids; ManifestProblems = problems; Indexes = indexes;
    }

    public static StoreReader Open(string root)
    {
        if (!StoreLayout.IsStore(root)) throw new DirectoryNotFoundException($"'{root}' is not a LatticeLog store (missing {StoreLayout.StoreFile}).");
        var info = StoreWriter.ReadStoreFile(root);
        var manifest = Manifest.Load(root);
        var retired = manifest.RetiredSegments();
        var ids = new List<ulong>();
        var segDir = Path.Combine(root, StoreLayout.SegmentsDir);
        if (Directory.Exists(segDir))
        {
            foreach (var file in Directory.EnumerateFiles(segDir, "seg-*.llog"))
                if (StoreLayout.TryParseSegmentId(file, out var id) && !retired.Contains(id)) ids.Add(id);
        }
        var indexes = new Dictionary<ulong, SegmentIndex?>();
        foreach (var id in ids)
        {
            try
            {
                using var r = SegmentReader.Open(StoreLayout.SegmentPath(root, id));
                indexes[id] = r.Index;
            }
            catch (Exception ex) when (ex is LlogFormatException or IOException) { indexes[id] = null; }
        }
        ids.Sort((a, b) =>
        {
            var ia = indexes[a]; var ib = indexes[b];
            var ka = ia is { HasRecords: true } ? ia.FirstSequence : ulong.MaxValue;
            var kb = ib is { HasRecords: true } ? ib.FirstSequence : ulong.MaxValue;
            var c = ka.CompareTo(kb);
            return c != 0 ? c : a.CompareTo(b);
        });
        return new StoreReader(root, info, ids, manifest.Problems, indexes);
    }

    public string SegmentPath(ulong id) => StoreLayout.SegmentPath(Root, id);

    /// <summary>Opens every segment in id order. Unreadable segments are reported, not hidden.</summary>
    public IEnumerable<SegmentReader> OpenSegments(ScanReport report, Func<ulong, SegmentIndex?, bool>? segmentFilter = null)
    {
        foreach (var id in SegmentIds)
        {
            var path = SegmentPath(id);
            SegmentReader? reader = null;
            try { reader = SegmentReader.Open(path); }
            catch (LlogFormatException ex) { report.Add(new ScanIssue(IssueKind.UnsupportedFile, path, 0, 0, ex.Message)); }
            catch (FileNotFoundException) { continue; /* deleted concurrently by retention/compaction */ }
            catch (IOException ex) { report.Add(new ScanIssue(IssueKind.Io, path, 0, 0, ex.Message)); }
            if (reader is null) continue;
            using (reader)
            {
                if (reader.FooterError is not null) report.Add(new ScanIssue(IssueKind.CorruptFooter, path, 0, 0, "Footer unreadable, falling back to scan: " + reader.FooterError));
                if (segmentFilter is not null && !segmentFilter(id, reader.Index)) { report.SegmentsSkippedByIndex++; continue; }
                report.SegmentsRead++;
                yield return reader;
            }
        }
    }

    public IEnumerable<DecodedBlock> ReadBlocks(ScanReport report, Func<ulong, SegmentIndex?, bool>? segmentFilter = null, Func<BlockIndexEntry, bool>? blockFilter = null)
    {
        foreach (var seg in OpenSegments(report, segmentFilter))
            foreach (var b in seg.ReadBlocks(report, blockFilter))
                yield return b;
    }

    public IEnumerable<LogRecord> ReadAll(ScanReport report)
    {
        foreach (var b in ReadBlocks(report))
            foreach (var r in b.Records)
                yield return r;
    }
}
