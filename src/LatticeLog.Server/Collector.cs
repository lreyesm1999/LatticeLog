using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using LatticeLog.Ingest;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LatticeLog.Server;

public sealed record CollectorOptions
{
    public required IngestServiceOptions Ingest { get; init; }
    public required TokenStore Tokens { get; init; }
    public IngestLimits Limits { get; init; } = new();
    /// <summary>For example <c>https://0.0.0.0:7400</c> or <c>http://127.0.0.1:0</c> (0 = any free port).</summary>
    public string Url { get; init; } = "http://127.0.0.1:7400";
    public string? CertificatePath { get; init; }
    public string? CertificatePassword { get; init; }
    /// <summary>Plain HTTP on a non-loopback address. Off by default: credentials and logs would travel in clear text.</summary>
    public bool AllowInsecureHttp { get; init; }
}

/// <summary>HTTP collector (ADR-0009): <c>POST /v1/ingest</c> and <c>GET /v1/health</c>.</summary>
public sealed class Collector : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly IngestService _service;
    private readonly CollectorOptions _options;
    private readonly QuotaLimiter _quota = new();

    private Collector(WebApplication app, IngestService service, CollectorOptions options)
    {
        _app = app;
        _service = service;
        _options = options;
    }

    public IngestService Service => _service;

    /// <summary>Address actually bound (useful with port 0).</summary>
    public Uri BaseAddress { get; private set; } = null!;

    public static async Task<Collector> StartAsync(CollectorOptions options)
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
        var service = new IngestService(options.Ingest);
        var collector = new Collector(app, service, options);
        app.MapPost(IngestProtocol.Path, (RequestDelegate)collector.HandleIngestAsync);
        app.MapGet("/v1/health", (RequestDelegate)collector.HandleHealthAsync);
        await app.StartAsync().ConfigureAwait(false);
        var bound = app.Urls.First();
        collector.BaseAddress = new Uri(bound.Replace("[::]", "127.0.0.1", StringComparison.Ordinal).Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal));
        return collector;
    }

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

    private async Task HandleIngestAsync(HttpContext ctx)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        var token = _options.Tokens.Authenticate(auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth[7..].Trim() : null);
        if (token is null)
        {
            ctx.Response.Headers.WWWAuthenticate = "Bearer";
            await ErrorAsync(ctx, 401, "unauthorized", "missing or unknown bearer token").ConfigureAwait(false);
            return;
        }
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
        var result = await _service.SubmitAsync(batch, ack).ConfigureAwait(false);
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

    private Task HandleHealthAsync(HttpContext ctx) => WriteJsonAsync(ctx, _service.Failure is null ? 200 : 503, new JsonObject
    {
        ["status"] = _service.Failure is null ? "ok" : "failed",
        ["failure"] = _service.Failure,
        ["store_id"] = _service.StoreId,
        ["last_sequence"] = _service.LastSequence.ToString(CultureInfo.InvariantCulture),
        ["producers"] = _service.ProducerCount,
    });

    private int _disposed;

    /// <summary>Test-only: stop as after a crash (no final checkpoint).</summary>
    public async Task AbortAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        _service.Abort();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        _service.Dispose();
    }
}
