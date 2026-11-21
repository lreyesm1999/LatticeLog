using System.Globalization;
using System.Text.Json.Nodes;
using LatticeLog;
using LatticeLog.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace LatticeLog.Server;

/// <summary>
/// Online administration (P17-T12), <c>admin</c> scope + namespace access. Operations run while the server keeps
/// ingesting and serving queries; one admin operation at a time per namespace.
/// </summary>
public sealed partial class LatticeServer
{
    private void MapAdminApi(WebApplication app)
    {
        app.MapPost("/v1/admin/retention", (RequestDelegate)HandleRetentionAsync);
        app.MapPost("/v1/admin/compact", (RequestDelegate)HandleCompactAsync);
        app.MapPost("/v1/admin/verify", (RequestDelegate)HandleVerifyAsync);
        app.MapPost("/v1/admin/erase", (RequestDelegate)HandleEraseAsync);
    }

    private async Task<(TokenInfo Token, string Namespace, IngestService Service)?> AdminTargetAsync(HttpContext ctx)
    {
        var auth = await AuthorizeAsync(ctx, TokenScopes.Admin, ctx.Request.Query["ns"].ToString()).ConfigureAwait(false);
        if (auth is not var (token, ns)) return null;
        if (!_namespaces.Exists(ns))
        {
            await ErrorAsync(ctx, 404, "namespace_not_found", $"namespace '{ns}' has no data").ConfigureAwait(false);
            return null;
        }
        return (token, ns, _namespaces.Ingest(ns));
    }

    private async Task HandleRetentionAsync(HttpContext ctx)
    {
        if (await AdminTargetAsync(ctx).ConfigureAwait(false) is not var (token, ns, service)) return;
        TimeSpan? maxAge = null;
        long? maxBytes = null;
        try
        {
            var now = UnixTime.NowUnixNanos();
            if (TimeArg(ctx.Request.Query, "max_age", now) is { } cutoff)
            {
                maxAge = TimeSpan.FromTicks((now - cutoff) / 100);
                if (maxAge <= TimeSpan.Zero) throw new ArgumentException("'max_age' must be a positive duration like 30d");
            }
            if (ctx.Request.Query["max_mb"].ToString() is { Length: > 0 }) maxBytes = IntArg(ctx.Request.Query, "max_mb", 0, 1, int.MaxValue) * 1024L * 1024;
        }
        catch (ArgumentException ex)
        {
            await ErrorAsync(ctx, 400, "invalid_request", ex.Message).ConfigureAwait(false);
            return;
        }
        var policy = new RetentionPolicy { MaxAge = maxAge, MaxBytes = maxBytes };
        if (policy.IsEmpty)
        {
            await ErrorAsync(ctx, 400, "invalid_request", "give max_age and/or max_mb").ConfigureAwait(false);
            return;
        }
        var dryRun = ctx.Request.Query["dry_run"].ToString() == "true";
        var result = await service.ApplyRetentionAsync(policy, dryRun).ConfigureAwait(false);
        Audit(ctx, token, ns, "admin.retention", $"dry_run={dryRun} max_age={maxAge} max_bytes={maxBytes} segments={result.Deleted.Count} records={result.Records}");
        await WriteJsonAsync(ctx, 200, new JsonObject
        {
            ["namespace"] = ns,
            ["dry_run"] = dryRun,
            ["segments"] = result.Deleted.Count,
            ["records"] = result.Records,
            ["bytes"] = result.Bytes,
            ["deleted"] = new JsonArray(result.Deleted.Select(d => (JsonNode?)new JsonObject
            {
                ["segment_id"] = d.SegmentId.ToString(CultureInfo.InvariantCulture),
                ["reason"] = d.Reason,
                ["records"] = d.Records,
                ["first_seq"] = d.FirstSequence.ToString(CultureInfo.InvariantCulture),
                ["last_seq"] = d.LastSequence.ToString(CultureInfo.InvariantCulture),
                ["max_time"] = UnixTime.FormatRfc3339(d.MaxTimestampUnixNanos),
            }).ToArray()),
        }).ConfigureAwait(false);
    }

