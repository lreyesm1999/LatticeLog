using System.Diagnostics;
using System.Text.Json.Nodes;
using LatticeLog.Storage.Codecs;
using LatticeLog.Storage.Format;

namespace LatticeLog.Storage;

public enum CompactionPolicy
{
    /// <summary>Prefer low CPU (structural, no general-purpose compression).</summary>
    Fast,
    /// <summary>Weighted bytes vs CPU.</summary>
    Balanced,
    /// <summary>Smallest bytes regardless of CPU.</summary>
    Compact,
}

public sealed record CodecEstimate(CodecId Codec, Compression Compression, long Bytes, double EncodeMicros, double DecodeMicros, double Cost);

/// <summary>
/// Chooses a codec per segment from a sample, with an explicit cost function (P10-T01..T05):
/// cost = bytes + wCpu * (encode_us + decode_us * wRead). Weights depend on the policy.
/// </summary>
public static class CodecPlanner
{
    public static readonly (CodecId, Compression)[] Candidates =
    [
        (CodecId.Raw, Compression.None),
        (CodecId.Structural, Compression.None),
        (CodecId.Contextual, Compression.None),
        (CodecId.Raw, Compression.Deflate),
        (CodecId.Structural, Compression.Deflate),
        (CodecId.Contextual, Compression.Deflate),
    ];

    public static (double BytesWeight, double CpuWeightPerMicro, double ReadWeight) Weights(CompactionPolicy p) => p switch
    {
        CompactionPolicy.Fast => (1.0, 50.0, 2.0),
        CompactionPolicy.Balanced => (1.0, 2.0, 1.0),
        CompactionPolicy.Compact => (1.0, 0.0, 0.0),
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };

    public static IReadOnlyList<CodecEstimate> Estimate(IReadOnlyList<LogRecord> sample, CompactionPolicy policy)
    {
        var (wb, wc, wr) = Weights(policy);
        var results = new List<CodecEstimate>();
        foreach (var (codec, comp) in Candidates)
        {
            var tables = new SegmentTables();
            var w = new ByteWriter();
            var (ns, nc) = tables.CollectNew(sample, 0, sample.Count);
            var meta = new ByteWriter();
            tables.EncodeSchemaBlock(ns, meta);
            tables.EncodeContextBlock(nc, meta);
            var sw = Stopwatch.StartNew();
            RecordCodec.Encode(codec, sample, 0, sample.Count, tables, w);
            long bytes = w.Length;
            if (comp == Compression.Deflate)
            {
                using var ms = new MemoryStream();
                using (var ds = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionLevel.Fastest, true)) ds.Write(w.WrittenSpan);
                bytes = Math.Min(bytes, ms.Length);
            }
            var enc = sw.Elapsed.TotalMicroseconds;
            sw.Restart();
            var list = new List<LogRecord>(sample.Count);
            // Decoding cost measured on the uncompressed payload plus a fixed inflate factor when compressed.
            RecordCodec.Decode(codec, w.WrittenSpan, sample.Count, tables, list);
            var dec = sw.Elapsed.TotalMicroseconds * (comp == Compression.Deflate ? 1.3 : 1.0);
            results.Add(new CodecEstimate(codec, comp, bytes + meta.Length, enc, dec, wb * (bytes + meta.Length) + wc * (enc + dec * wr)));
            w.Release();
            meta.Release();
        }
        return results;
    }

    public static CodecEstimate Choose(IReadOnlyList<LogRecord> sample, CompactionPolicy policy)
        => Estimate(sample, policy).OrderBy(e => e.Cost).ThenBy(e => e.Codec).First();
}

public sealed record CompactionResult(IReadOnlyList<ulong> Inputs, ulong Output, long InputBytes, long OutputBytes, long Records, CodecEstimate Choice, TimeSpan Elapsed)
{
    /// <summary>Records removed by an erase filter (0 for plain compaction).</summary>
    public long Erased { get; init; }
}

