using System.Buffers.Binary;
using System.IO.Compression;
using LatticeLog.Core;
using LatticeLog.Ingest;
using LatticeLog.Server;
using LatticeLog.Storage;
using LatticeLog.Storage.Format;

namespace LatticeLog.Tests;

/// <summary>Remote ingestion (P16, ADR-0009, specs/ingest-protocol.md): network faults, crashes, dedup and security.</summary>
public class IngestTests
{
    private const string Secret = "test-secret-token";

    /// <summary>Injects network faults per request: lose the request, lose the response after the server handled it, or delay.</summary>
    private sealed class FaultHandler(double failBefore, double dropResponse, double delay, int seed) : DelegatingHandler(new SocketsHttpHandler())
    {
        private readonly Random _rnd = new(seed);
        public int Injected;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            double roll;
            lock (_rnd) roll = _rnd.NextDouble();
            if (roll < failBefore) { Interlocked.Increment(ref Injected); throw new HttpRequestException("injected: connection refused"); }
            if (roll < failBefore + delay) await Task.Delay(30, ct);
            var response = await base.SendAsync(request, ct);
            if (roll >= failBefore + delay && roll < failBefore + delay + dropResponse)
            {
                // The server processed the batch, the client never hears back: it must retry and get "duplicate".
                await response.Content.ReadAsByteArrayAsync(ct);
                response.Dispose();
                Interlocked.Increment(ref Injected);
                throw new HttpRequestException("injected: connection reset after the server answered");
            }
            return response;
        }
    }

    private static async Task<LatticeServer> StartCollector(string root, TokenStore? tokens = null, IngestLimits? limits = null, Func<long>? quotaClock = null)
        => await LatticeServer.StartAsync(new ServerOptions
        {
            QuotaClock = quotaClock,
            DataDirectory = root,
            IngestTemplate = new IngestServiceOptions { StoreDirectory = "", Store = new StoreWriterOptions { MinFreeDiskBytes = 0 } },
            Tokens = tokens ?? TokenStore.FromTokens(TokenStore.Token("t", Secret)),
            Limits = limits ?? new IngestLimits(),
            Url = "http://127.0.0.1:0",
        });

    /// <summary>A producer store with <paramref name="n"/> records carrying attribute i = 0..n-1.</summary>
    private static (string Root, string StoreId, ulong Last) ProducerStore(int n, int segmentBytes = 256 * 1024)
    {
        var root = TestData.TempDir();
        using (var logger = LatticeLogger.CreateLocal(root, o => { o.Durability = DurabilityMode.Fast; o.FlushOnProcessExit = false; },
                   new StoreWriterOptions { MinFreeDiskBytes = 0, SegmentMaxBytes = segmentBytes }))
        {
            using var op = logger.BeginOperation("checkout", "op-1");
            for (var i = 0; i < n; i++) logger.Info("event {i}", new LogAttribute("i", i), new LogAttribute("payload", "p-" + (i % 97)));
        }
        return (root, StoreWriter.ReadStoreFile(root).StoreId, (ulong)n);
    }

    /// <summary>Records of the server's default namespace store (data/ns/default).</summary>
    private static string DefaultStore(string dataRoot) => Path.Combine(dataRoot, "ns", NamespaceNames.Default);

    private static List<LogRecord> ServerRecords(string root)
    {
        var rep = new ScanReport();
        var all = StoreReader.Open(DefaultStore(root)).ReadAll(rep).ToList();
        Assert.True(rep.IsClean, string.Join("\n", rep.Issues));
        return all;
    }

    private static void AssertExactlyOnceInOrder(List<LogRecord> server, int n, string producer)
    {
        Assert.Equal(n, server.Count);
        for (var i = 0; i < n; i++)
        {
            var r = server[i];
            Assert.True(r.TryGetAttribute("i", out var v));
            Assert.Equal(i, v.AsInt64()); // each record once, in local order
            Assert.Equal($"p-{i % 97}", r.Attributes[1].Value.AsString()); // payload intact
            var ingest = r.Context!;
            Assert.Equal(IngestProtocol.IngestContextName, ingest.Name);
            Assert.Equal(producer, ingest.Attributes[0].Value.AsString());
            Assert.Equal("checkout", ingest.Parent!.Name); // the producer's own context chain is kept underneath
        }
    }

    [Fact]
    public async Task Lossy_network_still_stores_every_record_exactly_once()
    {
        var (local, producer, last) = ProducerStore(12_000);
        var serverRoot = TestData.TempDir();
        await using var collector = await StartCollector(serverRoot);
        var faults = new FaultHandler(failBefore: 0.2, dropResponse: 0.2, delay: 0.1, seed: 7);
        await using (var fwd = Forwarder.Start(local, new ForwarderOptions
        {
            Endpoint = collector.BaseAddress,
            Token = Secret,
            MaxBatchRecords = 700,
            Handler = faults,
            RetryBase = TimeSpan.FromMilliseconds(2),
            RetryMax = TimeSpan.FromMilliseconds(20),
            PollInterval = TimeSpan.FromMilliseconds(10),
        }))
        {
            Assert.True(await fwd.WaitForAsync(last, TimeSpan.FromSeconds(60)), $"state {fwd.State}, committed {fwd.CommittedThrough}, error {fwd.LastError}");
            Assert.True(fwd.Retries > 0);
            // Regression: retries once re-read past the unacknowledged batch and declared a false gap (data loss).
            Assert.Equal(0, fwd.GapsReported);
        }
        Assert.True(faults.Injected > 5, $"only {faults.Injected} faults injected");
        AssertExactlyOnceInOrder(ServerRecords(serverRoot), 12_000, producer);
    }

    [Fact]
    public async Task Collector_crash_without_checkpoint_rebuilds_dedup_from_stored_data()
    {
        var (local, producer, last) = ProducerStore(6_000);
        var serverRoot = TestData.TempDir();
        var collector = await StartCollector(serverRoot);
        await using (var fwd = Forwarder.Start(local, new ForwarderOptions { Endpoint = collector.BaseAddress, Token = Secret, MaxBatchRecords = 500, PollInterval = TimeSpan.FromMilliseconds(10) }))
            Assert.True(await fwd.WaitForAsync(3_000, TimeSpan.FromSeconds(30)));
        await collector.AbortAsync(); // "crash": no checkpoint
        Assert.False(File.Exists(Path.Combine(DefaultStore(serverRoot), IngestService.CheckpointFile)));
        foreach (var f in Directory.GetFiles(local, "FORWARD-*.json")) File.Delete(f); // client forgot its cursor too

        await using var restarted = await StartCollector(serverRoot);
        Assert.True(restarted.Ingest().CommittedThrough(producer) >= 3_000);
        await using (var fwd = Forwarder.Start(local, new ForwarderOptions { Endpoint = restarted.BaseAddress, Token = Secret, MaxBatchRecords = 500, PollInterval = TimeSpan.FromMilliseconds(10) }))
            Assert.True(await fwd.WaitForAsync(last, TimeSpan.FromSeconds(30)), fwd.LastError);
        await restarted.DisposeAsync();
        AssertExactlyOnceInOrder(ServerRecords(serverRoot), 6_000, producer); // resent from 1, nothing duplicated
    }

    private static List<LogRecord> Range(ulong first, int count)
    {
        var list = new TestData(3).Records(count);
        for (var i = 0; i < count; i++) list[i].Sequence = first + (ulong)i;
        return list;
    }

    [Theory]
    [InlineData(IngestProtocol.EncodingJsonl)]
    [InlineData(IngestProtocol.EncodingLlog)]
    public async Task Duplicate_overlap_and_gap_rules(byte encoding)
    {
        var serverRoot = TestData.TempDir();
        await using var collector = await StartCollector(serverRoot);
        using var http = new HttpClient();
        var client = new IngestClient(http, collector.BaseAddress, Secret);
        var producer = Guid.NewGuid().ToString("N");
        async Task<SendResult> Send(ulong first, int count, bool gapAck = false)
            => await client.SendAsync(IngestProtocol.EncodeBatch(producer, Range(first, count), gzip: first % 2 == 0, gapAcknowledged: gapAck, encoding: encoding), AckLevel.FsyncConfirmed, default);

        var r1 = await Send(1, 100);
        Assert.Equal(("stored", 100UL, 100L, AckLevel.FsyncConfirmed), (r1.Ack!.Status, r1.Ack.CommittedThrough, r1.Ack.Stored, r1.Ack.Ack));
        var r2 = await Send(1, 100);
        Assert.Equal(("duplicate", 100UL, 0L, 100L), (r2.Ack!.Status, r2.Ack.CommittedThrough, r2.Ack.Stored, r2.Ack.SkippedDuplicates));
        var r3 = await Send(51, 100); // 51..150 overlaps 51..100
        Assert.Equal(("partial_duplicate", 150UL, 50L, 50L), (r3.Ack!.Status, r3.Ack.CommittedThrough, r3.Ack.Stored, r3.Ack.SkippedDuplicates));
        var r4 = await Send(200, 10);
        Assert.Equal((SendOutcome.SequenceGap, 150UL), (r4.Outcome, r4.CommittedThrough));
        var r5 = await Send(200, 10, gapAck: true);
        Assert.Equal(("stored", 209UL, 49UL), (r5.Ack!.Status, r5.Ack.CommittedThrough, r5.Ack.GapBefore));
        await collector.DisposeAsync();
        Assert.Equal(160, ServerRecords(serverRoot).Count); // 100 + 50 + 10, never twice
    }

    private static async Task<(int Status, string Body)> Post(Uri baseUri, byte[] body, string? token = Secret)
    {
        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, IngestProtocol.Path)) { Content = new ByteArrayContent(body) };
        if (token is not null) req.Headers.Authorization = new("Bearer", token);
        using var resp = await http.SendAsync(req);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Security_and_limits_are_enforced()
    {
        var producer = Guid.NewGuid().ToString("N");
        var other = Guid.NewGuid().ToString("N");
        var tokens = TokenStore.FromTokens(TokenStore.Token("t", Secret), TokenStore.Token("narrow", "narrow-secret", 0, other));
        await using var collector = await StartCollector(TestData.TempDir(), tokens, new IngestLimits { MaxRequestBytes = 256 * 1024, MaxCompressionRatio = 50 });
        var good = IngestProtocol.EncodeBatch(producer, Range(1, 10));

        Assert.Equal(401, (await Post(collector.BaseAddress, good, token: null)).Status);
        Assert.Equal(401, (await Post(collector.BaseAddress, good, token: "wrong")).Status);
        var forbidden = await Post(collector.BaseAddress, good, token: "narrow-secret");
        Assert.Equal(403, forbidden.Status);
        Assert.Contains("forbidden_producer", forbidden.Body);

        var corrupted = (byte[])good.Clone();
        corrupted[^3] ^= 0x55;
        var crc = await Post(collector.BaseAddress, corrupted);
        Assert.Equal(400, crc.Status);
        Assert.Contains("checksum_mismatch", crc.Body);

        var unknownEncoding = (byte[])good.Clone();
        unknownEncoding[5] = 9;
        Assert.Equal(415, (await Post(collector.BaseAddress, unknownEncoding)).Status);

        // Decompression bomb: a few KB that inflate to megabytes (ratio far above 50).
        var bomb = Bomb(producer, 4 * 1024 * 1024);
        var bombAnswer = await Post(collector.BaseAddress, bomb);
        Assert.Equal(413, bombAnswer.Status);
        Assert.Contains("ratio", bombAnswer.Body);

        var huge = new byte[300 * 1024];
        good.CopyTo(huge, 0);
        Assert.Equal(413, (await Post(collector.BaseAddress, huge)).Status);

        Assert.Equal(200, (await Post(collector.BaseAddress, good)).Status); // still healthy afterwards
    }

    /// <summary>Valid envelope whose gzip body inflates to <paramref name="inflated"/> bytes of spaces.</summary>
    private static byte[] Bomb(string producer, int inflated)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var chunk = new byte[64 * 1024];
            Array.Fill(chunk, (byte)' ');
            for (var i = 0; i < inflated / chunk.Length; i++) gz.Write(chunk);
        }
        var body = ms.ToArray();
        var env = new byte[IngestProtocol.HeaderSize + body.Length];
        "LLIB"u8.CopyTo(env);
        env[4] = 1; env[5] = 1; env[6] = 1;
        Convert.FromHexString(producer).CopyTo(env, 8);
        BinaryPrimitives.WriteUInt64LittleEndian(env.AsSpan(24), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(env.AsSpan(32), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(env.AsSpan(40), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(env.AsSpan(44), Checksum.Crc32(body));
        body.CopyTo(env, IngestProtocol.HeaderSize);
        return env;
    }

    [Fact]
    public async Task Tokens_rotate_without_restart_and_quota_answers_429_with_retry_after()
    {
        var dir = TestData.TempDir();
        var file = Path.Combine(dir, "tokens.json");
        void WriteTokens(params (string Id, string Secret, int Rate)[] list)
            => File.WriteAllText(file, "{\"tokens\":[" + string.Join(",", list.Select(t =>
                $"{{\"id\":\"{t.Id}\",\"sha256\":\"{TokenStore.HashHex(t.Secret)}\",\"producers\":[\"*\"],\"records_per_second\":{t.Rate}}}")) + "]}");
        WriteTokens(("old", "old-secret", 0));
        var tokens = TokenStore.FromFile(file);
        // The quota clock is frozen: no refill between requests, however slow the machine (a slow fsync on a CI runner
        // used to let the bucket refill). The refill arithmetic is tested in Quota_refills_at_the_token_rate_and_caps_at_the_burst.
        await using var collector = await StartCollector(TestData.TempDir(), tokens, quotaClock: () => 0);
        var producer = Guid.NewGuid().ToString("N");
        Assert.Equal(200, (await Post(collector.BaseAddress, IngestProtocol.EncodeBatch(producer, Range(1, 5)), "old-secret")).Status);

        WriteTokens(("new", "new-secret", 100)); // rotation: old removed, new added with a 100 records/s quota (burst 200)
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(5));
        tokens.ExpireCache();
        Assert.Equal(401, (await Post(collector.BaseAddress, IngestProtocol.EncodeBatch(producer, Range(6, 5)), "old-secret")).Status);
        Assert.Equal(200, (await Post(collector.BaseAddress, IngestProtocol.EncodeBatch(producer, Range(6, 150)), "new-secret")).Status); // within the 200 burst

        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(collector.BaseAddress, IngestProtocol.Path))
        {
            Content = new ByteArrayContent(IngestProtocol.EncodeBatch(producer, Range(156, 150))), // only 50 left
        };
        req.Headers.Authorization = new("Bearer", "new-secret");
        using var limited = await http.SendAsync(req);
        Assert.Equal(429, (int)limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task A_full_writer_queue_answers_503_with_retry_after_and_loses_nothing()
    {
        const int maxGroup = 4, queueCapacity = 2, batches = maxGroup + queueCapacity + 3, perBatch = 10;
        var serverRoot = TestData.TempDir();
        await using var collector = await LatticeServer.StartAsync(new ServerOptions
        {
            DataDirectory = serverRoot,
            IngestTemplate = new IngestServiceOptions
            {
                StoreDirectory = "",
                Store = new StoreWriterOptions { MinFreeDiskBytes = 0 },
                MaxGroup = maxGroup,
                QueueCapacity = queueCapacity,
            },
            Tokens = TokenStore.FromTokens(TokenStore.Token("t", Secret)),
            Url = "http://127.0.0.1:0",
        });
        using var http = new HttpClient();
        async Task<(int Status, TimeSpan? RetryAfter)> Send(byte[] body)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(collector.BaseAddress, IngestProtocol.Path)) { Content = new ByteArrayContent(body) };
            req.Headers.Authorization = new("Bearer", Secret);
            using var resp = await http.SendAsync(req);
            return ((int)resp.StatusCode, resp.Headers.RetryAfter?.Delta);
        }

        var warmup = IngestProtocol.EncodeBatch(Guid.NewGuid().ToString("N"), Range(1, perBatch));
        Assert.Equal(200, (await Send(warmup)).Status); // opens the namespace store
        var service = collector.Ingest();
        var bodies = Enumerable.Range(0, batches).Select(_ => IngestProtocol.EncodeBatch(Guid.NewGuid().ToString("N"), Range(1, perBatch))).ToArray();

        // With the writer held it can absorb at most one group (maxGroup batches) plus a full queue, so at least
        // batches - maxGroup - queueCapacity requests must be refused at once instead of waiting without bound.
        List<Task<(int Status, TimeSpan? RetryAfter)>> sends;
        using (service.HoldWriterForTesting())
        {
            sends = bodies.Select(Send).ToList();
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (sends.Count(t => t.IsCompleted) < batches - maxGroup - queueCapacity)
            {
                Assert.True(DateTime.UtcNow < deadline, "the server never answered 503 while its writer was held");
                await Task.Delay(20);
            }
            Assert.True(service.GetStats().QueueDepth <= queueCapacity);
        }
        var answers = await Task.WhenAll(sends);
        var refused = Enumerable.Range(0, batches).Where(i => answers[i].Status == 503).ToList();
        Assert.True(refused.Count >= batches - maxGroup - queueCapacity, $"only {refused.Count} refused");
        Assert.All(refused, i => Assert.NotNull(answers[i].RetryAfter));
        Assert.All(Enumerable.Range(0, batches).Except(refused), i => Assert.Equal(200, answers[i].Status));

        // A producer that got 503 simply resends: everything ends up stored exactly once.
        foreach (var i in refused) Assert.Equal(200, (await Send(bodies[i])).Status);
        foreach (var body in bodies) Assert.Equal(200, (await Send(body)).Status); // a second resend is a duplicate, not new data
        Assert.Equal((ulong)((batches + 1) * perBatch), service.LastSequence);
    }

    [Fact]
    public async Task A_full_producer_spool_refuses_new_records_keeps_the_stored_ones_and_ships_them()
    {
        // The producer's local store is its spool. When its quota is reached, new batches are refused and counted
        // (DroppedWriteError); nothing already stored is deleted, and everything stored still reaches the server.
        const int written = 5_000;
        var local = TestData.TempDir();
        LoggerStats stats;
        using (var logger = LatticeLogger.CreateLocal(local, o => { o.Durability = DurabilityMode.Balanced; o.FlushOnProcessExit = false; },
                   new StoreWriterOptions { MinFreeDiskBytes = 0, MaxStoreBytes = 48 * 1024, SegmentMaxBytes = 16 * 1024 }))
        {
            for (var i = 0; i < written; i++)
            {
                logger.Info("order {i}", new LogAttribute("i", i), new LogAttribute("payload", "p-" + (i % 97)));
                if (i % 250 == 249) await logger.FlushAsync(AckLevel.WrittenToOs); // several batches, so the quota is hit between them
            }
            await logger.FlushAsync(AckLevel.FsyncConfirmed);
            logger.Info("still alive after the spool filled up"); // logging never throws into the application
            await logger.FlushAsync(AckLevel.FsyncConfirmed);
            stats = logger.GetStats();
        }

        var report = new ScanReport();
        var spool = StoreReader.Open(local).ReadAll(report).Select(r => r.Attributes[0].Value.AsInt64()).ToList();
        Assert.True(report.IsClean, string.Join("\n", report.Issues));
        Assert.True(stats.DroppedWriteError > 0, "the quota was never reached");
        Assert.True(spool.Count > 0);
        Assert.Equal(written + 1, spool.Count + stats.DroppedWriteError); // every record is either stored or counted as dropped
        Assert.Equal(Enumerable.Range(0, spool.Count).Select(i => (long)i), spool); // a prefix: refused at the end, never deleted

        var serverRoot = TestData.TempDir();
        await using (var collector = await StartCollector(serverRoot))
        await using (var fwd = Forwarder.Start(local, new ForwarderOptions { Endpoint = collector.BaseAddress, Token = Secret, PollInterval = TimeSpan.FromMilliseconds(10) }))
            Assert.True(await fwd.WaitForAsync((ulong)spool.Count, TimeSpan.FromSeconds(30)), fwd.LastError);
        Assert.Equal(spool, ServerRecords(serverRoot).Select(r => r.Attributes[0].Value.AsInt64()));
        Assert.Equal(spool.Count, StoreReader.Open(local).ReadAll(new ScanReport()).Count()); // shipping does not delete from the spool
    }

    [Fact]
    public void Quota_refills_at_the_token_rate_and_caps_at_the_burst()
    {
        long now = 0;
        var second = System.Diagnostics.Stopwatch.Frequency;
        var quota = new QuotaLimiter { Clock = () => now };
        var token = TokenStore.Token("q", "q-secret", recordsPerSecond: 100); // burst = 200
        Assert.True(quota.TryTake(token, 150, out _));
        Assert.False(quota.TryTake(token, 150, out var wait)); // 50 left
        Assert.Equal(TimeSpan.FromSeconds(1), wait);           // 100 more needed at 100/s
        now += second;                                         // +100
        Assert.True(quota.TryTake(token, 150, out _));         // 150 available, 0 left
        now += 10 * second;                                    // refill stops at the burst
        Assert.False(quota.TryTake(token, 201, out _) && quota.TryTake(token, 1, out _));
        Assert.True(quota.TryTake(TokenStore.Token("free", "f", recordsPerSecond: 0), 1_000_000, out _)); // 0 = unlimited
    }

    [Fact]
    public async Task Plain_http_on_a_public_address_is_refused()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => LatticeServer.StartAsync(new ServerOptions
        {
            DataDirectory = TestData.TempDir(),
            Tokens = TokenStore.FromTokens(TokenStore.Token("t", Secret)),
            Url = "http://0.0.0.0:0",
        }));
        Assert.Contains("plain HTTP", ex.Message);
    }

    [Fact]
    public void Binary_body_round_trips_exactly_and_falls_back_to_jsonl_for_lone_surrogate_keys()
    {
        var records = new TestData(21).Records(800); // NaN payloads, binary, decimals, deep maps, odd strings in values
        for (var i = 0; i < records.Count; i++) records[i].Sequence = (ulong)i + 1;
        var producer = Guid.NewGuid().ToString("N");
        foreach (var gzip in new[] { true, false })
        {
            var env = IngestProtocol.EncodeBatch(producer, records, gzip, encoding: IngestProtocol.EncodingLlog);
            Assert.Equal(IngestProtocol.EncodingLlog, env[5]);
            var decoded = BatchDecoder.Decode(env, new IngestLimits());
            for (var i = 0; i < records.Count; i++) Assert.True(records[i].Equals(decoded.Records[i]), TestData.Describe(records[i], decoded.Records[i]));
        }
        var jsonl = IngestProtocol.EncodeBatch(producer, records, encoding: IngestProtocol.EncodingJsonl);
        var binary = IngestProtocol.EncodeBatch(producer, records, encoding: IngestProtocol.EncodingLlog);
        Assert.True(binary.Length < jsonl.Length, $"binary {binary.Length} vs jsonl {jsonl.Length}");

        var odd = Range(1, 3);
        odd[1].Attributes = [new("bad\uD800key", 1)];
        Assert.Equal(IngestProtocol.EncodingJsonl, IngestProtocol.EncodeBatch(producer, odd, encoding: IngestProtocol.EncodingLlog)[5]);
    }

    [Fact]
    public void Corrupted_binary_bodies_are_rejected_cleanly()
    {
        var records = Range(1, 200);
        var env = IngestProtocol.EncodeBatch(Guid.NewGuid().ToString("N"), records, gzip: false, encoding: IngestProtocol.EncodingLlog);
        var rnd = new Random(9);
        var accepted = 0;
        for (var iter = 0; iter < 400; iter++)
        {
            var bad = (byte[])env.Clone();
            for (var f = rnd.Next(1, 4); f > 0; f--) bad[rnd.Next(IngestProtocol.HeaderSize, bad.Length)] ^= (byte)(1 << rnd.Next(8));
            // Valid CRC on purpose: the decoder itself must cope with garbage (a buggy or hostile client).
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(44), Checksum.Crc32(bad.AsSpan(IngestProtocol.HeaderSize)));
            try
            {
                var d = BatchDecoder.Decode(bad, new IngestLimits());
                Assert.Equal(200, d.Records.Count); // if accepted, the envelope invariants still hold
                accepted++;
            }
            catch (IngestException ex) { Assert.True(ex.Status is 400 or 413, ex.Message); }
        }
        Assert.True(accepted < 400);
    }

    /// <summary>Plays an older server: 415 for binary bodies, passes JSONL through.</summary>
    private sealed class OldServerHandler() : DelegatingHandler(new SocketsHttpHandler())
    {
        public int Rejected;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsByteArrayAsync(ct);
            if (body.Length > 5 && body[5] == IngestProtocol.EncodingLlog)
            {
                Interlocked.Increment(ref Rejected);
                return new HttpResponseMessage((System.Net.HttpStatusCode)415) { Content = new StringContent("{\"error\":\"unsupported_encoding\"}") };
            }
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new(IngestProtocol.ContentType);
            return await base.SendAsync(request, ct);
        }
    }

    [Fact]
    public async Task Forwarder_falls_back_to_jsonl_when_the_server_rejects_the_binary_body()
    {
        var (local, producer, last) = ProducerStore(2_000);
        var serverRoot = TestData.TempDir();
        await using var collector = await StartCollector(serverRoot);
        var old = new OldServerHandler();
        await using (var fwd = Forwarder.Start(local, new ForwarderOptions { Endpoint = collector.BaseAddress, Token = Secret, Handler = old, PollInterval = TimeSpan.FromMilliseconds(10) }))
            Assert.True(await fwd.WaitForAsync(last, TimeSpan.FromSeconds(30)), fwd.LastError);
        Assert.Equal(1, old.Rejected); // one 415, then JSONL for the rest
        AssertExactlyOnceInOrder(ServerRecords(serverRoot), 2_000, producer);
    }
}
