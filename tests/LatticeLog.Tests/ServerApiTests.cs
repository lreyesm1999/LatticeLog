using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using LatticeLog.Ingest;
using LatticeLog.Server;
using LatticeLog.Storage;
using LatticeLog.Storage.Json;

namespace LatticeLog.Tests;

/// <summary>Server v1 read API (P17, specs/server-api.md): namespaces, scopes, signed cursors, endpoints, limits.</summary>
public class ServerApiTests
{
    private static readonly TokenInfo[] Tokens =
    [
        TokenStore.Create("acme-writer", "acme-w", TokenScopes.Ingest, ["acme/prod"]),
        TokenStore.Create("globex-writer", "globex-w", TokenScopes.Ingest, ["globex/dev"]),
        TokenStore.Create("acme-reader", "acme-r", TokenScopes.Read, ["acme/prod"]),
        TokenStore.Create("acme-reader-2", "acme-r2", TokenScopes.Read, ["acme/prod"]),
        TokenStore.Create("ops", "ops-secret", TokenScopes.Read | TokenScopes.Admin, ["*"]),
    ];

    private static async Task<LatticeServer> Start(string data, QueryLimits? query = null, string? backups = null)
        => await LatticeServer.StartAsync(new ServerOptions
        {
            BackupDirectory = backups,
            DataDirectory = data,
            Tokens = TokenStore.FromTokens(Tokens),
            IngestTemplate = new IngestServiceOptions { StoreDirectory = "", Store = new StoreWriterOptions { MinFreeDiskBytes = 0, SegmentMaxBytes = 8 * 1024 } },
            Query = query ?? new QueryLimits(),
            Url = "http://127.0.0.1:0",
        });

    /// <summary>n records: event "order.placed"/"order.failed" (every 10th, ERROR), trace per 5 records, attribute i.</summary>
    private static List<LogRecord> Orders(int n, string service)
    {
        var ctx = new ContextSnapshot(ContextKind.Resource, null, [new("service.name", service)], null);
        var list = new List<LogRecord>(n);
        Span<byte> t = stackalloc byte[16];
        for (var i = 0; i < n; i++)
        {
            var failed = i % 10 == 9;
            BitConverter.TryWriteBytes(t, (long)(i / 5) + 1);
            BitConverter.TryWriteBytes(t[8..], (long)service.Length);
            list.Add(new LogRecord
            {
                TimestampUnixNanos = 1_763_000_000_000_000_000 + i * 1_000_000L,
                Severity = failed ? Severity.Error : Severity.Info,
                EventName = failed ? "order.failed" : "order.placed",
                Attributes = [new("i", i), new("password", "s3cr3t-" + i)],
                TraceId = TraceId.FromBytes(t),
                Context = ctx,
                Sequence = (ulong)i + 1,
            });
        }
        return list;
    }

    private static async Task Ingest(Uri server, string token, List<LogRecord> records)
    {
        using var http = new HttpClient();
        var client = new IngestClient(http, server, token);
        var producer = Guid.NewGuid().ToString("N");
        for (var i = 0; i < records.Count; i += 100)
        {
            var r = await client.SendAsync(IngestProtocol.EncodeBatch(producer, records.GetRange(i, Math.Min(100, records.Count - i))), AckLevel.FsyncConfirmed, default);
            Assert.Equal(SendOutcome.Acknowledged, r.Outcome);
        }
    }

