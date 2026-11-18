using LatticeLog.Ingest;

namespace LatticeLog.Server;

/// <summary>Asynchronous replication of every namespace to another LatticeLog server (P19).</summary>
public sealed record ReplicationOptions
{
    /// <summary>Base address of the replica server.</summary>
    public required Uri Target { get; init; }
    /// <summary>Token on the replica with the ingest scope for the replicated namespaces (usually "*").</summary>
    public required string Token { get; init; }
    /// <summary>Namespaces to replicate (null = all with data, discovered continuously).</summary>
    public IReadOnlyList<string>? Namespaces { get; init; }
    /// <summary>Level the replica must reach before a batch counts as replicated.</summary>
    public AckLevel Ack { get; init; } = AckLevel.FsyncConfirmed;
    public TimeSpan DiscoveryInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(200);
    /// <summary>Custom handler (client certificates, tests with fault injection).</summary>
    public HttpMessageHandler? Handler { get; init; }
}

/// <summary>
/// Primary side: one <see cref="Forwarder"/> per namespace store, shipping to the same namespace on the replica with
/// the normal ingestion protocol. The replica deduplicates by (namespace store id, sequence) and also learns how far
/// each original producer got (upstream provenance), so producers can fail over to the replica without duplicates.
/// Replication is asynchronous: an acknowledgement to a producer does not wait for the replica.
/// </summary>
public sealed class Replicator : IAsyncDisposable
{
    private readonly NamespaceRegistry _namespaces;
    private readonly ReplicationOptions _options;
    private readonly Action<string> _diagnose;
    private readonly Dictionary<string, Forwarder> _forwarders = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public Replicator(NamespaceRegistry namespaces, ReplicationOptions options, Action<string> diagnose)
    {
        _namespaces = namespaces;
        _options = options;
        _diagnose = diagnose;
        _loop = Task.Run(LoopAsync);
    }

    public sealed record NamespaceStatus(string Namespace, ulong ReplicatedThrough, ForwarderState State, string? LastError);

    public IReadOnlyList<NamespaceStatus> Status()
    {
        lock (_forwarders)
            return _forwarders.Select(kv => new NamespaceStatus(kv.Key, kv.Value.CommittedThrough, kv.Value.State, kv.Value.LastError)).ToList();
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                foreach (var ns in _options.Namespaces ?? _namespaces.List())
                {
                    if (!_namespaces.Exists(ns)) continue;
                    lock (_forwarders)
                    {
                        if (_forwarders.ContainsKey(ns)) continue;
                        _forwarders[ns] = Forwarder.Start(_namespaces.StorePath(ns), new ForwarderOptions
                        {
                            Endpoint = _options.Target,
                            Token = _options.Token,
                            Namespace = ns,
                            Ack = _options.Ack,
                            PollInterval = _options.PollInterval,
                            Handler = _options.Handler,
                            Diagnostics = m => _diagnose($"replication '{ns}': {m}"),
                        });
                    }
                    _diagnose($"replication of '{ns}' to {_options.Target} started");
                }
                await Task.Delay(_options.DiscoveryInterval, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _diagnose("replication discovery failed: " + ex.Message);
                try { await Task.Delay(_options.DiscoveryInterval, _stop.Token).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        List<Forwarder> all;
        lock (_forwarders) all = [.. _forwarders.Values];
        foreach (var f in all) await f.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }
}
