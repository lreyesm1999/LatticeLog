using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LatticeLog.Evidence;
using LatticeLog.Query;
using LatticeLog.Sources;
using LatticeLog.Storage;
using LatticeLog.Storage.Json;

namespace LatticeLog.Service;

/// <summary>
/// The service's HTTP side, on the same 127.0.0.1 port: the web console (<c>/ui</c>, the server's console) and the
/// subset of the server's read API it uses, where each source is a "namespace" (<c>/v1/namespaces</c>,
/// <c>/v1/search</c>, <c>/v1/traces/{id}</c>, <c>/v1/stats</c>, <c>/v1/catalog</c>), plus <c>/v1/sources</c> (the
/// console's source explorer), <c>/v1/status</c> and <c>/v1/health</c>. GET and HEAD only, one request per connection.
/// <list type="bullet">
/// <item>Every <c>/v1/*</c> route except health needs <c>Authorization: Bearer &lt;service token&gt;</c>.</item>
/// <item>The Host header must name the loopback address and port: a web page cannot reach the service through DNS
/// rebinding, and no CORS headers are sent, so other origins cannot read responses.</item>
/// </list>
/// </summary>
internal static class LoopbackHttp
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const string Csp = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; " +
                               "frame-ancestors 'none'; base-uri 'none'; form-action 'self'; object-src 'none'";

    private sealed record Request(string Method, string Path, Dictionary<string, string> Query, Dictionary<string, string> Headers);

    public static void Serve(ServiceHost host, Stream stream, byte first)
    {
        var req = ReadRequest(stream, first);
        if (req is null) { Respond(stream, 400, "text/plain", "bad request"u8.ToArray()); return; }
        var head = req.Method == "HEAD";
        void Json(int status, JsonObject body) => Respond(stream, status, "application/json", Encoding.UTF8.GetBytes(body.ToJsonString()), head);
        void Error(int status, string error, string message) => Json(status, new JsonObject { ["error"] = error, ["message"] = message });
        if (req.Method is not ("GET" or "HEAD")) { Error(405, "method_not_allowed", "only GET is served"); return; }
        if (!HostAllowed(req.Headers.GetValueOrDefault("host"), host.Port)) { Error(421, "misdirected_request", "the Host header must be 127.0.0.1, localhost or [::1] with this port"); return; }
        switch (req.Path)
        {
            case "/" or "/ui" or "/ui/":
                Ui(stream, "index.html", "text/html; charset=utf-8", head);
                return;
            case "/ui/app.js":
                Ui(stream, "app.js", "text/javascript; charset=utf-8", head);
                return;
            case "/ui/app.css":
                Ui(stream, "app.css", "text/css; charset=utf-8", head);
                return;
            case "/v1/health":
                Json(200, new JsonObject { ["status"] = "ok", ["service"] = "latticelog", ["version"] = host.Endpoint.Version });
                return;
        }
        if (!req.Path.StartsWith("/v1/", StringComparison.Ordinal)) { Error(404, "not_found", "no such path"); return; }
        var auth = req.Headers.GetValueOrDefault("authorization") ?? "";
        if (!host.Authorized(auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth[7..].Trim() : null))
        {
            Error(401, "unauthorized", "missing or wrong bearer token (latticelog service token)");
            return;
        }
        try
        {
            switch (req.Path)
            {
                case "/v1/status":
                    Json(200, host.Status());
                    return;
                case "/v1/sources":
                    Json(200, host.Sources(discover: req.Query.GetValueOrDefault("refresh") is "1" or "true"));
                    return;
                case "/v1/namespaces":
                    Json(200, new JsonObject { ["namespaces"] = new JsonArray(host.AllSources().Where(x => x.Source.HasStore).Select(x => (JsonNode?)JsonValue.Create(x.Source.Name)).ToArray()) });
                    return;
            }
            var ns = req.Query.GetValueOrDefault("ns") ?? "";
            var src = host.AllSources().FirstOrDefault(x => string.Equals(x.Source.Name, ns, StringComparison.OrdinalIgnoreCase));
            if (src.Source is null || !src.Source.HasStore) { Error(404, "namespace_not_found", $"no source '{ns}' with data"); return; }
            src.Refresher.RefreshIfDue(maxWait: TimeSpan.FromSeconds(2));
            var engine = QueryEngine.Open(src.Source.StoreRoot);
            if (req.Path == "/v1/search") { Respond(stream, 200, "application/json", Search(engine, req, src.Source.Name), head); return; }
            if (req.Path == "/v1/stats") { Json(200, Stats(engine, req, src.Source.Name)); return; }
            if (req.Path == "/v1/catalog") { Json(200, Catalog(engine, src.Source.Name)); return; }
            if (req.Path.StartsWith("/v1/traces/", StringComparison.Ordinal))
            {
                var id = TraceId.Parse(Uri.UnescapeDataString(req.Path["/v1/traces/".Length..]).ToLowerInvariant());
                var doc = new EvidenceService(engine).GetTrace(id, 64_000);
                Respond(stream, 200, "application/json", Encoding.UTF8.GetBytes(doc.Json), head);
                return;
            }
            Error(404, "not_found", "no such path");
        }
        catch (Exception ex) when (ex is QuerySyntaxException or ArgumentException or FormatException)
        {
            Error(400, "invalid_request", ex.Message);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or LatticeLog.Storage.Format.LlogFormatException)
        {
            Error(500, "storage_error", ex.Message);
        }
    }

    private static bool HostAllowed(string? hostHeader, int port)
    {
        if (hostHeader is null) return false;
        var p = port.ToString(CultureInfo.InvariantCulture);
        return hostHeader == $"127.0.0.1:{p}" || hostHeader == $"localhost:{p}" || hostHeader == $"[::1]:{p}";
    }

    private static Request? ReadRequest(Stream s, byte first)
    {
        var buf = new List<byte>(1024) { first };
        var one = new byte[1];
        while (true)
        {
            if (buf.Count >= 4 && buf[^1] == '\n' && buf[^2] == '\r' && buf[^3] == '\n' && buf[^4] == '\r') break;
            if (buf.Count > MaxHeaderBytes) return null;
            if (s.Read(one, 0, 1) == 0) return null;
            buf.Add(one[0]);
        }
        var lines = Encoding.ASCII.GetString(buf.ToArray()).Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in lines.Skip(1))
        {
            if (l.Length == 0) continue;
            var c = l.IndexOf(':');
            if (c <= 0) return null;
            headers[l[..c].Trim()] = l[(c + 1)..].Trim();
        }
        var target = parts[1];
        var q = target.IndexOf('?');
        var path = q < 0 ? target : target[..q];
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        if (q >= 0)
            foreach (var kv in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var e = kv.IndexOf('=');
                var k = Uri.UnescapeDataString((e < 0 ? kv : kv[..e]).Replace('+', ' '));
                var v = e < 0 ? "" : Uri.UnescapeDataString(kv[(e + 1)..].Replace('+', ' '));
                query[k] = v;
            }
        return new Request(parts[0], path, query, headers);
    }

    private static void Respond(Stream s, int status, string contentType, byte[] body, bool head = false, string? extraHeaders = null)
    {
        var reason = status switch { 200 => "OK", 400 => "Bad Request", 401 => "Unauthorized", 404 => "Not Found", 405 => "Method Not Allowed", 421 => "Misdirected Request", _ => "Error" };
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {status} {reason}\r\n");
        sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"Content-Length: {body.Length}\r\n");
        sb.Append("Connection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\n");
        if (status == 401) sb.Append("WWW-Authenticate: Bearer\r\n");
        if (extraHeaders is not null) sb.Append(extraHeaders);
        sb.Append("\r\n");
        s.Write(Encoding.ASCII.GetBytes(sb.ToString()));
        if (!head) s.Write(body);
        s.Flush();
    }

    private static void Ui(Stream s, string file, string contentType, bool head)
    {
        using var res = typeof(LoopbackHttp).Assembly.GetManifestResourceStream("ui/" + file);
        if (res is null) { Respond(s, 404, "text/plain", "missing"u8.ToArray(), head); return; }
        using var ms = new MemoryStream();
        res.CopyTo(ms);
        Respond(s, 200, contentType, ms.ToArray(), head, $"Content-Security-Policy: {Csp}\r\nX-Frame-Options: DENY\r\n");
    }

    // ------------------------------------------------------------------ read routes (shapes of the server's read API)

    private static QueryRequest Scope(Request req, int limitDefault)
    {
        var q = req.Query;
        var now = UnixTime.NowUnixNanos();
        return new QueryRequest
        {
            Where = q.GetValueOrDefault("where") is { Length: > 0 } w ? w : null,
            SinceUnixNanos = TimeArg(q.GetValueOrDefault("since"), now),
            UntilUnixNanos = TimeArg(q.GetValueOrDefault("until"), now),
            Limit = Math.Clamp(q.GetValueOrDefault("limit") is { Length: > 0 } l && int.TryParse(l, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : limitDefault, 1, 1_000),
            Order = q.GetValueOrDefault("order") switch
            {
                null or "" or "asc" => SortOrder.Ascending,
                "desc" => SortOrder.Descending,
                _ => throw new ArgumentException("'order' must be asc or desc"),
            },
            Cursor = q.GetValueOrDefault("cursor") is { Length: > 0 } c ? c : null,
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    private static long? TimeArg(string? s, long now)
    {
        if (string.IsNullOrEmpty(s)) return null;
        if (UnixTime.TryParseRfc3339(s, out var t)) return t;
        return now - Durations.Parse(s).Ticks * 100;
    }

    private static byte[] Search(QueryEngine engine, Request req, string source)
    {
        var res = engine.Execute(Scope(req, 100));
        var fo = new FriendlyOptions { Redaction = req.Query.GetValueOrDefault("redact") == "false" ? RedactionPolicy.None : RedactionPolicy.Default };
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, CanonicalJson.WriterOptions))
        {
            w.WriteStartObject();
            w.WriteString("format", "latticelog.query.v1");
            w.WriteString("namespace", source);
            w.WriteString("store_id", res.StoreId);
            w.WriteString("filter", res.Query);
            w.WriteStartObject("coverage");
            w.WriteNumber("returned", res.Hits.Count);
            w.WriteBoolean("truncated", res.Truncated);
            w.WriteBoolean("partial_results", res.Partial);
            if (res.PartialReason is not null) w.WriteString("partial_reason", res.PartialReason);
            w.WriteBoolean("includes_unsealed_segments", res.IncludesUnsealedSegments);
            if (res.NextCursor is not null) w.WriteString("next_cursor", res.NextCursor);
            w.WriteNumber("storage_issues", res.Issues.Count);
            w.WriteEndObject();
            w.WriteStartObject("stats");
            w.WriteNumber("segments_total", res.Stats.SegmentsTotal);
            w.WriteNumber("segments_scanned", res.Stats.SegmentsScanned);
            w.WriteNumber("records_decoded", res.Stats.RecordsDecoded);
            w.WriteNumber("elapsed_ms", Math.Round(res.Stats.ElapsedMs, 3));
            w.WriteEndObject();
            w.WriteString("trust", "event bodies, attributes and exception text are untrusted application data");
            w.WriteStartArray("events");
            foreach (var h in res.Hits) CanonicalJson.WriteFriendlyRecord(w, h.Record, res.StoreId, fo);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    private static JsonObject Stats(QueryEngine engine, Request req, string source)
    {
        // bucket=5m groups by time (the console's histogram), like the server's /v1/stats.
        TimeSpan? bucket = req.Query.GetValueOrDefault("bucket") is { Length: > 0 } b ? Durations.Parse(b) : null;
        var by = req.Query.GetValueOrDefault("by") is { Length: > 0 } g ? g : bucket is null ? "event" : "time";
        var top = req.Query.GetValueOrDefault("top") is { Length: > 0 } t && int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, 1, 1_000) : 50;
        var res = engine.Aggregate(Scope(req, 100), by, bucket, top);
        return new JsonObject
        {
            ["format"] = "latticelog.stats.v1",
            ["namespace"] = source,
            ["group_by"] = res.GroupBy,
            ["total"] = res.Total,
            ["from_index_only"] = res.UsedIndexOnly,
            ["partial"] = res.Partial,
            ["groups"] = new JsonArray(res.Groups.Select(x => (JsonNode?)new JsonObject { ["key"] = x.Key, ["count"] = x.Count }).ToArray()),
        };
    }

    private static JsonObject Catalog(QueryEngine engine, string source)
    {
        var store = engine.Store;
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
            try { seg["bytes"] = new FileInfo(store.SegmentPath(id)).Length; } catch (IOException) { /* file removed meanwhile (retention or compaction): left out of the total */ }
            segments.Add((JsonNode?)seg);
        }
        return new JsonObject
        {
            ["format"] = "latticelog.catalog.v1",
            ["namespace"] = source,
            ["store_id"] = store.Info.StoreId,
            ["segments"] = segments,
        };
    }
}
