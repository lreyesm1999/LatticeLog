using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using LatticeLog.Storage;

namespace LatticeLog.Ingest;

/// <summary>How the client must react to an answer (specs/ingest-protocol.md §3).</summary>
public enum SendOutcome { Acknowledged, Retry, SequenceGap, TooLarge, UnsupportedEncoding, Fatal }

public sealed record SendResult(SendOutcome Outcome, IngestAck? Ack = null, ulong CommittedThrough = 0, TimeSpan? RetryAfter = null, string? Error = null);

/// <summary>Thin HTTP client for <c>POST /v1/ingest</c>. Never retries by itself; see <see cref="Forwarder"/>.</summary>
public sealed class IngestClient(HttpClient http, Uri baseUri, string token, string? ns = null)
{
    public async Task<SendResult> SendAsync(byte[] envelope, AckLevel ack, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, IngestProtocol.Path));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add(IngestProtocol.AckHeader, ack >= AckLevel.FsyncConfirmed ? "fsync" : "os");
        if (ns is not null) req.Headers.Add("LatticeLog-Namespace", ns);
        req.Content = new ByteArrayContent(envelope);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue(IngestProtocol.ContentType);
        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) { return new SendResult(SendOutcome.Retry, Error: "network: " + ex.Message); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { return new SendResult(SendOutcome.Retry, Error: "timeout: " + ex.Message); }
        using (resp)
        {
            string text;
            try { text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
            catch (HttpRequestException ex) { return new SendResult(SendOutcome.Retry, Error: "response lost: " + ex.Message); }
            JsonNode? json = null;
            try { json = text.Length > 0 ? JsonNode.Parse(text) : null; } catch (System.Text.Json.JsonException) { }
            var error = (string?)json?["error"] ?? $"HTTP {(int)resp.StatusCode}";
            var retryAfter = resp.Headers.RetryAfter?.Delta;
            switch ((int)resp.StatusCode)
            {
                case 200:
                    if (json is null) return new SendResult(SendOutcome.Retry, Error: "empty 200 response");
                    return new SendResult(SendOutcome.Acknowledged, IngestAck.FromJson(json));
                case 409 when error == "sequence_gap":
                    return new SendResult(SendOutcome.SequenceGap, CommittedThrough: ulong.Parse((string)json!["committed_through"]!, CultureInfo.InvariantCulture), Error: error);
                case 413:
                    return new SendResult(SendOutcome.TooLarge, Error: error);
                case 415:
                    return new SendResult(SendOutcome.UnsupportedEncoding, Error: error);
                case 429 or 503 or >= 500:
                    return new SendResult(SendOutcome.Retry, RetryAfter: retryAfter, Error: error);
                default:
                    return new SendResult(SendOutcome.Fatal, Error: $"{(int)resp.StatusCode} {error}: {(string?)json?["message"]}");
            }
        }
    }
}

