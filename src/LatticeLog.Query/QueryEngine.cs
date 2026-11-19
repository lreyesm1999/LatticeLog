using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LatticeLog.Storage;
using LatticeLog.Storage.Format;

namespace LatticeLog.Query;

public enum SortOrder { Ascending, Descending }

public sealed record QueryRequest
{
    public string? Where { get; init; }
    public long? SinceUnixNanos { get; init; }
    public long? UntilUnixNanos { get; init; }
    public int Limit { get; init; } = 100;
    public string? Cursor { get; init; }
    public SortOrder Order { get; init; } = SortOrder.Ascending;
    /// <summary>Stop after decoding this many records (resource limit, P11-T13). Reported as partial.</summary>
    public long MaxScannedRecords { get; init; } = 50_000_000;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>Disable index pruning (full-scan oracle for correctness tests, P11-T18).</summary>
    public bool UseIndexes { get; init; } = true;

    public const int MaxLimit = 10_000;
}

public sealed record QueryStats
{
    public int SegmentsTotal { get; set; }
    public int SegmentsScanned { get; set; }
    public Dictionary<string, int> SegmentsPruned { get; } = [];
    public long BlocksScanned { get; set; }
    public long BlocksPruned { get; set; }
    public long RecordsDecoded { get; set; }
    public long RecordsMatched { get; set; }
    public long BytesRead { get; set; }
    public double ElapsedMs { get; set; }
}

public sealed record QueryHit(LogRecord Record, ulong SegmentId, long BlockOffset);

/// <summary>Result with explicit coverage: callers can always tell whether the answer is complete (P11-T17).</summary>
public sealed record QueryResult
{
    public required string StoreId { get; init; }
    public required string Query { get; init; }
    public required List<QueryHit> Hits { get; init; }
    public string? NextCursor { get; init; }
    /// <summary>True when more matches exist beyond Limit.</summary>
    public bool Truncated { get; init; }
    /// <summary>True when the scan stopped early (timeout, scan limit, cancellation) — results may miss matches.</summary>
    public bool Partial { get; init; }
    public string? PartialReason { get; init; }
    public required IReadOnlyList<ScanIssue> Issues { get; init; }
    public required QueryStats Stats { get; init; }
    public required string Plan { get; init; }
    /// <summary>The active (unsealed) segment was read as of the scan: newer records may exist (near-real-time).</summary>
    public bool IncludesUnsealedSegments { get; init; }
}

/// <summary>Opaque, versioned pagination cursor bound to the store and the query text (P11-T12).</summary>
public static class Cursor
{
    public static string Encode(string storeId, string queryKey, ulong lastSequence, SortOrder order)
    {
        var json = JsonSerializer.Serialize(new CursorData(1, storeId, Hash(queryKey), lastSequence, order == SortOrder.Descending), CursorJson.Default.CursorData);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static ulong Decode(string cursor, string storeId, string queryKey, SortOrder order)
    {
        try
        {
            var b64 = cursor.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            var d = JsonSerializer.Deserialize(Encoding.UTF8.GetString(Convert.FromBase64String(b64)), CursorJson.Default.CursorData)!;
            if (d.V != 1) throw new ArgumentException("Unsupported cursor version.");
            if (d.S != storeId) throw new ArgumentException("Cursor belongs to another store.");
            if (d.Q != Hash(queryKey)) throw new ArgumentException("Cursor was issued for a different query.");
            if (d.D != (order == SortOrder.Descending)) throw new ArgumentException("Cursor was issued for a different sort order.");
            return d.L;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or NullReferenceException)
        {
            throw new ArgumentException("Invalid cursor.", ex);
        }
    }

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16];

    internal sealed record CursorData(int V, string S, string Q, ulong L, bool D);
}

/// <summary>
/// Query execution over a local store (P11): parse → extract pruning constraints → prune segments and blocks with
/// footer indexes → decode only surviving blocks → evaluate → paginate. Read-only; isolated from the writer.
/// </summary>
public sealed class QueryEngine(StoreReader store)
{
    public StoreReader Store { get; } = store;

    public static QueryEngine Open(string root) => new(StoreReader.Open(root));

    public Expr BuildExpression(QueryRequest req)
    {
        var e = QueryParser.Parse(req.Where);
        if (req.SinceUnixNanos is { } s) e = new AndExpr(new CompareExpr(new FieldRef(BuiltinField.Time), CompareOp.Ge, new Literal(s, s.ToString(System.Globalization.CultureInfo.InvariantCulture))), e);
        if (req.UntilUnixNanos is { } u) e = new AndExpr(new CompareExpr(new FieldRef(BuiltinField.Time), CompareOp.Le, new Literal(u, u.ToString(System.Globalization.CultureInfo.InvariantCulture))), e);
        return e;
    }