/// <summary>
/// Rewrites sealed segments into a new segment with the planned codec (P10-T08..T11).
/// Protocol: journal "compaction_started" → write .tmp → fsync → full verification (record equality) →
/// rename → journal "compaction_committed" (atomic visibility switch) → delete inputs.
/// Event references are sequence based and therefore stable.
/// </summary>
public static class Compactor
{
    /// <param name="allocateId">Segment id allocator: pass <see cref="StoreWriter.AllocateSegmentId"/> when a writer is active in-process; otherwise the caller must hold the store lock.</param>
    /// <param name="erase">Optional filter (P18 erasure): matching records are left out of the output. The output is
    /// verified against the kept records and the inputs are retired exactly as in a plain compaction.</param>
    public static CompactionResult? Compact(string root, IReadOnlyList<ulong> inputs, CompactionPolicy policy, Func<ulong> allocateId, Func<LogRecord, bool>? erase = null, CancellationToken ct = default)
    {
        if (inputs.Count == 0) return null;
        var sw = Stopwatch.StartNew();
        // Load all records of the inputs (sealed only), verifying cleanliness first: never compact damaged data silently.
        var records = new List<LogRecord>();
        long inputBytes = 0;
        foreach (var id in inputs)
        {
            ct.ThrowIfCancellationRequested();
            var path = StoreLayout.SegmentPath(root, id);
            using var reader = SegmentReader.Open(path);
            if (!reader.IsSealed) throw new InvalidOperationException($"Segment {id} is not sealed; only sealed segments are compacted.");
            var report = new ScanReport();
            foreach (var b in reader.ReadBlocks(report)) records.AddRange(b.Records);
            if (!report.IsClean) throw new InvalidOperationException($"Segment {id} has integrity issues; run 'verify'/'repair' first. First issue: {report.Issues[0].Message}");
            inputBytes += reader.Length;
        }
        var erased = 0L;
        if (erase is not null)
        {
            var before = records.Count;
            records = records.Where(r => !erase(r)).ToList();
            erased = before - records.Count;
        }
        var sample = records.Take(Math.Min(records.Count, 4096)).ToList();
        var choice = sample.Count > 0 ? CodecPlanner.Choose(sample, policy) : new CodecEstimate(CodecId.Structural, Compression.None, 0, 0, 0, 0);
        var output = allocateId();
        Manifest.Append(root, new JsonObject
        {
            ["op"] = "compaction_started",
            ["inputs"] = new JsonArray(inputs.Select(i => (JsonNode)i).ToArray()),
            ["output"] = output,
            ["codec"] = choice.Codec.ToString(),
            ["compression"] = choice.Compression.ToString(),
            ["policy"] = policy.ToString(),
        });
        var tmp = StoreLayout.TempSegmentPath(root, output);
        var final = StoreLayout.SegmentPath(root, output);
        try
        {
            // Copies, so the planner/verification can compare against the originals.
            var copies = records.Select(r => r.ShallowClone()).ToList();
            using (var w = new SegmentWriter(tmp, output, new SegmentWriterOptions { Codec = choice.Codec, Compression = choice.Compression }))
            {
                for (var i = 0; i < copies.Count; i += 4096)
                {
                    ct.ThrowIfCancellationRequested();
                    w.Append(copies, i, Math.Min(4096, copies.Count - i));
                }
                w.Seal();
            }
            // Verify: decode the new file and compare every record with the source.
            var verify = new ScanReport();
            var idx = 0;
            using (var reader = SegmentReader.Open(tmp))
            {
                foreach (var b in reader.ReadBlocks(verify))
                {
                    foreach (var r in b.Records)
                    {
                        if (idx >= records.Count || !r.Equals(records[idx])) throw new InvalidOperationException($"Compaction verification failed at record {idx}.");
                        idx++;
                    }
                }
            }
            if (!verify.IsClean || idx != records.Count) throw new InvalidOperationException("Compaction verification failed: output incomplete.");
            File.Move(tmp, final);
            Manifest.Append(root, new JsonObject
            {
                ["op"] = "compaction_committed",
                ["inputs"] = new JsonArray(inputs.Select(i => (JsonNode)i).ToArray()),
                ["output"] = output,
                ["records"] = records.Count,
                ["erased"] = erased,
            });
        }
        catch
        {
            TryDelete(tmp);
            TryDelete(final);
            Manifest.Append(root, new JsonObject { ["op"] = "compaction_aborted", ["output"] = output });
            throw;
        }
        foreach (var id in inputs) TryDelete(StoreLayout.SegmentPath(root, id));
        var outBytes = new FileInfo(final).Length;
        return new CompactionResult(inputs, output, inputBytes, outBytes, records.Count, choice, sw.Elapsed) { Erased = erased };
    }

