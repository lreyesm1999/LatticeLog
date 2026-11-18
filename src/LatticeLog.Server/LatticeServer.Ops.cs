using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using LatticeLog.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace LatticeLog.Server;

/// <summary>Operations (P23 console, P24 metrics and backup).</summary>
public sealed partial class LatticeServer
{
    private const string ConsoleCsp = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; " +
                                      "frame-ancestors 'none'; base-uri 'none'; form-action 'self'; object-src 'none'";

    private void MapOps(WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers.XContentTypeOptions = "nosniff";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            try { await next(ctx).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Last line: anything a handler did not catch is reported (logging providers are cleared) and the
                // client gets a JSON 500 without internals.
                Diagnose($"unhandled {ctx.Request.Method} {ctx.Request.Path}: {ex}");
                if (!ctx.Response.HasStarted) await ErrorAsync(ctx, 500, "internal_error", "unexpected server error").ConfigureAwait(false);
            }
        });
        app.MapGet("/ui", (RequestDelegate)(ctx => ServeUiAsync(ctx, "index.html", "text/html; charset=utf-8")));
        app.MapGet("/ui/app.js", (RequestDelegate)(ctx => ServeUiAsync(ctx, "app.js", "text/javascript; charset=utf-8")));
        app.MapGet("/ui/app.css", (RequestDelegate)(ctx => ServeUiAsync(ctx, "app.css", "text/css; charset=utf-8")));
        app.MapGet("/metrics", (RequestDelegate)HandleMetricsAsync);
        app.MapPost("/v1/admin/backup", (RequestDelegate)HandleBackupAsync);
    }

    /// <summary>The console is static and talks to the read API with the viewer's own token (nothing server-side).</summary>
    private static async Task ServeUiAsync(HttpContext ctx, string file, string contentType)
    {
        await using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ui/" + file);
        if (s is null) { ctx.Response.StatusCode = 404; return; }
        ctx.Response.ContentType = contentType;
        ctx.Response.Headers.ContentSecurityPolicy = ConsoleCsp;
        ctx.Response.Headers.XFrameOptions = "DENY";
        ctx.Response.Headers.CacheControl = "no-cache";
        await s.CopyToAsync(ctx.Response.Body).ConfigureAwait(false);
    }

    /// <summary>Prometheus text exposition. Needs an admin token unless <see cref="ServerOptions.MetricsAnonymous"/>.</summary>
    private async Task HandleMetricsAsync(HttpContext ctx)
    {
        if (!_options.MetricsAnonymous && (Authenticate(ctx) is not { } t || !t.Has(TokenScopes.Admin)))
        {
            ctx.Response.StatusCode = 401;
            ctx.Response.Headers.WWWAuthenticate = "Bearer";
            return;
        }
        var sb = new StringBuilder();
        void Metric(string name, string type, string help)
        {
            sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
            sb.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
        }
        void Sample(string name, string? ns, double value)
        {
            sb.Append(name);
            if (ns is not null) sb.Append("{namespace=\"").Append(ns.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\"}");
            sb.Append(' ').Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        var services = _namespaces.OpenServices();
        (string Name, string Type, string Help, Func<IngestService, IngestStats, double> Get)[] perNs =
        [
            ("latticelog_store_last_sequence", "gauge", "Highest sequence stored in the namespace.", (s, _) => s.LastSequence),
            ("latticelog_ingest_records_stored_total", "counter", "Records stored since start.", (_, st) => st.RecordsStored),
            ("latticelog_ingest_batches_stored_total", "counter", "Batches stored since start.", (_, st) => st.BatchesStored),
            ("latticelog_ingest_batches_duplicate_total", "counter", "Batches answered as duplicate since start.", (_, st) => st.BatchesDuplicate),
            ("latticelog_ingest_flushes_total", "counter", "Shared flushes (group commits) since start.", (_, st) => st.Flushes),
            ("latticelog_ingest_queue_depth", "gauge", "Batches waiting for the writer.", (_, st) => st.QueueDepth),
            ("latticelog_ingest_producers", "gauge", "Producers known to the deduplication state.", (s, _) => s.ProducerCount),
            ("latticelog_ingest_failed", "gauge", "1 when the namespace refuses writes after a storage failure.", (s, _) => s.Failure is null ? 0 : 1),
        ];
        foreach (var (name, type, help, get) in perNs)
        {
            Metric(name, type, help);
            foreach (var (ns, svc) in services) Sample(name, ns, get(svc, svc.GetStats()));
        }
        if (_replicator is not null)
        {
            Metric("latticelog_replication_lag_records", "gauge", "Records stored but not yet acknowledged by the replica.");
            foreach (var st in _replicator.Status())
            {
                var last = services.FirstOrDefault(s => s.Namespace == st.Namespace).Service?.LastSequence ?? st.ReplicatedThrough;
                Sample("latticelog_replication_lag_records", st.Namespace, last - Math.Min(last, st.ReplicatedThrough));
            }
        }
        Metric("latticelog_queries_running", "gauge", "Read queries running now.");
        Sample("latticelog_queries_running", null, _options.Query.MaxConcurrentQueries - _querySlots.CurrentCount);
        Metric("latticelog_audit_events_dropped_total", "counter", "Audit events lost because the queue was full.");
        Sample("latticelog_audit_events_dropped_total", null, _audit.Dropped);
        using var proc = Process.GetCurrentProcess();
        Metric("latticelog_process_working_set_bytes", "gauge", "Process working set.");
        Sample("latticelog_process_working_set_bytes", null, proc.WorkingSet64);
        Metric("latticelog_gc_heap_bytes", "gauge", "Managed heap size.");
        Sample("latticelog_gc_heap_bytes", null, GC.GetTotalMemory(false));
        ctx.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
        await ctx.Response.WriteAsync(sb.ToString()).ConfigureAwait(false);
    }

    /// <summary>Hot backup of one namespace into the configured backup directory (never a client-supplied path).</summary>
    private async Task HandleBackupAsync(HttpContext ctx)
    {
        if (await AdminTargetAsync(ctx).ConfigureAwait(false) is not var (token, ns, service)) return;
        if (_options.BackupDirectory is null)
        {
            await ErrorAsync(ctx, 409, "backup_not_configured", "start the server with a backup directory").ConfigureAwait(false);
            return;
        }
        var dest = Path.Combine(_options.BackupDirectory, NamespaceNames.DirectoryName(ns), DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture));
        var result = await Task.Run(() => StoreBackup.Create(service.StoreDirectory, dest)).ConfigureAwait(false);
        Audit(ctx, token, ns, "admin.backup", $"segments={result.Segments} records={result.Records} verified={result.Verified}");
        await WriteJsonAsync(ctx, result.Verified ? 200 : 500, new JsonObject
        {
            ["namespace"] = ns,
            ["backup"] = Path.GetFileName(dest),
            ["segments"] = result.Segments,
            ["bytes"] = result.Bytes,
            ["records"] = result.Records,
            ["verified"] = result.Verified,
            ["issues"] = new JsonArray(result.Issues.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()),
        }).ConfigureAwait(false);
    }
}
