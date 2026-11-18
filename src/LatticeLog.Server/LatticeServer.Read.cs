using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using LatticeLog.Evidence;
using LatticeLog.Query;
using LatticeLog.Storage;
using LatticeLog.Storage.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LatticeLog.Server;

/// <summary>Read API (P17-T04), see specs/server-api.md. Every route needs a token with the <c>read</c> scope.</summary>
public sealed partial class LatticeServer
{
    private sealed record ReadContext(TokenInfo Token, string Namespace, QueryEngine Engine, TimeSpan Timeout, IQueryCollection Query);

    private void MapReadApi(WebApplication app)
    {
        app.MapGet("/v1/namespaces", (RequestDelegate)HandleNamespacesAsync);
        Read(app, "/v1/search", SearchAsync);
        Read(app, "/v1/events/{seq}", GetEventAsync);
        Read(app, "/v1/events/{seq}/related", RelatedAsync);
        Read(app, "/v1/traces/{traceId}", TraceAsync);
        Read(app, "/v1/stats", StatsAsync);
        Read(app, "/v1/overview", (ctx, rc, ct) => EvidenceAsync(ctx, rc, e => e.Overview(Scope(rc, limitDefault: 100)), ct));
        Read(app, "/v1/error-groups", (ctx, rc, ct) => EvidenceAsync(ctx, rc, e => e.ErrorGroups(Scope(rc, limitDefault: 100), IntArg(rc, "top", 20, 1, 200)), ct));
        Read(app, "/v1/context", (ctx, rc, ct) => EvidenceAsync(ctx, rc, e => e.ContextPack(Scope(rc, limitDefault: 100), IntArg(rc, "budget_bytes", 32_000, 1_000, 1_000_000)), ct));
        Read(app, "/v1/schema", (ctx, rc, ct) => EvidenceAsync(ctx, rc, e => e.Schema(), ct));
        Read(app, "/v1/export", ExportAsync);
        Read(app, "/v1/catalog", CatalogAsync);
    }

    private void Read(WebApplication app, string pattern, Func<HttpContext, ReadContext, CancellationToken, Task> handler)
        => app.MapGet(pattern, (RequestDelegate)(ctx => RunReadAsync(ctx, handler)));