    public QueryResult Execute(QueryRequest req, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var limit = Math.Clamp(req.Limit, 1, QueryRequest.MaxLimit);
        var expr = BuildExpression(req);
        var queryKey = expr.ToString();
        var match = Evaluator.Compile(expr);
        var constraints = req.UseIndexes ? PruningConstraints.From(expr) : new PruningConstraints();
        ulong? cursorSeq = req.Cursor is null ? null : Cursor.Decode(req.Cursor, Store.Info.StoreId, queryKey, req.Order);
        if (cursorSeq is { } cs)
        {
            if (req.Order == SortOrder.Ascending) constraints.AfterSequence = cs; else constraints.BeforeSequence = cs;
        }
        var stats = new QueryStats { SegmentsTotal = Store.SegmentIds.Count };
        var report = new ScanReport();
        var hits = new List<QueryHit>();
        var truncated = false;
        string? partialReason = null;
        var unsealed = false;
        var deadline = sw.Elapsed + req.Timeout;

        var segIds = req.Order == SortOrder.Ascending ? Store.SegmentIds : Store.SegmentIds.Reverse().ToList();
        foreach (var id in segIds)
        {
            if (truncated || partialReason is not null) break;
            var idx = Store.Indexes.GetValueOrDefault(id);
            var reason = constraints.ExcludeSegment(idx);
            if (reason is not null)
            {
                stats.SegmentsPruned[reason] = stats.SegmentsPruned.GetValueOrDefault(reason) + 1;
                continue;
            }
            SegmentReader reader;
            try { reader = SegmentReader.Open(Store.SegmentPath(id)); }
            catch (FileNotFoundException)
            {
                // Removed by retention/compaction after this query's snapshot: say so instead of silently missing data.
                report.Add(new ScanIssue(IssueKind.Io, Store.SegmentPath(id), 0, 0, "segment removed by retention/compaction during the query"));
                continue;
            }
            catch (Exception ex) when (ex is LlogFormatException or IOException)
            {
                report.Add(new ScanIssue(IssueKind.UnsupportedFile, Store.SegmentPath(id), 0, 0, ex.Message));
                continue;
            }
            using (reader)
            {
                stats.SegmentsScanned++;
                if (!reader.IsSealed) unsealed = true;
                foreach (var block in Blocks(reader, constraints, req.Order, report, stats))
                {
                    ct.ThrowIfCancellationRequested();
                    stats.BlocksScanned++;
                    stats.RecordsDecoded += block.Records.Count;
                    IEnumerable<LogRecord> recs = req.Order == SortOrder.Ascending ? block.Records : Enumerable.Reverse(block.Records);
                    foreach (var r in recs)
                    {
                        if (cursorSeq is { } c && (req.Order == SortOrder.Ascending ? r.Sequence <= c : r.Sequence >= c)) continue;
                        if (!match(r)) continue;
                        stats.RecordsMatched++;
                        if (hits.Count == limit) { truncated = true; break; }
                        hits.Add(new QueryHit(r, block.SegmentId, block.Entry.Offset));
                    }
                    if (truncated) break;
                    if (stats.RecordsDecoded >= req.MaxScannedRecords) { partialReason = $"scan limit of {req.MaxScannedRecords} records reached"; break; }
                    if (sw.Elapsed > deadline) { partialReason = $"timeout of {req.Timeout.TotalSeconds:0.#}s reached"; break; }
                }
            }
        }
        stats.BytesRead = report.BytesRead;
        stats.ElapsedMs = sw.Elapsed.TotalMilliseconds;
        string? next = null;
        if ((truncated || partialReason is not null) && hits.Count > 0)
            next = Cursor.Encode(Store.Info.StoreId, queryKey, hits[^1].Record.Sequence, req.Order);
        return new QueryResult
        {
            StoreId = Store.Info.StoreId,
            Query = queryKey,
            Hits = hits,
            NextCursor = next,
            Truncated = truncated,
            Partial = partialReason is not null || !report.IsClean,
            PartialReason = partialReason ?? (report.IsClean ? null : $"{report.Issues.Count} integrity issue(s) while scanning; affected data was skipped"),
            Issues = report.Issues,
            Stats = stats,
            Plan = Explain(expr, constraints, req),
            IncludesUnsealedSegments = unsealed,
        };
    }