    /// <summary>
    /// Erasure (P18): rewrites, one by one, the sealed segments below <paramref name="excludeAtOrAbove"/> that contain
    /// at least one record matching <paramref name="erase"/>. Segments without matches are not touched.
    /// </summary>
    public static List<CompactionResult> EraseMatching(string root, Func<LogRecord, bool> erase, CompactionPolicy policy, Func<ulong> allocateId, ulong excludeAtOrAbove, CancellationToken ct = default)
    {
        var store = StoreReader.Open(root);
        var results = new List<CompactionResult>();
        foreach (var id in store.SegmentIds)
        {
            ct.ThrowIfCancellationRequested();
            if (id >= excludeAtOrAbove || store.Indexes.GetValueOrDefault(id) is not { HasRecords: true }) continue;
            bool any;
            using (var reader = SegmentReader.Open(store.SegmentPath(id)))
                any = reader.ReadBlocks(new ScanReport()).Any(b => b.Records.Any(erase));
            if (any && Compact(root, [id], policy, allocateId, erase, ct) is { } r) results.Add(r);
        }
        return results;
    }

    /// <summary>Counts records matching <paramref name="erase"/> in sealed segments below the limit (dry run).</summary>
    public static long CountMatching(string root, Func<LogRecord, bool> erase, ulong excludeAtOrAbove)
    {
        var store = StoreReader.Open(root);
        long n = 0;
        foreach (var b in store.ReadBlocks(new ScanReport(), (id, _) => id < excludeAtOrAbove))
            foreach (var r in b.Records) if (erase(r)) n++;
        return n;
    }

    /// <summary>Groups consecutive sealed segments up to <paramref name="targetBytes"/> and compacts each group.</summary>
    public static List<CompactionResult> CompactAll(string root, CompactionPolicy policy, Func<ulong> allocateId, long targetBytes = 256L * 1024 * 1024, ulong? excludeAtOrAbove = null, CancellationToken ct = default)
    {
        var store = StoreReader.Open(root);
        var groups = new List<List<ulong>>();
        var current = new List<ulong>();
        long size = 0;
        foreach (var id in store.SegmentIds)
        {
            if (excludeAtOrAbove is { } ex && id >= ex) continue;
            if (store.Indexes.GetValueOrDefault(id) is not { HasRecords: true }) continue;
            var len = new FileInfo(store.SegmentPath(id)).Length;
            if (size + len > targetBytes && current.Count > 0) { groups.Add(current); current = []; size = 0; }
            current.Add(id);
            size += len;
        }
        if (current.Count > 0) groups.Add(current);
        var results = new List<CompactionResult>();
        foreach (var g in groups)
        {
            ct.ThrowIfCancellationRequested();
            if (Compact(root, g, policy, allocateId, ct: ct) is { } r) results.Add(r);
        }
        return results;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* best effort: a leftover temporary file is removed by recovery */ }
        catch (UnauthorizedAccessException) { /* best effort: a leftover temporary file is removed by recovery */ }
    }
}

/// <summary>Completes or rolls back interrupted compactions on startup (idempotent, P10-T10).</summary>
public static class CompactionRecovery
{
    public static void Complete(string root)
    {
        var manifest = Manifest.Load(root);
        var started = new Dictionary<ulong, JsonObject>();
        var committed = new List<JsonObject>();
        var finished = new HashSet<ulong>();
        foreach (var e in manifest.Entries)
        {
            switch ((string?)e["op"])
            {
                case "compaction_started": started[(ulong)e["output"]!] = e; break;
                case "compaction_committed": committed.Add(e); finished.Add((ulong)e["output"]!); break;
                case "compaction_aborted": finished.Add((ulong)e["output"]!); break;
            }
        }
        foreach (var (output, _) in started)
        {
            if (finished.Contains(output)) continue;
            // Never committed: discard output, inputs stay authoritative.
            Delete(StoreLayout.TempSegmentPath(root, output));
            Delete(StoreLayout.SegmentPath(root, output));
            Manifest.Append(root, new JsonObject { ["op"] = "compaction_aborted", ["output"] = output, ["reason"] = "interrupted before commit" });
        }
        foreach (var c in committed)
        {
            // Committed: make sure inputs are gone (deletion may have been interrupted).
            if (c["inputs"] is JsonArray inputs)
                foreach (var i in inputs) Delete(StoreLayout.SegmentPath(root, (ulong)i!));
        }
        var segDir = Path.Combine(root, StoreLayout.SegmentsDir);
        if (Directory.Exists(segDir))
            foreach (var tmp in Directory.EnumerateFiles(segDir, "*.llog.tmp")) Delete(tmp);
    }

    private static void Delete(string p)
    {
        if (File.Exists(p)) File.Delete(p);
    }
}