    private static async Task<(HttpStatusCode Status, JsonNode? Json, HttpResponseMessage Raw, string Text)> Get(Uri server, string path, string? token)
    {
        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(server, path));
        if (token is not null) req.Headers.Authorization = new("Bearer", token);
        var resp = await http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        JsonNode? json = null;
        if (resp.Content.Headers.ContentType?.MediaType == "application/json") json = JsonNode.Parse(text);
        return (resp.StatusCode, json, resp, text);
    }

    private static string Q(string s) => Uri.EscapeDataString(s);

    [Fact]
    public async Task Namespaces_are_isolated_and_scopes_are_enforced()
    {
        var data = TestData.TempDir();
        await using var server = await Start(data);
        await Ingest(server.BaseAddress, "acme-w", Orders(300, "acme-orders"));
        await Ingest(server.BaseAddress, "globex-w", Orders(120, "globex-billing"));

        var acme = await Get(server.BaseAddress, $"/v1/search?ns={Q("acme/prod")}&limit=1000", "acme-r");
        Assert.Equal(HttpStatusCode.OK, acme.Status);
        Assert.Equal(300, acme.Json!["events"]!.AsArray().Count);
        Assert.All(acme.Json["events"]!.AsArray(), e => Assert.Equal("acme-orders", (string?)e!["service"]));

        var ops = await Get(server.BaseAddress, $"/v1/search?ns={Q("globex/dev")}&limit=1000", "ops-secret");
        Assert.Equal(120, ops.Json!["events"]!.AsArray().Count); // wildcard namespaces: sees globex, still only globex

        Assert.Equal(HttpStatusCode.Forbidden, (await Get(server.BaseAddress, $"/v1/search?ns={Q("globex/dev")}", "acme-r")).Status);
        var writer = await Get(server.BaseAddress, $"/v1/search?ns={Q("acme/prod")}", "acme-w");
        Assert.Equal(HttpStatusCode.Forbidden, writer.Status);
        Assert.Equal("forbidden_scope", (string?)writer.Json!["error"]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(server.BaseAddress, "/v1/search", null)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Get(server.BaseAddress, $"/v1/search?ns={Q("../acme")}", "ops-secret")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(server.BaseAddress, $"/v1/search?ns={Q("acme/staging")}", "ops-secret")).Status);

        var list = await Get(server.BaseAddress, "/v1/namespaces", "acme-r");
        Assert.Equal(["acme/prod"], list.Json!["namespaces"]!.AsArray().Select(x => (string)x!).ToArray());
        var all = await Get(server.BaseAddress, "/v1/namespaces", "ops-secret");
        Assert.Equal(["acme/prod", "globex/dev"], all.Json!["namespaces"]!.AsArray().Select(x => (string)x!).ToArray());

        // Each namespace is its own store on disk, with its own sequences and dedup state.
        Assert.True(File.Exists(Path.Combine(data, "ns", "acme~prod", "STORE.json")));
        Assert.True(File.Exists(Path.Combine(data, "ns", "globex~dev", "STORE.json")));
    }

    [Fact]
    public async Task Signed_cursors_page_through_everything_and_reject_tampering_borrowing_and_expiry()
    {
        await using var server = await Start(TestData.TempDir(), new QueryLimits { CursorTtl = TimeSpan.FromSeconds(1) });
        await Ingest(server.BaseAddress, "acme-w", Orders(1_050, "acme-orders"));

        var seen = new HashSet<long>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await Get(server.BaseAddress, $"/v1/search?ns={Q("acme/prod")}&limit=200" + (cursor is null ? "" : "&cursor=" + Q(cursor)), "acme-r");
            Assert.Equal(HttpStatusCode.OK, page.Status);
            foreach (var e in page.Json!["events"]!.AsArray()) Assert.True(seen.Add((long)e!["attributes"]!["i"]!));
            cursor = (string?)page.Json["coverage"]!["next_cursor"];
            pages++;
        } while (cursor is not null);
        Assert.Equal(1_050, seen.Count);
        Assert.Equal(6, pages);

        var first = await Get(server.BaseAddress, $"/v1/search?ns={Q("acme/prod")}&limit=10", "acme-r");
        var c = (string)first.Json!["coverage"]!["next_cursor"]!;
        var parts = c.Split('.');
        var forged = string.Join('.', parts[0], parts[1], parts[2].Replace('A', 'B').Replace('e', 'f'), parts[3]);
        Assert.Equal(HttpStatusCode.BadRequest, (await Get(server.BaseAddress, $"/v1/search?ns={Q("acme/prod")}&limit=10&cursor={Q(forged)}", "acme-r")).Status);
        var borrowed = await Get(server.BaseAddress, $"/v1/search?ns={Q("acme/prod")}&limit=10&cursor={Q(c)}", "acme-r2");
        Assert.Equal(HttpStatusCode.BadRequest, borrowed.Status);
        Assert.Contains("signature", (string?)borrowed.Json!["message"]);
        await Task.Delay(2_100);
        var expired = await Get(server.BaseAddress, $"/v1/search?ns={Q("acme/prod")}&limit=10&cursor={Q(c)}", "acme-r");
        Assert.Equal(HttpStatusCode.BadRequest, expired.Status);
        Assert.Contains("expired", (string?)expired.Json!["message"]);
    }

    [Fact]
    public async Task Read_endpoints_answer_with_coverage_and_redaction()
    {
        var data = TestData.TempDir();
        await using var server = await Start(data);
        await Ingest(server.BaseAddress, "acme-w", Orders(400, "acme-orders"));
        string ns = "?ns=" + Q("acme/prod");

        var errors = await Get(server.BaseAddress, $"/v1/search{ns}&where={Q("level >= ERROR")}&limit=1000", "acme-r");
        Assert.Equal(40, errors.Json!["events"]!.AsArray().Count);
        var ev = errors.Json["events"]![0]!;
        Assert.Equal("[REDACTED]", (string?)ev["attributes"]!["password"]); // secrets redacted on output by default
        Assert.NotNull(ev["observed_time"]); // arrival time recorded by the server (P17-T11)

        var bad = await Get(server.BaseAddress, $"/v1/search{ns}&where={Q("level >=")}", "acme-r");
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        Assert.Equal("invalid_query", (string?)bad.Json!["error"]);

        var one = await Get(server.BaseAddress, $"/v1/events/{Q((string)ev["ref"]!)}{ns}", "acme-r");
        Assert.Equal(HttpStatusCode.OK, one.Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(server.BaseAddress, $"/v1/events/999999{ns}", "acme-r")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Get(server.BaseAddress, $"/v1/events/{Q((string)ev["ref"]!)}/related{ns}", "acme-r")).Status);

        var trace = await Get(server.BaseAddress, $"/v1/traces/{(string)ev["trace_id"]!}{ns}", "acme-r");
        Assert.Equal(HttpStatusCode.OK, trace.Status);
        Assert.Contains("order.failed", trace.Text);

        var stats = await Get(server.BaseAddress, $"/v1/stats{ns}&by=event", "acme-r");
        var groups = stats.Json!["groups"]!.AsArray().ToDictionary(g => (string)g!["key"]!, g => (long)g!["count"]!);
        Assert.Equal(360, groups["order.placed"]);
        Assert.Equal(40, groups["order.failed"]);

        foreach (var path in new[] { "/v1/overview", "/v1/error-groups", "/v1/context", "/v1/schema" })
        {
            var r = await Get(server.BaseAddress, path + ns, "acme-r");
            Assert.True(r.Status == HttpStatusCode.OK, $"{path}: {r.Status} {r.Text}");
            Assert.Contains("latticelog.evidence.v1", r.Text);
        }
        var catalog = await Get(server.BaseAddress, $"/v1/catalog{ns}", "acme-r");
        var segments = catalog.Json!["segments"]!.AsArray();
        Assert.True(segments.Count >= 2, $"{segments.Count} segment(s)"); // 8 KiB segments: rotated, sealed and active ones listed
        Assert.Equal(400, segments.Sum(s => (long?)s!["records"] ?? 0) + await UnsealedCount(data));
    }

    private static Task<long> UnsealedCount(string data)
    {
        var reader = StoreReader.Open(Path.Combine(data, "ns", "acme~prod"));
        var rep = new ScanReport();
        return Task.FromResult((long)reader.ReadBlocks(rep, (_, idx) => idx is null).Sum(b => b.Records.Count));
    }

    [Fact]
    public async Task Export_pages_are_lossless_latticelog_jsonl()
    {
        var data = TestData.TempDir();
        await using var server = await Start(data);
        var tricky = new TestData(17).Records(700); // NaN payloads, lone surrogates, binary, decimals, deep maps
        for (var i = 0; i < tricky.Count; i++) tricky[i].Sequence = (ulong)i + 1;
        await Ingest(server.BaseAddress, "acme-w", tricky);

        var exported = new List<LogRecord>();
        string? cursor = null;
        do
        {
            var page = await Get(server.BaseAddress, $"/v1/export?ns={Q("acme/prod")}&limit=250" + (cursor is null ? "" : "&cursor=" + Q(cursor)), "acme-r");
            Assert.Equal(HttpStatusCode.OK, page.Status);
            Assert.Equal("false", page.Raw.Headers.GetValues("LatticeLog-Partial").Single());
            exported.AddRange(new JsonlExchangeReader().Read(new StringReader(page.Text)));
            cursor = page.Raw.Headers.TryGetValues("LatticeLog-Next-Cursor", out var v) ? v.Single() : null;
        } while (cursor is not null);

        var stored = StoreReader.Open(Path.Combine(data, "ns", "acme~prod")).ReadAll(new ScanReport()).ToList();
        Assert.Equal(stored.Count, exported.Count);
        for (var i = 0; i < stored.Count; i++) Assert.True(stored[i].Equals(exported[i]), TestData.Describe(stored[i], exported[i]));
    }

    [Fact]
    public async Task Query_limits_partial_results_per_token_concurrency_and_admin_health()
    {
        var data = TestData.TempDir();
        await using (var server = await Start(data, new QueryLimits { MaxScannedRecords = 50 }))
        {
            await Ingest(server.BaseAddress, "acme-w", Orders(500, "acme-orders"));
            var r = await Get(server.BaseAddress, $"/v1/search?ns={Q("acme/prod")}&where={Q("attr.i = 499")}", "acme-r");
            Assert.Equal(HttpStatusCode.OK, r.Status);
            Assert.True((bool)r.Json!["coverage"]!["partial_results"]!); // stopped at the scan limit and says so

            var anonymous = await Get(server.BaseAddress, "/v1/health", null);
            Assert.Equal("ok", (string?)anonymous.Json!["status"]);
            Assert.Null(anonymous.Json["namespaces"]); // no details without an admin token
            var admin = await Get(server.BaseAddress, "/v1/health", "ops-secret");
            Assert.Equal("500", (string?)admin.Json!["namespaces"]!["acme/prod"]!["last_sequence"]);
        }
        await using (var server = await Start(data, new QueryLimits { PerTokenConcurrentQueries = 0 }))
        {
            var r = await Get(server.BaseAddress, $"/v1/search?ns={Q("acme/prod")}", "acme-r");
            Assert.Equal((HttpStatusCode)429, r.Status);
            Assert.NotNull(r.Raw.Headers.RetryAfter);
        }
    }

    private static async Task<(HttpStatusCode Status, JsonNode? Json)> Post(Uri server, string path, string token)
    {
        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(server, path));
        req.Headers.Authorization = new("Bearer", token);
        using var resp = await http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, text.Length > 0 ? JsonNode.Parse(text) : null);
    }

    [Fact]
    public async Task Online_retention_compaction_and_verify_keep_dedup_and_are_audited()
    {
        var data = TestData.TempDir();
        var producer = Guid.NewGuid().ToString("N");
        var orders = Orders(1_000, "acme-orders"); // event times in 2025: older than 30 days
        await using (var server = await Start(data))
        {
            using var http = new HttpClient();
            var client = new IngestClient(http, server.BaseAddress, "acme-w");
            for (var i = 0; i < orders.Count; i += 100)
                Assert.Equal("stored", (await client.SendAsync(IngestProtocol.EncodeBatch(producer, orders.GetRange(i, 100)), AckLevel.FsyncConfirmed, default)).Ack!.Status);
            var ns = "?ns=" + Q("acme/prod");

            Assert.Equal(HttpStatusCode.Forbidden, (await Post(server.BaseAddress, "/v1/admin/retention" + ns + "&max_age=30d", "acme-r")).Status);

            var compact = await Post(server.BaseAddress, "/v1/admin/compact" + ns + "&policy=compact", "ops-secret");
            Assert.Equal(HttpStatusCode.OK, compact.Status);
            Assert.NotEmpty(compact.Json!["compactions"]!.AsArray());
            var afterCompact = await Get(server.BaseAddress, $"/v1/search{ns}&limit=1000", "acme-r");
            Assert.Equal(1_000, afterCompact.Json!["events"]!.AsArray().Count); // nothing lost or doubled by compaction

            var dry = await Post(server.BaseAddress, "/v1/admin/retention" + ns + "&max_age=30d&dry_run=true", "ops-secret");
            Assert.True((bool)dry.Json!["dry_run"]!);
            var retention = await Post(server.BaseAddress, "/v1/admin/retention" + ns + "&max_age=30d", "ops-secret");
            Assert.Equal(HttpStatusCode.OK, retention.Status);
            var deleted = (long)retention.Json!["records"]!;
            Assert.True(deleted > 0);
            var remaining = await Get(server.BaseAddress, $"/v1/search{ns}&limit=1000", "acme-r");
            Assert.Equal(1_000 - deleted, remaining.Json!["events"]!.AsArray().Count); // only the active segment survives

            // The producer resends everything (lost its cursor): old records must not come back.
            var resend = await client.SendAsync(IngestProtocol.EncodeBatch(producer, orders.GetRange(0, 100)), AckLevel.FsyncConfirmed, default);
            Assert.Equal("duplicate", resend.Ack!.Status);

            var verify = await Post(server.BaseAddress, "/v1/admin/verify" + ns, "ops-secret");
            Assert.True((bool)verify.Json!["ok"]!);
            await Get(server.BaseAddress, "/v1/search" + ns, "wrong-token");
        }
        // After a restart too (dedup comes from the checkpoint written before deleting).
        await using (var server = await Start(data))
        {
            using var http = new HttpClient();
            var client = new IngestClient(http, server.BaseAddress, "acme-w");
            Assert.Equal("duplicate", (await client.SendAsync(IngestProtocol.EncodeBatch(producer, orders.GetRange(0, 100)), AckLevel.FsyncConfirmed, default)).Ack!.Status);
        }
        var auditFile = Directory.GetFiles(Path.Combine(data, "audit"), "audit-*.jsonl").Single();
        var (events, corrupt) = AuditLog.ReadFile(auditFile);
        Assert.Equal(0, corrupt);
        var actions = events.Select(e => ((string)e["action"]!, (string)e["outcome"]!)).ToList();
        Assert.Contains(("admin.compact", "ok"), actions);
        Assert.Contains(("admin.retention", "ok"), actions);
        Assert.Contains(("admin.verify", "ok"), actions);
        Assert.Contains(("read.search", "ok"), actions);
        Assert.Contains(actions, a => a.Item2 == "forbidden"); // the reader trying to administer
        Assert.Contains(actions, a => a.Item2 == "unauthorized");
        Assert.All(events, e => Assert.DoesNotContain("s3cr3t", e.ToJsonString())); // no log content in the audit trail
    }

    [Fact]
    public async Task Erase_removes_matching_records_everywhere_in_the_store_and_they_cannot_come_back()
    {
        var data = TestData.TempDir();
        var producer = Guid.NewGuid().ToString("N");
        var orders = Orders(600, "acme-orders");
        await using var server = await Start(data);
        using var http = new HttpClient();
        var client = new IngestClient(http, server.BaseAddress, "acme-w");
        for (var i = 0; i < orders.Count; i += 100)
            Assert.Equal("stored", (await client.SendAsync(IngestProtocol.EncodeBatch(producer, orders.GetRange(i, 100)), AckLevel.FsyncConfirmed, default)).Ack!.Status);
        var ns = "?ns=" + Q("acme/prod");
        var filter = "&where=" + Q("attr.i < 50 OR attr.i >= 590"); // old (sealed) and newest (active segment) records

        Assert.Equal(HttpStatusCode.BadRequest, (await Post(server.BaseAddress, "/v1/admin/erase" + ns, "ops-secret")).Status);
        var dry = await Post(server.BaseAddress, "/v1/admin/erase" + ns + filter + "&dry_run=true", "ops-secret");
        Assert.Equal(60, (long)dry.Json!["records"]!);
        Assert.Equal(600, (await Get(server.BaseAddress, $"/v1/search{ns}&limit=1000", "acme-r")).Json!["events"]!.AsArray().Count);

        var erase = await Post(server.BaseAddress, "/v1/admin/erase" + ns + filter, "ops-secret");
        Assert.Equal(HttpStatusCode.OK, erase.Status);
        Assert.Equal(60, (long)erase.Json!["records"]!);
        var left = (await Get(server.BaseAddress, $"/v1/search{ns}&limit=1000", "acme-r")).Json!["events"]!.AsArray();
        Assert.Equal(540, left.Count);
        Assert.All(left, e => Assert.InRange((long)e!["attributes"]!["i"]!, 50, 589));
        // Not even in the raw files: every segment is decoded directly.
        var raw = StoreReader.Open(Path.Combine(data, "ns", "acme~prod")).ReadAll(new ScanReport()).ToList();
        Assert.DoesNotContain(raw, r => r.Attributes[0].Value.AsInt64() is < 50 or >= 590);
        // A producer resending the erased records does not bring them back.
        Assert.Equal("duplicate", (await client.SendAsync(IngestProtocol.EncodeBatch(producer, orders.GetRange(0, 100)), AckLevel.FsyncConfirmed, default)).Ack!.Status);
        Assert.Equal(540, (await Get(server.BaseAddress, $"/v1/search{ns}&limit=1000", "acme-r")).Json!["events"]!.AsArray().Count);
        // Ingestion keeps working after the active segment was sealed for the erase.
        Assert.Equal(SendOutcome.Acknowledged, (await client.SendAsync(IngestProtocol.EncodeBatch(producer, Orders(610, "acme-orders").GetRange(600, 10)), AckLevel.FsyncConfirmed, default)).Outcome);
    }

    [Fact]
    public async Task Repeated_bad_tokens_from_one_address_are_throttled()
    {
        await using var server = await Start(TestData.TempDir());
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 25; i++) statuses.Add((await Get(server.BaseAddress, "/v1/search", "guess-" + i)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, statuses[0]);
        Assert.Equal((HttpStatusCode)429, statuses[^1]);
        // Even the right token waits out the window: the brake is per address, not per token.
        Assert.Equal((HttpStatusCode)429, (await Get(server.BaseAddress, $"/v1/search?ns={Q("acme/prod")}", "acme-r")).Status);
    }

    [Fact]
    public async Task Console_metrics_and_backup()
    {
        var data = TestData.TempDir();
        var backups = TestData.TempDir();
        await using var server = await Start(data, backups: backups);
        await Ingest(server.BaseAddress, "acme-w", Orders(250, "acme-orders"));

        // Console: static, served with a strict CSP and no framing; the page itself needs no token.
        var page = await Get(server.BaseAddress, "/ui", null);
        Assert.True(page.Status == HttpStatusCode.OK, page.Text);
        Assert.Contains("<script src=\"/ui/app.js\">", page.Text);
        Assert.Contains("default-src 'self'", page.Raw.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("DENY", page.Raw.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("nosniff", page.Raw.Headers.GetValues("X-Content-Type-Options").Single());
        var js = await Get(server.BaseAddress, "/ui/app.js", null);
        Assert.Equal("text/javascript", js.Raw.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("innerHTML", js.Text);

        // Metrics: admin only, Prometheus text with per-namespace series.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(server.BaseAddress, "/metrics", "acme-r")).Status);
        var metrics = await Get(server.BaseAddress, "/metrics", "ops-secret");
        Assert.Equal(HttpStatusCode.OK, metrics.Status);
        Assert.Contains("# TYPE latticelog_store_last_sequence gauge", metrics.Text);
        Assert.Contains("latticelog_store_last_sequence{namespace=\"acme/prod\"} 250", metrics.Text);
        Assert.Contains("latticelog_ingest_records_stored_total{namespace=\"acme/prod\"} 250", metrics.Text);

        // Backup: admin only, into the configured directory, verified; the copy reads back every record.
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(server.BaseAddress, $"/v1/admin/backup?ns={Q("acme/prod")}", "acme-r")).Status);
        var bk = await Post(server.BaseAddress, $"/v1/admin/backup?ns={Q("acme/prod")}", "ops-secret");
        Assert.Equal(HttpStatusCode.OK, bk.Status);
        Assert.True((bool)bk.Json!["verified"]!);
        Assert.Equal(250, (long)bk.Json["records"]!);
        var copy = Directory.GetDirectories(Directory.GetDirectories(backups).Single()).Single();
        Assert.Equal(250, StoreReader.Open(copy).ReadAll(new ScanReport()).Count());
    }
}