    private async Task HandleCompactAsync(HttpContext ctx)
    {
        if (await AdminTargetAsync(ctx).ConfigureAwait(false) is not var (token, ns, service)) return;
        CompactionPolicy? policy = ctx.Request.Query["policy"].ToString() switch
        {
            "" or "balanced" => CompactionPolicy.Balanced,
            "fast" => CompactionPolicy.Fast,
            "compact" => CompactionPolicy.Compact,
            _ => null,
        };
        if (policy is null)
        {
            await ErrorAsync(ctx, 400, "invalid_request", "policy must be fast, balanced or compact").ConfigureAwait(false);
            return;
        }
        var targetMb = IntArg(ctx.Request.Query, "target_mb", 256, 1, 4096);
        IReadOnlyList<CompactionResult> results;
        try { results = await service.CompactAsync(policy.Value, targetMb * 1024L * 1024, ctx.RequestAborted).ConfigureAwait(false); }
        catch (InvalidOperationException ex)
        {
            // Damaged input: compaction refuses to rewrite it (never compact corrupt data silently).
            await ErrorAsync(ctx, 409, "compaction_refused", ex.Message).ConfigureAwait(false);
            return;
        }
        Audit(ctx, token, ns, "admin.compact", $"policy={policy} groups={results.Count} records={results.Sum(r => r.Records)}");
        await WriteJsonAsync(ctx, 200, new JsonObject
        {
            ["namespace"] = ns,
            ["compactions"] = new JsonArray(results.Select(r => (JsonNode?)new JsonObject
            {
                ["inputs"] = new JsonArray(r.Inputs.Select(i => (JsonNode?)JsonValue.Create(i.ToString(CultureInfo.InvariantCulture))).ToArray()),
                ["output"] = r.Output.ToString(CultureInfo.InvariantCulture),
                ["records"] = r.Records,
                ["input_bytes"] = r.InputBytes,
                ["output_bytes"] = r.OutputBytes,
                ["codec"] = $"{r.Choice.Codec}+{r.Choice.Compression}",
                ["elapsed_ms"] = Math.Round(r.Elapsed.TotalMilliseconds, 1),
            }).ToArray()),
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Erasure (P18): <c>where</c> is required and is never "everything" by accident (an empty filter is refused;
    /// use retention to drop whole periods). <c>dry_run=true</c> only counts.
    /// </summary>
    private async Task HandleEraseAsync(HttpContext ctx)
    {
        if (await AdminTargetAsync(ctx).ConfigureAwait(false) is not var (token, ns, service)) return;
        var where = ctx.Request.Query["where"].ToString();
        if (string.IsNullOrWhiteSpace(where))
        {
            await ErrorAsync(ctx, 400, "invalid_request", "erase requires a 'where' filter").ConfigureAwait(false);
            return;
        }
        Func<LogRecord, bool> match;
        try { match = LatticeLog.Query.Evaluator.Compile(LatticeLog.Query.QueryParser.Parse(where)); }
        catch (LatticeLog.Query.QuerySyntaxException ex)
        {
            await ErrorAsync(ctx, 400, "invalid_query", ex.Message).ConfigureAwait(false);
            return;
        }
        if (LatticeLog.Query.QueryParser.Parse(where) is LatticeLog.Query.TrueExpr)
        {
            await ErrorAsync(ctx, 400, "invalid_request", "the filter matches everything; use retention instead").ConfigureAwait(false);
            return;
        }
        var dryRun = ctx.Request.Query["dry_run"].ToString() == "true";
        (long records, IReadOnlyList<CompactionResult> rewrites) result;
        try { result = await service.EraseAsync(match, dryRun, ctx.RequestAborted).ConfigureAwait(false); }
        catch (InvalidOperationException ex)
        {
            await ErrorAsync(ctx, 409, "erase_refused", ex.Message).ConfigureAwait(false);
            return;
        }
        Audit(ctx, token, ns, "admin.erase", $"dry_run={dryRun} where={where} records={result.records} segments_rewritten={result.rewrites.Count}");
        await WriteJsonAsync(ctx, 200, new JsonObject
        {
            ["namespace"] = ns,
            ["dry_run"] = dryRun,
            ["filter"] = where,
            ["records"] = result.records,
            ["segments_rewritten"] = result.rewrites.Count,
            ["note"] = "copies outside this store (producer stores, exports, backups, replicas) are not affected",
        }).ConfigureAwait(false);
    }

    /// <summary>Full integrity scan of a namespace (read-only; uses a query slot).</summary>
    private async Task HandleVerifyAsync(HttpContext ctx)
    {
        if (await AdminTargetAsync(ctx).ConfigureAwait(false) is not var (token, ns, service)) return;
        if (!await _querySlots.WaitAsync(_options.Query.QueueWait, ctx.RequestAborted).ConfigureAwait(false))
        {
            await ErrorAsync(ctx, 503, "overloaded", "all query slots are busy", TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            return;
        }
        try
        {
            var (records, report) = await Task.Run(() =>
            {
                var rep = new ScanReport();
                long n = 0;
                foreach (var b in StoreReader.Open(service.StoreDirectory).ReadBlocks(rep)) n += b.Records.Count;
                return (n, rep);
            }, ctx.RequestAborted).ConfigureAwait(false);
            Audit(ctx, token, ns, "admin.verify", $"records={records} issues={report.Issues.Count}");
            await WriteJsonAsync(ctx, 200, new JsonObject
            {
                ["namespace"] = ns,
                ["ok"] = report.IsClean,
                ["records"] = records,
                ["issues"] = new JsonArray(report.Issues.Select(i => (JsonNode?)new JsonObject
                {
                    ["kind"] = i.Kind.ToString(),
                    ["file"] = Path.GetFileName(i.File),
                    ["offset"] = i.Offset,
                    ["message"] = i.Message,
                    ["lost_records"] = i.LostRecords,
                }).ToArray()),
            }).ConfigureAwait(false);
        }
        finally { _querySlots.Release(); }
    }
}