public sealed record ForwarderOptions
{
    public required Uri Endpoint { get; init; }
    public required string Token { get; init; }
    /// <summary>Target namespace on the server (null = the token's default namespace).</summary>
    public string? Namespace { get; init; }
    public AckLevel Ack { get; init; } = AckLevel.FsyncConfirmed;
    public int MaxBatchRecords { get; init; } = 5_000;
    public bool Gzip { get; init; } = true;
    /// <summary>Body encoding: binary mini segment (default, faster and smaller) or JSONL. Falls back to JSONL on 415.</summary>
    public byte BodyEncoding { get; init; } = IngestProtocol.EncodingLlog;
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan RetryBase { get; init; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan RetryMax { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Custom HTTP handler (proxies, client certificates, tests with fault injection).</summary>
    public HttpMessageHandler? Handler { get; init; }
    public Action<string>? Diagnostics { get; init; }
}

public enum ForwarderState { Running, Stopped, Failed }

/// <summary>
/// Ships a local store to a collector (ADR-0009). The local store is the spool: nothing is deleted locally, the
/// cursor (<c>FORWARD-*.json</c>) only records what the server acknowledged. Retries are idempotent because the server
/// deduplicates by (producer = store id, local sequence).
/// </summary>
public sealed class Forwarder : IAsyncDisposable
{
    private readonly string _root;
    private readonly ForwarderOptions _options;
    private readonly HttpClient _http;
    private readonly IngestClient _client;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly string _cursorPath;
    private readonly string _producer;
    private readonly StoreTail _tail;
    private byte _encoding;
    private long _committed;
    private long _batches, _retries, _gaps;
    private volatile string? _lastError;
    private volatile ForwarderState _state = ForwarderState.Running;

    private Forwarder(string root, ForwarderOptions options)
    {
        _root = root;
        _options = options;
        _producer = StoreWriter.ReadStoreFile(root).StoreId;
        _http = options.Handler is null ? new HttpClient() : new HttpClient(options.Handler, disposeHandler: false);
        _http.Timeout = options.RequestTimeout;
        _client = new IngestClient(_http, options.Endpoint, options.Token, options.Namespace);
        var endpointKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(options.Endpoint.AbsoluteUri + "|" + options.Namespace)))[..12].ToLowerInvariant();
        _cursorPath = Path.Combine(root, $"FORWARD-{endpointKey}.json");
        _committed = (long)LoadCursor();
        _tail = new StoreTail(root);
        _encoding = options.BodyEncoding;
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>Starts shipping the store at <paramref name="storeRoot"/> (it must already exist).</summary>
    public static Forwarder Start(string storeRoot, ForwarderOptions options) => new(storeRoot, options);

    /// <summary>Highest local sequence acknowledged by the server.</summary>
    public ulong CommittedThrough => (ulong)Interlocked.Read(ref _committed);
    public ForwarderState State => _state;
    public string? LastError => _lastError;
    public long BatchesSent => Interlocked.Read(ref _batches);
    public long Retries => Interlocked.Read(ref _retries);
    public long GapsReported => Interlocked.Read(ref _gaps);
    /// <summary>Frames decoded incrementally and full scans done by the local reader (diagnostics).</summary>
    public (long Frames, long FullScans) ReaderStats => (_tail.FramesRead, _tail.FullScans);

    /// <summary>Waits until everything currently in the local store up to <paramref name="localSequence"/> is acknowledged.</summary>
    public async Task<bool> WaitForAsync(ulong localSequence, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (CommittedThrough < localSequence)
        {
            if (_state != ForwarderState.Running || DateTime.UtcNow > deadline) return false;
            await Task.Delay(20).ConfigureAwait(false);
        }
        return true;
    }

    private ulong LoadCursor()
    {
        try
        {
            if (!File.Exists(_cursorPath)) return 0;
            var n = JsonNode.Parse(File.ReadAllText(_cursorPath));
            return ulong.Parse((string)n!["committed_through"]!, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is IOException or FormatException or System.Text.Json.JsonException or NullReferenceException)
        {
            // A lost or damaged cursor is harmless: resending is idempotent, the server answers "duplicate".
            Diagnose("cursor unreadable, restarting from 0: " + ex.Message);
            return 0;
        }
    }

    private void SaveCursor(ulong committed)
    {
        var json = new JsonObject
        {
            ["endpoint"] = _options.Endpoint.AbsoluteUri,
            ["producer"] = _producer,
            ["committed_through"] = committed.ToString(CultureInfo.InvariantCulture),
        }.ToJsonString();
        var tmp = _cursorPath + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(Encoding.UTF8.GetBytes(json));
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, _cursorPath, overwrite: true);
    }

    private void Diagnose(string message)
    {
        try { _options.Diagnostics?.Invoke("[LatticeLog.Forwarder] " + message); } catch { /* never break the loop */ }
    }

    /// <summary>
    /// Reads up to <paramref name="max"/> records after <paramref name="after"/> from the local store, incrementally
    /// (<see cref="StoreTail"/>: only frames written since the last poll are decoded). <paramref name="gap"/> is true
    /// when the run does not start at <paramref name="after"/> + 1 (records lost locally, e.g. a quarantined segment).
    /// </summary>
    private List<LogRecord> ReadAfter(ulong after, int max, out bool gap) => _tail.ReadAfter(after, max, out gap);

    private async Task LoopAsync()
    {
        var ct = _stop.Token;
        var attempt = 0;
        var maxRecords = _options.MaxBatchRecords;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var after = CommittedThrough;
                var batch = ReadAfter(after, maxRecords, out var gap);
                if (batch.Count == 0)
                {
                    await Task.Delay(_options.PollInterval, ct).ConfigureAwait(false);
                    continue;
                }
                if (gap)
                {
                    Interlocked.Increment(ref _gaps);
                    Diagnose($"local records {after + 1}..{batch[0].Sequence - 1} are missing from the store; sending with gap_acknowledged");
                }
                var envelope = IngestProtocol.EncodeBatch(_producer, batch, _options.Gzip, gap, _encoding);
                var result = await _client.SendAsync(envelope, _options.Ack, ct).ConfigureAwait(false);
                switch (result.Outcome)
                {
                    case SendOutcome.Acknowledged:
                        attempt = 0;
                        maxRecords = _options.MaxBatchRecords;
                        Interlocked.Increment(ref _batches);
                        var ack = result.Ack!;
                        if (ack.Ack < _options.Ack)
                        {
                            // The server could not reach the requested level: do not advance, retry later.
                            _lastError = $"server acknowledged only {ack.Ack}";
                            await BackoffAsync(++attempt, null, ct).ConfigureAwait(false);
                            break;
                        }
                        if (ack.CommittedThrough > after)
                        {
                            SaveCursor(ack.CommittedThrough);
                            Interlocked.Exchange(ref _committed, (long)ack.CommittedThrough);
                        }
                        _lastError = null;
                        break;
                    case SendOutcome.SequenceGap:
                        // The server has less (or more) than our cursor says: continue from its truth.
                        Diagnose($"server committed_through {result.CommittedThrough}, local cursor {after}: resyncing");
                        SaveCursor(result.CommittedThrough);
                        Interlocked.Exchange(ref _committed, (long)result.CommittedThrough);
                        break;
                    case SendOutcome.UnsupportedEncoding when _encoding != IngestProtocol.EncodingJsonl:
                        // Older server (negotiation, P16-T09): JSONL is accepted by every v1 server.
                        Diagnose("server does not accept the binary body; falling back to latticelog.jsonl");
                        _encoding = IngestProtocol.EncodingJsonl;
                        break;
                    case SendOutcome.UnsupportedEncoding:
                        _lastError = result.Error;
                        _state = ForwarderState.Failed;
                        Diagnose("stopped: " + result.Error);
                        return;
                    case SendOutcome.TooLarge:
                        if (maxRecords == 1) throw new InvalidOperationException("server rejects even a single record as too large: " + result.Error);
                        maxRecords = Math.Max(1, batch.Count / 2);
                        Diagnose($"batch too large, splitting to {maxRecords} records");
                        break;
                    case SendOutcome.Retry:
                        Interlocked.Increment(ref _retries);
                        _lastError = result.Error;
                        await BackoffAsync(++attempt, result.RetryAfter, ct).ConfigureAwait(false);
                        break;
                    case SendOutcome.Fatal:
                        _lastError = result.Error;
                        _state = ForwarderState.Failed;
                        Diagnose("stopped: " + result.Error);
                        return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _state = ForwarderState.Failed;
            Diagnose("stopped: " + ex);
            return;
        }
        _state = ForwarderState.Stopped;
    }

    /// <summary>Exponential backoff with full jitter, capped; a server <c>Retry-After</c> is a lower bound.</summary>
    private Task BackoffAsync(int attempt, TimeSpan? retryAfter, CancellationToken ct)
    {
        var cap = Math.Min(_options.RetryMax.TotalMilliseconds, _options.RetryBase.TotalMilliseconds * Math.Pow(2, Math.Min(attempt, 20)));
        var delay = TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * cap);
        if (retryAfter is { } ra && ra > delay) delay = ra;
        return Task.Delay(delay, ct);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _http.Dispose();
        _stop.Dispose();
        _tail.Dispose();
    }
}
