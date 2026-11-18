// End-to-end ingestion measurement (P16-T14): a producer store is shipped to a collector on loopback with the real
// client, one batch in flight (as the forwarder does). Reports records/s, wire bytes and ACK latency per batch.
using System.Diagnostics;
using System.Globalization;
using LatticeLog;
using LatticeLog.Core;
using LatticeLog.Ingest;
using LatticeLog.Server;
using LatticeLog.Storage;

public static class IngestBench
{
    public static async Task<int> Run(string[] args)
    {
        int Arg(string name, int fallback) { var i = Array.IndexOf(args, name); return i >= 0 ? int.Parse(args[i + 1], CultureInfo.InvariantCulture) : fallback; }
        var n = Arg("--records", 300_000);
        var batchSize = Arg("--batch", 5_000);
        var producerRoot = Path.Combine(Path.GetTempPath(), "latticelog-ingest-bench", Guid.NewGuid().ToString("N"));
        using (var logger = LatticeLogger.CreateLocal(producerRoot, o => { o.Durability = DurabilityMode.Fast; o.FlushOnProcessExit = false; }, new StoreWriterOptions { MinFreeDiskBytes = 0 }))
            for (var i = 0; i < n; i++) BenchEvents.OrderPlaced(logger, i, i % 7, "SKU-" + (i % 500).ToString("D5", CultureInfo.InvariantCulture), 19.99 + i % 13);
        var producer = StoreWriter.ReadStoreFile(producerRoot).StoreId;
        var records = StoreReader.Open(producerRoot).ReadAll(new ScanReport()).ToList();
        var producersArg = Array.IndexOf(args, "--producers");
        if (producersArg >= 0)
            return await RunConcurrent(records, args[producersArg + 1].Split(',').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray(), Arg("--batch", 1_000), Arg("--readers", 0), (byte)Arg("--encoding", 1));
        Console.WriteLine($"producer store: {records.Count:N0} records; batch {batchSize:N0}; machine: {Environment.ProcessorCount} logical CPUs");
        Console.WriteLine("| body | ack | records/s | MB/s on the wire | bytes/record | ACK p50 ms | ACK p99 ms | ACK max ms |");
        Console.WriteLine("|---|---|---:|---:|---:|---:|---:|---:|");
        // The first configuration runs twice: the first pass (JIT, disk warm-up) is not printed.
        var runs = new[] { (true, AckLevel.FsyncConfirmed, false), (true, AckLevel.FsyncConfirmed, true), (false, AckLevel.FsyncConfirmed, true), (true, AckLevel.WrittenToOs, true), (false, AckLevel.WrittenToOs, true) };
        foreach (var (gzip, ack, print) in runs)
        {
            var serverRoot = Path.Combine(Path.GetTempPath(), "latticelog-ingest-bench", Guid.NewGuid().ToString("N"));
            await using var collector = await LatticeServer.StartAsync(new ServerOptions
            {
                DataDirectory = serverRoot,
                IngestTemplate = new IngestServiceOptions { StoreDirectory = "", Store = new StoreWriterOptions { MinFreeDiskBytes = 0 } },
                Tokens = TokenStore.FromTokens(TokenStore.Token("bench", "bench-secret")),
                Url = "http://127.0.0.1:0",
            });
            using var http = new HttpClient();
            var client = new IngestClient(http, collector.BaseAddress, "bench-secret");
            var latencies = new List<double>();
            long wire = 0;
            var total = Stopwatch.StartNew();
            for (var i = 0; i < records.Count; i += batchSize)
            {
                var batch = records.GetRange(i, Math.Min(batchSize, records.Count - i));
                var env = IngestProtocol.EncodeBatch(producer, batch, gzip);
                wire += env.Length;
                var t = Stopwatch.StartNew();
                var r = await client.SendAsync(env, ack, default);
                latencies.Add(t.Elapsed.TotalMilliseconds);
                if (r.Outcome != SendOutcome.Acknowledged || r.Ack!.Ack < ack) throw new InvalidOperationException($"unexpected answer {r.Outcome} {r.Error}");
            }
            var secs = total.Elapsed.TotalSeconds;
            latencies.Sort();
            double P(double p) => latencies[Math.Min(latencies.Count - 1, (int)Math.Ceiling(p / 100 * latencies.Count) - 1)];
            if (print) Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"| {(gzip ? "jsonl+gzip" : "jsonl")} | {ack} | {records.Count / secs:N0} | {wire / 1e6 / secs:F1} | {wire / (double)records.Count:F1} | {P(50):F1} | {P(99):F1} | {latencies[^1]:F1} |"));
            if (collector.Ingest().LastSequence != (ulong)records.Count) throw new InvalidOperationException("server did not store every record");
        }
        return 0;
    }

    /// <summary>
    /// P concurrent producers (distinct ids), each with one batch in flight, same total volume. Envelopes are encoded
    /// before timing, so this measures the collector (transport + decode + dedup + group commit), not the clients.
    /// </summary>
    private static async Task<int> RunConcurrent(List<LogRecord> records, int[] producerCounts, int batchSize, int readers, byte encoding)
    {
        Console.WriteLine($"concurrent producers: {records.Count:N0} records in total, batch {batchSize:N0}, gzip, body {(encoding == IngestProtocol.EncodingLlog ? "llog" : "jsonl")}, ACK fsync, envelopes pre-encoded; {readers} concurrent reader(s)");
        Console.WriteLine("| producers | records/s | ACK p50 ms | ACK p99 ms | ACK max ms | batches per fsync | queries | query p50 ms | query p99 ms | peak working set MiB |");
        Console.WriteLine("|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var producers in producerCounts.Prepend(producerCounts[0])) // first entry repeated as warm-up
        {
            var envelopes = new List<byte[]>[producers];
            var per = records.Count / producers;
            for (var p = 0; p < producers; p++)
            {
                var id = Guid.NewGuid().ToString("N");
                envelopes[p] = [];
                var slice = records.GetRange(p * per, per);
                for (var i = 0; i < slice.Count; i += batchSize)
                {
                    var batch = slice.GetRange(i, Math.Min(batchSize, slice.Count - i));
                    for (var k = 0; k < batch.Count; k++) batch[k].Sequence = (ulong)(i + k + 1);
                    envelopes[p].Add(IngestProtocol.EncodeBatch(id, batch, gzip: true, encoding: encoding));
                }
            }
            var serverRoot = Path.Combine(Path.GetTempPath(), "latticelog-ingest-bench", Guid.NewGuid().ToString("N"));
            await using var collector = await LatticeServer.StartAsync(new ServerOptions
            {
                DataDirectory = serverRoot,
                IngestTemplate = new IngestServiceOptions { StoreDirectory = "", Store = new StoreWriterOptions { MinFreeDiskBytes = 0 } },
                Tokens = TokenStore.FromTokens(TokenStore.Token("bench", "bench-secret")),
                Url = "http://127.0.0.1:0",
                Diagnostics = m => Console.Error.WriteLine(m),
            });
            using var http = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 1024 });
            var client = new IngestClient(http, collector.BaseAddress, "bench-secret");
            var latencies = new System.Collections.Concurrent.ConcurrentBag<double>();
            var queryLatencies = new System.Collections.Concurrent.ConcurrentBag<double>();
            using var stopReaders = new CancellationTokenSource();
            // Readers: alternate a selective search and an aggregation on the namespace being written (P17-T16).
            var readerTasks = Enumerable.Range(0, readers).Select(rd => Task.Run(async () =>
            {
                using var rhttp = new HttpClient { BaseAddress = collector.BaseAddress };
                rhttp.DefaultRequestHeaders.Authorization = new("Bearer", "bench-secret");
                var k = 0;
                while (!stopReaders.IsCancellationRequested)
                {
                    var path = k++ % 2 == 0 ? "/v1/search?ns=default&where=" + Uri.EscapeDataString("attr.quantity = 3") + "&limit=100&desc=true&order=desc" : "/v1/stats?ns=default&by=event";
                    var t = Stopwatch.StartNew();
                    using var resp = await rhttp.GetAsync(path);
                    if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) { await Task.Delay(5); continue; } // namespace not created yet
                    await resp.Content.ReadAsByteArrayAsync();
                    if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"query failed: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
                    queryLatencies.Add(t.Elapsed.TotalMilliseconds);
                }
            })).ToList();
            GC.Collect();
            var total = Stopwatch.StartNew();
            await Task.WhenAll(Enumerable.Range(0, producers).Select(p => Task.Run(async () =>
            {
                foreach (var env in envelopes[p])
                {
                    var t = Stopwatch.StartNew();
                    var r = await client.SendAsync(env, AckLevel.FsyncConfirmed, default);
                    latencies.Add(t.Elapsed.TotalMilliseconds);
                    if (r.Outcome != SendOutcome.Acknowledged) throw new InvalidOperationException($"{r.Outcome} {r.Error}");
                }
            })));
            var secs = total.Elapsed.TotalSeconds;
            stopReaders.Cancel();
            await Task.WhenAll(readerTasks);
            var stats = collector.Ingest().GetStats();
            var expected = (long)per * producers;
            if (stats.RecordsStored != expected || collector.Ingest().LastSequence != (ulong)expected) throw new InvalidOperationException("record count mismatch");
            var sorted = latencies.OrderBy(x => x).ToArray();
            double P(double q) => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(q / 100 * sorted.Length) - 1)];
            var qs = queryLatencies.OrderBy(x => x).ToArray();
            string QP(double q) => qs.Length == 0 ? "-" : qs[Math.Min(qs.Length - 1, (int)Math.Ceiling(q / 100 * qs.Length) - 1)].ToString("F1", CultureInfo.InvariantCulture);
            if (_warmedUp)
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {producers} | {expected / secs:N0} | {P(50):F1} | {P(99):F1} | {sorted[^1]:F1} | {stats.BatchesStored / (double)Math.Max(1, stats.Flushes):F1} | {qs.Length} | {QP(50)} | {QP(99)} | {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576} |"));
            _warmedUp = true;
        }
        return 0;
    }

    private static bool _warmedUp;
}
