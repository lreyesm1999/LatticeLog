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
        Console.WriteLine($"producer store: {records.Count:N0} records; batch {batchSize:N0}; machine: {Environment.ProcessorCount} logical CPUs");
        Console.WriteLine("| body | ack | records/s | MB/s on the wire | bytes/record | ACK p50 ms | ACK p99 ms | ACK max ms |");
        Console.WriteLine("|---|---|---:|---:|---:|---:|---:|---:|");
        // The first configuration runs twice: the first pass (JIT, disk warm-up) is not printed.
        var runs = new[] { (true, AckLevel.FsyncConfirmed, false), (true, AckLevel.FsyncConfirmed, true), (false, AckLevel.FsyncConfirmed, true), (true, AckLevel.WrittenToOs, true), (false, AckLevel.WrittenToOs, true) };
        foreach (var (gzip, ack, print) in runs)
        {
            var serverRoot = Path.Combine(Path.GetTempPath(), "latticelog-ingest-bench", Guid.NewGuid().ToString("N"));
            await using var collector = await Collector.StartAsync(new CollectorOptions
            {
                Ingest = new IngestServiceOptions { StoreDirectory = serverRoot, Store = new StoreWriterOptions { MinFreeDiskBytes = 0 } },
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
            if (collector.Service.LastSequence != (ulong)records.Count) throw new InvalidOperationException("server did not store every record");
        }
        return 0;
    }
}