    private static IEnumerable<DecodedBlock> Blocks(SegmentReader reader, PruningConstraints c, SortOrder order, ScanReport report, QueryStats stats)
    {
        if (reader.Index is { } idx && (order == SortOrder.Descending || idx.Blocks.Any(b => b.Type == BlockType.Records && c.ExcludeBlock(b) is not null)))
        {
            // Random access only pays off when some blocks can be skipped (or for reverse order);
            // otherwise the sequential path below is cheaper (measured in reports/baseline-v1.md).
            var blocks = idx.Blocks.Where(b => b.Type == BlockType.Records);
            if (order == SortOrder.Descending) blocks = blocks.Reverse();
            foreach (var b in blocks)
            {
                if (c.ExcludeBlock(b) is not null) { stats.BlocksPruned++; continue; }
                DecodedBlock? decoded = null;
                try
                {
                    decoded = reader.ReadBlockAt(b);
                    report.BytesRead += b.Length;
                }
                catch (Exception ex) when (ex is LlogFormatException or InvalidDataException or IOException)
                {
                    report.Add(new ScanIssue(IssueKind.UndecodableBlock, reader.Path, b.Offset, b.Length, ex.Message, b.RecordCount));
                }
                if (decoded is not null) yield return decoded;
            }
            yield break;
        }
        // Unsealed segment (no footer index) or nothing to prune: sequential scan.
        var all = reader.ReadBlocks(report);
        if (order == SortOrder.Descending) all = all.Reverse();
        foreach (var b in all)
        {
            if (c.ExcludeBlock(b.Entry) is not null) { stats.BlocksPruned++; continue; }
            yield return b;
        }
    }

    public static string Explain(Expr expr, PruningConstraints c, QueryRequest req)
    {
        var sb = new StringBuilder();
        sb.Append("filter: ").Append(expr).Append('\n');
        sb.Append("order: ").Append(req.Order).Append(", limit: ").Append(Math.Clamp(req.Limit, 1, QueryRequest.MaxLimit)).Append('\n');
        if (!req.UseIndexes) { sb.Append("indexes: disabled (full scan)\n"); return sb.ToString(); }
        sb.Append("pruning:");
        var any = false;
        if (c.MinTime != long.MinValue || c.MaxTime != long.MaxValue)
        {
            sb.Append(" time[").Append(c.MinTime == long.MinValue ? "-inf" : UnixTime.FormatRfc3339(c.MinTime)).Append(", ")
              .Append(c.MaxTime == long.MaxValue ? "+inf" : UnixTime.FormatRfc3339(c.MaxTime)).Append(']');
            any = true;
        }
        if (c.MinSeverity is { } s) { sb.Append(" severity>=").Append(s.ToText()); any = true; }
        if (c.Events is { } e) { sb.Append(" events{").Append(string.Join(",", e)).Append('}'); any = true; }
        if (c.Services is { } sv) { sb.Append(" services{").Append(string.Join(",", sv)).Append('}'); any = true; }
        if (c.BloomKeys.Count > 0) { sb.Append(" bloom{").Append(string.Join(",", c.BloomKeys)).Append('}'); any = true; }
        if (c.AfterSequence is { } a) { sb.Append(" seq>").Append(a); any = true; }
        if (c.BeforeSequence is { } b) { sb.Append(" seq<").Append(b); any = true; }
        if (!any) sb.Append(" none (every segment is scanned)");
        sb.Append('\n');
        return sb.ToString();
    }

    // ------------------------------------------------------------------ lookups

    /// <summary>Resolves a stable reference (sequence) to its record. Works across compactions (P12-T02).</summary>
    public QueryHit? GetBySequence(ulong sequence, ScanReport? report = null)
    {
        report ??= new ScanReport();
        foreach (var id in Store.SegmentIds)
        {
            var idx = Store.Indexes.GetValueOrDefault(id);
            if (idx is not null && (!idx.HasRecords || sequence < idx.FirstSequence || sequence > idx.LastSequence)) continue;
            SegmentReader reader;
            try { reader = SegmentReader.Open(Store.SegmentPath(id)); }
            catch (FileNotFoundException) { continue; } // deleted by retention/compaction after the snapshot
            catch (Exception ex) when (ex is LlogFormatException or IOException) { report.Add(new ScanIssue(IssueKind.UnsupportedFile, Store.SegmentPath(id), 0, 0, ex.Message)); continue; }
            using var _ = reader;
            if (reader.Index is { } x)
            {
                var b = x.Blocks.FirstOrDefault(b => b.Type == BlockType.Records && b.FirstSequence <= sequence && b.LastSequence >= sequence);
                if (b is null) continue;
                var block = reader.ReadBlockAt(b);
                var r = block.Records.FirstOrDefault(r => r.Sequence == sequence);
                if (r is not null) return new QueryHit(r, id, b.Offset);
            }
            else
            {
                foreach (var block in reader.ReadBlocks(report))
                {
                    var r = block.Records.FirstOrDefault(r => r.Sequence == sequence);
                    if (r is not null) return new QueryHit(r, id, block.Entry.Offset);
                }
            }
        }
        return null;
    }

    public QueryResult Trace(TraceId traceId, int limit = 5000)
        => Execute(new QueryRequest { Where = $"trace_id = '{traceId}'", Limit = limit });

    // ------------------------------------------------------------------ aggregations (P11-T15)

