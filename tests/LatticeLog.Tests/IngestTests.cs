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

    private static async Task<Collector> StartCollector(string root, TokenStore? tokens = null, IngestLimits? limits = null)
        => await Collector.StartAsync(new CollectorOptions
        {
            Ingest = new IngestServiceOptions { StoreDirectory = root, Store = new StoreWriterOptions { MinFreeDiskBytes = 0 } },
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

    private static List<LogRecord> ServerRecords(string root)
    {
        var rep = new ScanReport();
        var all = StoreReader.Open(root).ReadAll(rep).ToList();
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
            Endpoint = collector.BaseAddress, Token = Secret, MaxBatchRecords = 700, Handler = faults,
            RetryBase = TimeSpan.FromMilliseconds(2), RetryMax = TimeSpan.FromMilliseconds(20), PollInterval = TimeSpan.FromMilliseconds(10),
        }))
        {
            Assert.True(await fwd.WaitForAsync(last, TimeSpan.FromSeconds(60)), $"state {fwd.State}, committed {fwd.CommittedThrough}, error {fwd.LastError}");
            Assert.True(fwd.Retries > 0);
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
        Assert.False(File.Exists(Path.Combine(serverRoot, IngestService.CheckpointFile)));
        foreach (var f in Directory.GetFiles(local, "FORWARD-*.json")) File.Delete(f); // client forgot its cursor too

        await using var restarted = await StartCollector(serverRoot);
        Assert.True(restarted.Service.CommittedThrough(producer) >= 3_000);
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

    [Fact]
    public async Task Duplicate_overlap_and_gap_rules()
    {
        var serverRoot = TestData.TempDir();
        await using var collector = await StartCollector(serverRoot);
        using var http = new HttpClient();
        var client = new IngestClient(http, collector.BaseAddress, Secret);
        var producer = Guid.NewGuid().ToString("N");
        async Task<SendResult> Send(ulong first, int count, bool gapAck = false)
            => await client.SendAsync(IngestProtocol.EncodeBatch(producer, Range(first, count), gzip: first % 2 == 0, gapAcknowledged: gapAck), AckLevel.FsyncConfirmed, default);

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
        await using var collector = await StartCollector(TestData.TempDir(), tokens);
        var producer = Guid.NewGuid().ToString("N");
        Assert.Equal(200, (await Post(collector.BaseAddress, IngestProtocol.EncodeBatch(producer, Range(1, 5)), "old-secret")).Status);

        WriteTokens(("new", "new-secret", 100)); // rotation: old removed, new added with a 100 records/s quota
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(5));
        tokens.ExpireCache();
        Assert.Equal(401, (await Post(collector.BaseAddress, IngestProtocol.EncodeBatch(producer, Range(6, 5)), "old-secret")).Status);
        Assert.Equal(200, (await Post(collector.BaseAddress, IngestProtocol.EncodeBatch(producer, Range(6, 150)), "new-secret")).Status); // within the 200 burst

        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(collector.BaseAddress, IngestProtocol.Path))
        {
            Content = new ByteArrayContent(IngestProtocol.EncodeBatch(producer, Range(156, 150))),
        };
        req.Headers.Authorization = new("Bearer", "new-secret");
        using var limited = await http.SendAsync(req);
        Assert.Equal(429, (int)limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task Plain_http_on_a_public_address_is_refused()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Collector.StartAsync(new CollectorOptions
        {
            Ingest = new IngestServiceOptions { StoreDirectory = TestData.TempDir() },
            Tokens = TokenStore.FromTokens(TokenStore.Token("t", Secret)),
            Url = "http://0.0.0.0:0",
        }));
        Assert.Contains("plain HTTP", ex.Message);
    }
}