    /// <summary>
    /// Authorization, namespace check, per-token and global concurrency limits, timeout. Reads run on the thread pool
    /// under their own semaphore; ingestion has dedicated writer threads, so slow queries cannot stall it (P17-T08).
    /// </summary>
    private async Task RunReadAsync(HttpContext ctx, Func<HttpContext, ReadContext, CancellationToken, Task> handler)
    {
        var auth = await AuthorizeAsync(ctx, TokenScopes.Read, ctx.Request.Query["ns"].ToString()).ConfigureAwait(false);
        if (auth is not var (token, ns)) return;
        if (!_namespaces.Exists(ns))
        {
            await ErrorAsync(ctx, 404, "namespace_not_found", $"namespace '{ns}' has no data").ConfigureAwait(false);
            return;
        }
        var running = _queriesPerToken.AddOrUpdate(token.Id, 1, (_, n) => n + 1);
        try
        {
            if (running > _options.Query.PerTokenConcurrentQueries)
            {
                await ErrorAsync(ctx, 429, "too_many_queries", $"token '{token.Id}' already runs {_options.Query.PerTokenConcurrentQueries} queries", TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                return;
            }
            if (!await _querySlots.WaitAsync(_options.Query.QueueWait, ctx.RequestAborted).ConfigureAwait(false))
            {
                await ErrorAsync(ctx, 503, "overloaded", "all query slots are busy", TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                return;
            }
            try
            {
                var timeout = TimeSpan.FromMilliseconds(IntArg(ctx.Request.Query, "timeout_ms", (int)_options.Query.DefaultTimeout.TotalMilliseconds, 100, (int)_options.Query.MaxTimeout.TotalMilliseconds));
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
                cts.CancelAfter(timeout + TimeSpan.FromSeconds(5)); // the engine stops itself at `timeout` and reports partial
                // Snapshot (P17-T09): the segment list is fixed when the reader opens; later writes are not mixed in.
                var engine = QueryEngine.Open(_namespaces.StorePath(ns));
                await handler(ctx, new ReadContext(token, ns, engine, timeout, ctx.Request.Query), cts.Token).ConfigureAwait(false);
                // Filters and status only: never the returned data.
                Audit(ctx, token, ns, "read" + ctx.Request.Path.Value!["/v1".Length..].Replace('/', '.'),
                    $"status={ctx.Response.StatusCode} where={ctx.Request.Query["where"]} since={ctx.Request.Query["since"]} until={ctx.Request.Query["until"]}",
                    ctx.Response.StatusCode < 400 ? "ok" : "error");
            }
            catch (QuerySyntaxException ex) { await ErrorAsync(ctx, 400, "invalid_query", ex.Message).ConfigureAwait(false); }
            catch (ArgumentException ex) { await ErrorAsync(ctx, 400, "invalid_request", ex.Message).ConfigureAwait(false); }
            catch (FormatException ex) { await ErrorAsync(ctx, 400, "invalid_request", ex.Message).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
            {
                await ErrorAsync(ctx, 503, "query_timeout", "the query did not finish in time").ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Unexpected: report it (type and message only, no stack trace to the client) and keep serving.
                Diagnose($"read {ctx.Request.Path} in '{ns}' failed: {ex}");
                if (!ctx.Response.HasStarted)
                    await ErrorAsync(ctx, 500, "internal_error", $"{ex.GetType().Name}: {ex.Message}").ConfigureAwait(false);
            }
            finally { _querySlots.Release(); }
        }
        finally { _queriesPerToken.AddOrUpdate(token.Id, 0, (_, n) => n - 1); }
    }

    // ------------------------------------------------------------------ arguments

    private static int IntArg(IQueryCollection q, string name, int fallback, int min, int max)
    {
        var s = q[name].ToString();
        if (s.Length == 0) return fallback;
        if (!int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v)) throw new ArgumentException($"'{name}' must be an integer");
        return Math.Clamp(v, min, max);
    }

    private static int IntArg(ReadContext rc, string name, int fallback, int min, int max) => IntArg(rc.Query, name, fallback, min, max);

    /// <summary>RFC 3339 or a relative duration back from now (15m, 2h, 7d).</summary>
    private static long? TimeArg(IQueryCollection q, string name, long now)
    {
        var s = q[name].ToString();
        if (s.Length == 0) return null;
        if (UnixTime.TryParseRfc3339(s, out var t)) return t;
        if (s.Length >= 2 && double.TryParse(s[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n >= 0)
        {
            var unit = s[^1] switch { 's' => 1e9, 'm' => 60e9, 'h' => 3600e9, 'd' => 86400e9, _ => -1 };
            if (unit > 0) return now - (long)(n * unit);
        }
        throw new ArgumentException($"'{name}' must be RFC 3339 or a relative duration like 15m, 2h, 7d");
    }

    private QueryRequest Scope(ReadContext rc, int limitDefault)
    {
        var q = rc.Query;
        var now = UnixTime.NowUnixNanos();
        var order = q["order"].ToString() switch
        {
            "" or "asc" => SortOrder.Ascending,
            "desc" => SortOrder.Descending,
            _ => throw new ArgumentException("'order' must be asc or desc"),
        };
        var cursor = q["cursor"].ToString();
        return new QueryRequest
        {
            Where = q["where"].ToString() is { Length: > 0 } w ? w : null,
            SinceUnixNanos = TimeArg(q, "since", now),
            UntilUnixNanos = TimeArg(q, "until", now),
            Limit = IntArg(q, "limit", limitDefault, 1, _options.Query.MaxPageSize),
            Order = order,
            Cursor = cursor.Length == 0 ? null : _cursors.Verify(rc.Namespace, rc.Token.Id, cursor),
            Timeout = rc.Timeout,
            MaxScannedRecords = _options.Query.MaxScannedRecords,
        };
    }

    private static async Task WriteBytesAsync(HttpContext ctx, int status, string contentType, ReadOnlyMemory<byte> body)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = contentType;
        await ctx.Response.Body.WriteAsync(body).ConfigureAwait(false);
    }

    private static void WriteCoverage(Utf8JsonWriter w, QueryResult res, string? signedCursor)
    {
        w.WriteStartObject("coverage");
        w.WriteNumber("returned", res.Hits.Count);
        w.WriteBoolean("truncated", res.Truncated);
        w.WriteBoolean("partial_results", res.Partial);
        if (res.PartialReason is not null) w.WriteString("partial_reason", res.PartialReason);
        w.WriteBoolean("includes_unsealed_segments", res.IncludesUnsealedSegments);
        if (signedCursor is not null) w.WriteString("next_cursor", signedCursor);
        w.WriteNumber("storage_issues", res.Issues.Count);
        w.WriteEndObject();
        w.WriteStartObject("stats");
        w.WriteNumber("segments_total", res.Stats.SegmentsTotal);
        w.WriteNumber("segments_scanned", res.Stats.SegmentsScanned);
        w.WriteNumber("blocks_scanned", res.Stats.BlocksScanned);
        w.WriteNumber("blocks_pruned", res.Stats.BlocksPruned);
        w.WriteNumber("records_decoded", res.Stats.RecordsDecoded);
        w.WriteNumber("elapsed_ms", Math.Round(res.Stats.ElapsedMs, 3));
        w.WriteEndObject();
    }

    // ------------------------------------------------------------------ handlers

    private async Task HandleNamespacesAsync(HttpContext ctx)
    {
        var token = Authenticate(ctx);
        if (token is null || !token.Has(TokenScopes.Read))
        {
            await ErrorAsync(ctx, token is null ? 401 : 403, token is null ? "unauthorized" : "forbidden_scope", "a token with the read scope is required").ConfigureAwait(false);
            return;
        }
        var list = new JsonArray(_namespaces.List().Where(token.AllowsNamespace).Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
        await WriteJsonAsync(ctx, 200, new JsonObject { ["namespaces"] = list }).ConfigureAwait(false);
    }

    private async Task SearchAsync(HttpContext ctx, ReadContext rc, CancellationToken ct)
    {
        var req = Scope(rc, limitDefault: 100);
        var res = await Task.Run(() => rc.Engine.Execute(req, ct), ct).ConfigureAwait(false);
        var signed = res.NextCursor is null ? null : _cursors.Sign(rc.Namespace, rc.Token.Id, res.NextCursor);
        var redact = rc.Query["redact"].ToString() != "false";
        var fo = new FriendlyOptions { Redaction = redact ? RedactionPolicy.Default : RedactionPolicy.None };
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, CanonicalJson.WriterOptions))
        {
            w.WriteStartObject();
            w.WriteString("format", "latticelog.query.v1");
            w.WriteString("namespace", rc.Namespace);
            w.WriteString("store_id", res.StoreId);
            w.WriteString("filter", res.Query);
            WriteCoverage(w, res, signed);
            if (rc.Query["explain"].ToString() == "true") w.WriteString("plan", res.Plan);
            w.WriteStartArray("events");
            foreach (var h in res.Hits) CanonicalJson.WriteFriendlyRecord(w, h.Record, res.StoreId, fo);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        await WriteBytesAsync(ctx, 200, "application/json", ms.GetBuffer().AsMemory(0, (int)ms.Length)).ConfigureAwait(false);
    }

    private static ulong ParseEventRef(string text)
    {
        // Accepts a sequence number or llog://<store_id>/e/<seq>.
        var s = text.StartsWith("llog://", StringComparison.Ordinal) ? text[(text.LastIndexOf('/') + 1)..] : text;
        return ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var seq) && seq > 0
            ? seq : throw new ArgumentException("event reference must be a sequence number or llog://<store>/e/<seq>");
    }

    private static async Task WriteEvidenceAsync(HttpContext ctx, EvidenceDocument doc)
    {
        ctx.Response.Headers["LatticeLog-Truncated"] = doc.Truncated ? "true" : "false";
        ctx.Response.Headers["LatticeLog-Partial"] = doc.Partial ? "true" : "false";
        await WriteBytesAsync(ctx, 200, "application/json", System.Text.Encoding.UTF8.GetBytes(doc.Json)).ConfigureAwait(false);
    }

    private static async Task EvidenceAsync(HttpContext ctx, ReadContext rc, Func<EvidenceService, EvidenceDocument> build, CancellationToken ct)
    {
        var doc = await Task.Run(() => build(new EvidenceService(rc.Engine)), ct).ConfigureAwait(false);
        await WriteEvidenceAsync(ctx, doc).ConfigureAwait(false);
    }

    private static async Task GetEventAsync(HttpContext ctx, ReadContext rc, CancellationToken ct)
    {
        var seq = ParseEventRef(Uri.UnescapeDataString((string)ctx.Request.RouteValues["seq"]!));
        if (rc.Engine.GetBySequence(seq) is null)
        {
            await ErrorAsync(ctx, 404, "event_not_found", $"no event {seq} in this namespace").ConfigureAwait(false);
            return;
        }
        await EvidenceAsync(ctx, rc, e => e.GetEvent(seq), ct).ConfigureAwait(false);
    }

    private static Task RelatedAsync(HttpContext ctx, ReadContext rc, CancellationToken ct)
    {
        var seq = ParseEventRef(Uri.UnescapeDataString((string)ctx.Request.RouteValues["seq"]!));
        return EvidenceAsync(ctx, rc, e => e.GetRelated(seq, IntArg(rc, "limit", 200, 1, 2_000)), ct);
    }

    private static Task TraceAsync(HttpContext ctx, ReadContext rc, CancellationToken ct)
    {
        var id = TraceId.Parse((string)ctx.Request.RouteValues["traceId"]!);
        return EvidenceAsync(ctx, rc, e => e.GetTrace(id, IntArg(rc, "budget_bytes", 64_000, 1_000, 2_000_000)), ct);
    }

    private async Task StatsAsync(HttpContext ctx, ReadContext rc, CancellationToken ct)
    {
        var req = Scope(rc, limitDefault: 100);
        TimeSpan? bucket = null;
        if (rc.Query["bucket"].ToString() is { Length: > 0 } b)
        {
            var now = UnixTime.NowUnixNanos();
            bucket = TimeSpan.FromTicks((now - TimeArg(rc.Query, "bucket", now)!.Value) / 100);
            if (bucket <= TimeSpan.Zero) throw new ArgumentException("'bucket' must be a positive duration like 5m");
        }
        var by = rc.Query["by"].ToString() is { Length: > 0 } g ? g : bucket is null ? "event" : "time";
        var res = await Task.Run(() => rc.Engine.Aggregate(req, by, bucket, IntArg(rc, "top", 50, 1, 1_000), ct), ct).ConfigureAwait(false);
        await WriteJsonAsync(ctx, 200, new JsonObject
        {
            ["format"] = "latticelog.stats.v1",
            ["namespace"] = rc.Namespace,
            ["group_by"] = res.GroupBy,
            ["total"] = res.Total,
            ["from_index_only"] = res.UsedIndexOnly,
            ["partial"] = res.Partial,
            ["groups"] = new JsonArray(res.Groups.Select(x => (JsonNode?)new JsonObject { ["key"] = x.Key, ["count"] = x.Count }).ToArray()),
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Lossless export page (P17-T04): a complete <c>latticelog.jsonl</c> document with up to <c>limit</c> records
    /// (default and max 10,000). Coverage travels in headers because they must be known before the body:
    /// <c>LatticeLog-Next-Cursor</c> (signed), <c>LatticeLog-Truncated</c>, <c>LatticeLog-Partial</c>.
    /// </summary>
    private async Task ExportAsync(HttpContext ctx, ReadContext rc, CancellationToken ct)
    {
        var req = Scope(rc, limitDefault: 10_000) with { Limit = IntArg(rc, "limit", 10_000, 1, 10_000) };
        var res = await Task.Run(() => rc.Engine.Execute(req, ct), ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        using (var w = new JsonlExchangeWriter(ms, res.StoreId, leaveOpen: true))
            foreach (var h in res.Hits) w.Write(h.Record);
        if (res.NextCursor is not null) ctx.Response.Headers["LatticeLog-Next-Cursor"] = _cursors.Sign(rc.Namespace, rc.Token.Id, res.NextCursor);
        ctx.Response.Headers["LatticeLog-Truncated"] = res.Truncated ? "true" : "false";
        ctx.Response.Headers["LatticeLog-Partial"] = res.Partial ? "true" : "false";
        ctx.Response.Headers["LatticeLog-Records"] = res.Hits.Count.ToString(CultureInfo.InvariantCulture);
        await WriteBytesAsync(ctx, 200, "application/x-ndjson", ms.GetBuffer().AsMemory(0, (int)ms.Length)).ConfigureAwait(false);
    }

    /// <summary>Segment catalog of the namespace (P17-T03): what is stored, sealed or still being written.</summary>
    private static Task CatalogAsync(HttpContext ctx, ReadContext rc, CancellationToken ct)
    {
        var store = rc.Engine.Store;
        var segments = new JsonArray();
        foreach (var id in store.SegmentIds)
        {
            var x = store.Indexes[id];
            var seg = new JsonObject { ["id"] = id.ToString(CultureInfo.InvariantCulture), ["sealed"] = x is not null };
            if (x is { HasRecords: true })
            {
                seg["records"] = x.RecordCount;
                seg["first_seq"] = x.FirstSequence.ToString(CultureInfo.InvariantCulture);
                seg["last_seq"] = x.LastSequence.ToString(CultureInfo.InvariantCulture);
                seg["min_time"] = UnixTime.FormatRfc3339(x.MinTimestamp);
                seg["max_time"] = UnixTime.FormatRfc3339(x.MaxTimestamp);
                seg["services"] = new JsonArray(x.Services.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
            }
            try { seg["bytes"] = new FileInfo(store.SegmentPath(id)).Length; } catch (IOException) { }
            segments.Add((JsonNode?)seg);
        }
        return WriteJsonAsync(ctx, 200, new JsonObject
        {
            ["format"] = "latticelog.catalog.v1",
            ["namespace"] = rc.Namespace,
            ["store_id"] = store.Info.StoreId,
            ["segments"] = segments,
            ["manifest_problems"] = new JsonArray(store.ManifestProblems.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
        });
    }
}
