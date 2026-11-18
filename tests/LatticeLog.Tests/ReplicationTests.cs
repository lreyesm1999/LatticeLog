using LatticeLog.Core;
using LatticeLog.Ingest;
using LatticeLog.Server;
using LatticeLog.Storage;

namespace LatticeLog.Tests;

/// <summary>Asynchronous replication between servers and producer failover to the replica (P19).</summary>
public class ReplicationTests
{
    private static async Task<LatticeServer> Start(string data, TokenInfo[] tokens, ReplicationOptions? replication = null)
        => await LatticeServer.StartAsync(new ServerOptions
        {
            DataDirectory = data,
            Tokens = TokenStore.FromTokens(tokens),
            IngestTemplate = new IngestServiceOptions { StoreDirectory = "", Store = new StoreWriterOptions { MinFreeDiskBytes = 0, SegmentMaxBytes = 32 * 1024 } },
            Replication = replication,
            Url = "http://127.0.0.1:0",
        });

    private static void Write(string local, int from, int count)
    {
        using var logger = LatticeLogger.CreateLocal(local, o => { o.Durability = DurabilityMode.Durable; o.FlushOnProcessExit = false; },
            new StoreWriterOptions { MinFreeDiskBytes = 0 });
        for (var i = from; i < from + count; i++) logger.Info("order {i}", new LogAttribute("i", i));
    }

    private static List<long> Values(string store)
        => StoreReader.Open(store).ReadAll(new ScanReport()).Select(r => r.Attributes[0].Value.AsInt64()).ToList();

    private static async Task WaitFor(Func<bool> condition, string what, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(what);
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Replica_receives_everything_and_producers_fail_over_without_duplicates()
    {
        var replicaData = TestData.TempDir();
        var replica = await Start(replicaData,
        [
            TokenStore.Create("replicator", "repl-secret", TokenScopes.Ingest, ["*"]),
            TokenStore.Create("app-on-replica", "app-secret-r", TokenScopes.Ingest, ["acme/prod"]),
        ]);
        await using var _ = replica;
        var primaryData = TestData.TempDir();
        var primary = await Start(primaryData, [TokenStore.Create("app", "app-secret", TokenScopes.Ingest, ["acme/prod"])],
            new ReplicationOptions { Target = replica.BaseAddress, Token = "repl-secret", DiscoveryInterval = TimeSpan.FromMilliseconds(100), PollInterval = TimeSpan.FromMilliseconds(20) });

        var local = TestData.TempDir();
        Write(local, 0, 3_000);
        await using (var fwd = Forwarder.Start(local, new ForwarderOptions { Endpoint = primary.BaseAddress, Token = "app-secret", PollInterval = TimeSpan.FromMilliseconds(10), MaxBatchRecords = 400 }))
            Assert.True(await fwd.WaitForAsync(3_000, TimeSpan.FromSeconds(30)), fwd.LastError);

        var replicaStore = Path.Combine(replicaData, "ns", "acme~prod");
        await WaitFor(() => File.Exists(Path.Combine(replicaStore, "STORE.json")) && replica.Ingest("acme/prod").LastSequence == 3_000, "replication");
        Assert.Equal(Enumerable.Range(0, 3_000).Select(i => (long)i), Values(replicaStore));
        // Provenance: replica layer (primary store) over the primary layer (original producer) over the app's context.
        var producer = StoreWriter.ReadStoreFile(local).StoreId;
        var sample = StoreReader.Open(replicaStore).ReadAll(new ScanReport()).First();
        Assert.Equal("ingest", sample.Context!.Name);
        Assert.Equal("ingest", sample.Context.Parent!.Name);
        Assert.Equal(producer, sample.Context.Parent.Attributes[0].Value.AsString());
        Assert.Equal(3_000UL, replica.Ingest("acme/prod").CommittedThrough(producer)); // learned from upstream provenance
        var status = Assert.Single(primary.ReplicationStatus!);
        Assert.Equal(3_000UL, status.ReplicatedThrough);

        // The primary dies; the producer, which also lost its cursor, switches to the replica and resends everything.
        await primary.AbortAsync();
        Write(local, 3_000, 500);
        await using (var fwd = Forwarder.Start(local, new ForwarderOptions { Endpoint = replica.BaseAddress, Token = "app-secret-r", PollInterval = TimeSpan.FromMilliseconds(10), MaxBatchRecords = 400 }))
            Assert.True(await fwd.WaitForAsync(3_500, TimeSpan.FromSeconds(30)), fwd.LastError);
        Assert.Equal(Enumerable.Range(0, 3_500).Select(i => (long)i), Values(replicaStore)); // nothing twice, nothing lost

        // And the replica keeps that knowledge across its own restart.
        await replica.DisposeAsync();
        await using var restarted = await Start(replicaData, [TokenStore.Create("app-on-replica", "app-secret-r", TokenScopes.Ingest, ["acme/prod"])]);
        Assert.Equal(3_500UL, restarted.Ingest("acme/prod").CommittedThrough(producer));
    }
}
