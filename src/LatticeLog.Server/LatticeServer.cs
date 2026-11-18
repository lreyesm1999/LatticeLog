using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using LatticeLog.Ingest;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LatticeLog.Server;

/// <summary>Read-side limits (P17-T06/T08): queries never share threads or limits with ingestion.</summary>
public sealed record QueryLimits
{
    /// <summary>Queries running at once on the whole server; others wait up to <see cref="QueueWait"/>, then 503.</summary>
    public int MaxConcurrentQueries { get; init; } = Math.Max(2, Environment.ProcessorCount / 2);
    public TimeSpan QueueWait { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Queries running at once per token; beyond this, 429.</summary>
    public int PerTokenConcurrentQueries { get; init; } = 4;
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan MaxTimeout { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>Records a single request may decode; beyond it the answer is marked partial.</summary>
    public long MaxScannedRecords { get; init; } = 20_000_000;
    public int MaxPageSize { get; init; } = 1_000;
    public long MaxExportRecords { get; init; } = 1_000_000;
    public TimeSpan CursorTtl { get; init; } = TimeSpan.FromHours(1);
}

public sealed record ServerOptions
{
    /// <summary>Root directory: <c>ns/&lt;namespace&gt;</c> stores and <c>server.key</c>.</summary>
    public required string DataDirectory { get; init; }
    public required TokenStore Tokens { get; init; }
    /// <summary>Per-namespace ingestion settings (StoreDirectory is filled in per namespace).</summary>
    public IngestServiceOptions IngestTemplate { get; init; } = new() { StoreDirectory = "" };
    public IngestLimits Limits { get; init; } = new();
    public QueryLimits Query { get; init; } = new();
    /// <summary>For example <c>https://0.0.0.0:7400</c> or <c>http://127.0.0.1:0</c> (0 = any free port).</summary>
    public string Url { get; init; } = "http://127.0.0.1:7400";
    public string? CertificatePath { get; init; }
    public string? CertificatePassword { get; init; }
    /// <summary>Plain HTTP on a non-loopback address. Off by default: credentials and logs would travel in clear text.</summary>
    public bool AllowInsecureHttp { get; init; }
    /// <summary>Server-level diagnostics (unexpected errors). Must not block or throw.</summary>
    public Action<string>? Diagnostics { get; init; }
    /// <summary>Where <c>POST /v1/admin/backup</c> writes snapshots (null = endpoint disabled).</summary>
    public string? BackupDirectory { get; init; }
    /// <summary>Serve <c>/metrics</c> without a token (only on a trusted network).</summary>
    public bool MetricsAnonymous { get; init; }
    /// <summary>Asynchronous replication of the namespaces to another server (P19). Null = no replication.</summary>
    public ReplicationOptions? Replication { get; init; }
}

/// <summary>
/// LatticeLog server v1 (P16 + P17): ingestion (<c>POST /v1/ingest</c>) and a read API per namespace, behind bearer
/// tokens with scopes. See specs/ingest-protocol.md and specs/server-api.md.
/// </summary>
public sealed partial class LatticeServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ServerOptions _options;
    private readonly NamespaceRegistry _namespaces;
    private readonly CursorSigner _cursors;
    private readonly QuotaLimiter _quota = new();
    private readonly SemaphoreSlim _querySlots;
    private readonly ConcurrentDictionary<string, int> _queriesPerToken = new(StringComparer.Ordinal);
    private readonly AuditLog _audit;
    private Replicator? _replicator;

    private LatticeServer(WebApplication app, ServerOptions options)
    {
        _app = app;
        _options = options;
        _namespaces = new NamespaceRegistry(options.DataDirectory, options.IngestTemplate);
        _cursors = CursorSigner.FromDataDirectory(options.DataDirectory, options.Query.CursorTtl);
        _querySlots = new SemaphoreSlim(options.Query.MaxConcurrentQueries);
        _audit = new AuditLog(options.DataDirectory);
    }

    public AuditLog AuditTrail => _audit;

    private void Audit(HttpContext ctx, TokenInfo? token, string? ns, string action, string? detail = null, string outcome = "ok")
        => _audit.Record(action, token?.Id, ctx.Connection.RemoteIpAddress?.ToString(), ns, outcome, detail);

    /// <summary>Address actually bound (useful with port 0).</summary>
    public Uri BaseAddress { get; private set; } = null!;

    public NamespaceRegistry Namespaces => _namespaces;

    /// <summary>The ingestion service of a namespace (opened on demand).</summary>
    public IngestService Ingest(string ns = NamespaceNames.Default) => _namespaces.Ingest(ns);

    public static async Task<LatticeServer> StartAsync(ServerOptions options)
    {
        var url = new Uri(options.Url);
        var https = url.Scheme == Uri.UriSchemeHttps;
        var address = url.Host == "localhost" ? IPAddress.Loopback
            : IPAddress.TryParse(url.Host.Trim('[', ']'), out var parsed) ? parsed
            : throw new InvalidOperationException($"Listen address must be an IP or localhost, got '{url.Host}'.");
        if (!https && !IPAddress.IsLoopback(address) && !options.AllowInsecureHttp)
            throw new InvalidOperationException("Refusing plain HTTP on a non-loopback address: configure a certificate (https) or set AllowInsecureHttp explicitly.");
        if (https && options.CertificatePath is null)
            throw new InvalidOperationException("https requires CertificatePath (PFX).");

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Limits.MaxRequestBodySize = options.Limits.MaxRequestBytes;
            k.Limits.MinRequestBodyDataRate = new MinDataRate(bytesPerSecond: 1024, gracePeriod: TimeSpan.FromSeconds(10)); // slowloris
            k.AddServerHeader = false;
            k.Listen(address, url.Port, lo =>
            {
                lo.Protocols = HttpProtocols.Http1AndHttp2;
                if (https) lo.UseHttps(options.CertificatePath!, options.CertificatePassword);
            });
        });
        var app = builder.Build();
        var server = new LatticeServer(app, options);
        app.MapPost(IngestProtocol.Path, (RequestDelegate)server.HandleIngestAsync);
        app.MapGet("/v1/health", (RequestDelegate)server.HandleHealthAsync);
        server.MapReadApi(app);
        server.MapAdminApi(app);
        server.MapOps(app);
        await app.StartAsync().ConfigureAwait(false);
        if (options.Replication is { } repl) server._replicator = new Replicator(server._namespaces, repl, server.Diagnose);
        var bound = app.Urls.First();
        server.BaseAddress = new Uri(bound.Replace("[::]", "127.0.0.1", StringComparison.Ordinal).Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal));
        return server;
    }

    private void Diagnose(string message)
    {
        try { _options.Diagnostics?.Invoke("[LatticeLog.Server] " + message); } catch { /* never break a request */ }
    }

    // ------------------------------------------------------------------ shared helpers

    private static Task WriteJsonAsync(HttpContext ctx, int status, JsonObject body, TimeSpan? retryAfter = null)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        if (retryAfter is { } ra) ctx.Response.Headers.RetryAfter = ((int)Math.Ceiling(ra.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        return ctx.Response.WriteAsync(body.ToJsonString());
    }

    private static Task ErrorAsync(HttpContext ctx, int status, string error, string message, TimeSpan? retryAfter = null, JsonObject? extra = null)
    {
        var body = extra ?? [];
        body["error"] = error;
        body["message"] = message;
        return WriteJsonAsync(ctx, status, body, retryAfter);
    }

    private TokenInfo? Authenticate(HttpContext ctx)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        return _options.Tokens.Authenticate(auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth[7..].Trim() : null);
    }

    // Brute-force brake (P18): too many failed authentications from one address → 429 for the rest of the window.
    private readonly ConcurrentDictionary<string, (long WindowStart, int Failures)> _authFailures = new(StringComparer.Ordinal);
    private const int MaxAuthFailuresPerMinute = 20;

    private bool AuthThrottled(HttpContext ctx, bool failedNow)
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
        var now = Environment.TickCount64;
        var entry = _authFailures.AddOrUpdate(ip,
            _ => (now, failedNow ? 1 : 0),
            (_, e) => now - e.WindowStart > 60_000 ? (now, failedNow ? 1 : 0) : (e.WindowStart, e.Failures + (failedNow ? 1 : 0)));
        if (_authFailures.Count > 100_000) _authFailures.Clear(); // bounded memory under address spraying
        return entry.Failures > MaxAuthFailuresPerMinute;
    }

    /// <summary>Authenticates and checks scope + namespace. Writes the error and returns null when refused.</summary>
    private async Task<(TokenInfo Token, string Namespace)?> AuthorizeAsync(HttpContext ctx, TokenScopes scope, string? requestedNamespace)
    {
        if (AuthThrottled(ctx, failedNow: false))
        {
            await ErrorAsync(ctx, 429, "too_many_auth_failures", "too many failed authentications from this address; wait a minute", TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            return null;
        }
        var token = Authenticate(ctx);
        if (token is null)
        {
            AuthThrottled(ctx, failedNow: true);
            Audit(ctx, null, requestedNamespace, "auth." + ctx.Request.Path, null, "unauthorized");
            ctx.Response.Headers.WWWAuthenticate = "Bearer";
            await ErrorAsync(ctx, 401, "unauthorized", "missing or unknown bearer token").ConfigureAwait(false);
            return null;
        }
        if (!token.Has(scope))
        {
            Audit(ctx, token, requestedNamespace, "auth." + ctx.Request.Path, $"missing scope {scope}", "forbidden");
            await ErrorAsync(ctx, 403, "forbidden_scope", $"token '{token.Id}' lacks the '{scope.ToString().ToLowerInvariant()}' scope").ConfigureAwait(false);
            return null;
        }
        var ns = string.IsNullOrEmpty(requestedNamespace) ? token.DefaultNamespace : requestedNamespace;
        if (!NamespaceNames.IsValid(ns))
        {
            await ErrorAsync(ctx, 400, "invalid_namespace", "namespace must look like tenant[/project[/environment]] (a-z, 0-9, . _ -)").ConfigureAwait(false);
            return null;
        }
        if (!token.AllowsNamespace(ns))
        {
            Audit(ctx, token, ns, "auth." + ctx.Request.Path, "namespace not allowed", "forbidden");
            await ErrorAsync(ctx, 403, "forbidden_namespace", $"token '{token.Id}' may not access namespace '{ns}'").ConfigureAwait(false);
            return null;
        }
        return (token, ns);
    }

    // ------------------------------------------------------------------ ingestion

    private async Task HandleIngestAsync(HttpContext ctx)
    {
        var auth = await AuthorizeAsync(ctx, TokenScopes.Ingest, ctx.Request.Headers["LatticeLog-Namespace"].ToString()).ConfigureAwait(false);
        if (auth is not var (token, ns)) return;
        if (ctx.Request.ContentLength is > 0 and var len && len > _options.Limits.MaxRequestBytes)
        {
            await ErrorAsync(ctx, 413, "too_large", $"request larger than {_options.Limits.MaxRequestBytes} bytes").ConfigureAwait(false);
            return;
        }
        byte[] body;
        try
        {
            using var ms = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int n;
            while ((n = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted).ConfigureAwait(false)) > 0)
            {
                if (ms.Length + n > _options.Limits.MaxRequestBytes)
                {
                    await ErrorAsync(ctx, 413, "too_large", $"request larger than {_options.Limits.MaxRequestBytes} bytes").ConfigureAwait(false);
                    return;
                }
                ms.Write(buffer, 0, n);
            }
            body = ms.ToArray();
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException ex)
        {
            await ErrorAsync(ctx, ex.StatusCode == 413 ? 413 : 400, ex.StatusCode == 413 ? "too_large" : "invalid_envelope", ex.Message).ConfigureAwait(false);
            return;
        }

        DecodedBatch batch;
        try { batch = BatchDecoder.Decode(body, _options.Limits); }
        catch (IngestException ex)
        {
            var extra = ex.Error == "unsupported_encoding" ? new JsonObject { ["accepted"] = new JsonArray("jsonl+none", "jsonl+gzip") } : null;
            await ErrorAsync(ctx, ex.Status, ex.Error, ex.Message, extra: extra).ConfigureAwait(false);
            return;
        }
        if (!token.AllowsProducer(batch.ProducerId))
        {
            await ErrorAsync(ctx, 403, "forbidden_producer", $"token '{token.Id}' may not send for producer {batch.ProducerId}").ConfigureAwait(false);
            return;
        }
        if (!_quota.TryTake(token, batch.Records.Count, out var wait))
        {
            await ErrorAsync(ctx, 429, "quota_exceeded", $"token '{token.Id}' exceeded {token.RecordsPerSecond} records/s", wait).ConfigureAwait(false);
            return;
        }
        var ack = ctx.Request.Headers[IngestProtocol.AckHeader].ToString() == "os" ? AckLevel.WrittenToOs : AckLevel.FsyncConfirmed;
        IngestService.Prepare(batch);
        IngestService service;
        try { service = _namespaces.Ingest(ns); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ErrorAsync(ctx, 503, "persist_failed", "cannot open namespace store: " + ex.Message, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            return;
        }
        var result = await service.SubmitAsync(batch, ack).ConfigureAwait(false);
        switch (result.Status)
        {
            case SubmitStatus.Acknowledged:
                await WriteJsonAsync(ctx, 200, result.Ack!.ToJson()).ConfigureAwait(false);
                break;
            case SubmitStatus.SequenceGap:
                await ErrorAsync(ctx, 409, "sequence_gap", "batch does not continue the committed sequence",
                    extra: new JsonObject { ["committed_through"] = result.CommittedThrough.ToString(CultureInfo.InvariantCulture) }).ConfigureAwait(false);
                break;
            case SubmitStatus.Overloaded:
                await ErrorAsync(ctx, 503, "overloaded", result.Error ?? "busy", TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                break;
            default:
                await ErrorAsync(ctx, 503, "persist_failed", result.Error ?? "server cannot persist", TimeSpan.FromSeconds(30),
                    extra: new JsonObject { ["ack"] = AckLevel.None.ToString() }).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>Unauthenticated liveness: only "ok"/"failed" and counts. Details per namespace need an admin token.</summary>
    private async Task HandleHealthAsync(HttpContext ctx)
    {
        var services = _namespaces.OpenServices();
        var failed = services.Where(s => s.Service.Failure is not null).ToList();
        var body = new JsonObject { ["status"] = failed.Count == 0 ? "ok" : "failed", ["open_namespaces"] = services.Count };
        if (Authenticate(ctx) is { } t && t.Has(TokenScopes.Admin))
        {
            body["audit_events_dropped"] = _audit.Dropped;
            if (_replicator is not null)
            {
                var repl = new JsonObject();
                foreach (var st in _replicator.Status())
                {
                    var last = _namespaces.Exists(st.Namespace) ? _namespaces.Ingest(st.Namespace).LastSequence : 0;
                    repl[st.Namespace] = new JsonObject
                    {
                        ["replicated_through"] = st.ReplicatedThrough.ToString(CultureInfo.InvariantCulture),
                        ["lag_records"] = (long)(last - Math.Min(last, st.ReplicatedThrough)),
                        ["state"] = st.State.ToString(),
                        ["last_error"] = st.LastError,
                    };
                }
                body["replication"] = repl;
            }
            var detail = new JsonObject();
            foreach (var (ns, svc) in services)
            {
                var st = svc.GetStats();
                detail[ns] = new JsonObject
                {
                    ["failure"] = svc.Failure,
                    ["store_id"] = svc.StoreId,
                    ["last_sequence"] = svc.LastSequence.ToString(CultureInfo.InvariantCulture),
                    ["producers"] = svc.ProducerCount,
                    ["batches_stored"] = st.BatchesStored,
                    ["flushes"] = st.Flushes,
                    ["queue_depth"] = st.QueueDepth,
                };
            }
            body["namespaces"] = detail;
        }
        await WriteJsonAsync(ctx, failed.Count == 0 ? 200 : 503, body).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ lifecycle

    private int _disposed;

    /// <summary>Test-only: stop as after a crash (no final checkpoints).</summary>
    public async Task AbortAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_replicator is not null) await _replicator.DisposeAsync().ConfigureAwait(false);
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        _namespaces.Abort();
        await _audit.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Replication status per namespace (null when replication is off).</summary>
    public IReadOnlyList<Replicator.NamespaceStatus>? ReplicationStatus => _replicator?.Status();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_replicator is not null) await _replicator.DisposeAsync().ConfigureAwait(false);
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        _namespaces.Dispose();
        _querySlots.Dispose();
        await _audit.DisposeAsync().ConfigureAwait(false);
    }
}