    public sealed record AggregateResult(string GroupBy, List<(string Key, long Count)> Groups, long Total, bool UsedIndexOnly, bool Partial, IReadOnlyList<ScanIssue> Issues, QueryStats Stats);

    /// <summary>
    /// Count records grouped by event, level, service, an attribute or time buckets. When there is no filter beyond
    /// time ranges covering whole segments, counts come from footer indexes without decoding any block.
    /// </summary>
    public AggregateResult Aggregate(QueryRequest req, string groupBy, TimeSpan? bucket = null, int top = 50, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var expr = BuildExpression(req);
        var constraints = PruningConstraints.From(expr);
        var onlyTime = IsOnlyTime(expr);
        var gb = groupBy.ToLowerInvariant();
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        var stats = new QueryStats { SegmentsTotal = Store.SegmentIds.Count };
        var report = new ScanReport();
        var usedIndexOnly = true;
        var match = Evaluator.Compile(expr);
        FieldRef? field = gb is "event" or "level" or "severity" or "service" or "time" ? null : QueryParser.ResolveField(groupBy);
        foreach (var id in Store.SegmentIds)
        {
            ct.ThrowIfCancellationRequested();
            var idx = Store.Indexes.GetValueOrDefault(id);
            var reason = constraints.ExcludeSegment(idx);
            if (reason is not null) { stats.SegmentsPruned[reason] = stats.SegmentsPruned.GetValueOrDefault(reason) + 1; continue; }
            var covered = idx is not null && idx.MinTimestamp >= constraints.MinTime && idx.MaxTimestamp <= constraints.MaxTime;
            if (onlyTime && covered && bucket is null && gb is "event" or "level" or "severity")
            {
                if (gb == "event") foreach (var e in idx!.Events) Add(counts, e.Name.Length == 0 ? "(none)" : e.Name, e.Count);
                else for (var s = 0; s < idx!.SeverityCounts.Length; s++) if (idx.SeverityCounts[s] > 0) Add(counts, ((Severity)s).ToText(), idx.SeverityCounts[s]);
                continue;
            }
            usedIndexOnly = false;
            SegmentReader reader;
            try { reader = SegmentReader.Open(Store.SegmentPath(id)); }
            catch (FileNotFoundException) { report.Add(new ScanIssue(IssueKind.Io, Store.SegmentPath(id), 0, 0, "segment removed by retention/compaction during the query")); continue; }
            catch (Exception ex) when (ex is LlogFormatException or IOException) { report.Add(new ScanIssue(IssueKind.UnsupportedFile, Store.SegmentPath(id), 0, 0, ex.Message)); continue; }
            using var _ = reader;
            stats.SegmentsScanned++;
            foreach (var block in Blocks(reader, constraints, SortOrder.Ascending, report, stats))
            {
                stats.BlocksScanned++;
                stats.RecordsDecoded += block.Records.Count;
                foreach (var r in block.Records)
                {
                    if (!match(r)) continue;
                    stats.RecordsMatched++;
                    string key;
                    if (bucket is { } bk)
                    {
                        var width = bk.Ticks * 100;
                        var start = r.TimestampUnixNanos - (((r.TimestampUnixNanos % width) + width) % width);
                        key = UnixTime.FormatRfc3339(start);
                    }
                    else key = gb switch
                    {
                        "event" => r.EffectiveEventName ?? "(none)",
                        "level" or "severity" => r.Severity.ToText(),
                        "service" => r.ServiceName ?? "(none)",
                        _ => Evaluator.TryGet(r, field!, out var v) ? v.ToString() : "(missing)",
                    };
                    Add(counts, key, 1);
                }
            }
        }
        stats.ElapsedMs = sw.Elapsed.TotalMilliseconds;
        var ordered = bucket is null ? counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal) : counts.OrderBy(kv => kv.Key, StringComparer.Ordinal);
        var groups = ordered.Take(bucket is null ? top : int.MaxValue).Select(kv => (kv.Key, kv.Value)).ToList();
        return new AggregateResult(bucket is null ? groupBy : $"time/{bucket}", groups, counts.Values.Sum(), usedIndexOnly && stats.SegmentsScanned == 0, !report.IsClean, report.Issues, stats);
    }

    private static void Add(Dictionary<string, long> d, string k, long n) => d[k] = d.GetValueOrDefault(k) + n;

    private static bool IsOnlyTime(Expr e) => e switch
    {
        TrueExpr => true,
        AndExpr a => IsOnlyTime(a.Left) && IsOnlyTime(a.Right),
        CompareExpr { Field.Kind: BuiltinField.Time } => true,
        _ => false,
    };
}

/// <summary>Source-generated serializer for cursors (no reflection: trimming/AOT-safe). Same JSON shape as before.</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(Cursor.CursorData))]
internal sealed partial class CursorJson : System.Text.Json.Serialization.JsonSerializerContext;
