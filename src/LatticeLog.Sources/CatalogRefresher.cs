using System.Collections.Concurrent;

namespace LatticeLog.Sources;

/// <summary>What the last refreshes did to one source (shown by <c>latticelog service status</c>).</summary>
public sealed class SourceStatus
{
    public required string Name { get; set; }
    /// <summary>Last refresh that checked this source without error (it is up to date as of then).</summary>
    public DateTime? LastIndexedUtc { get; set; }
    /// <summary>Last refresh that added records.</summary>
    public DateTime? LastImportUtc { get; set; }
    public long RecordsImported { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastErrorUtc { get; set; }
}

/// <summary>
/// Brings a <see cref="SourceCatalog"/> up to date (discovery + incremental indexing of Python sources + followed
/// migrations) at most once per interval. Refreshes that share a <c>gate</c> never run at the same time, so one process
/// that owns several catalogs (the background service) never contends with itself for an index's writer lock.
/// </summary>
public sealed class CatalogRefresher
{
    private readonly SemaphoreSlim _gate;
    private readonly TimeSpan _interval;
    private readonly ConcurrentDictionary<string, SourceStatus> _status;
    private long _lastTicks = DateTime.MinValue.Ticks;

    public CatalogRefresher(SourceCatalog catalog, TimeSpan interval, SemaphoreSlim? gate = null, ConcurrentDictionary<string, SourceStatus>? status = null)
    {
        Catalog = catalog;
        _interval = interval;
        _gate = gate ?? new SemaphoreSlim(1, 1);
        _status = status ?? new ConcurrentDictionary<string, SourceStatus>(StringComparer.Ordinal);
    }

    public SourceCatalog Catalog { get; }

    public DateTime LastRefreshUtc => new(Interlocked.Read(ref _lastTicks), DateTimeKind.Utc);

    /// <summary>Per-source results, keyed by <see cref="SourceInfo.StoreRoot"/>.</summary>
    public IReadOnlyDictionary<string, SourceStatus> Status => _status;

    private bool Due => DateTime.UtcNow - LastRefreshUtc >= _interval;

    /// <summary>
    /// Refreshes when the last refresh is older than the interval. Waits up to <paramref name="maxWait"/> (default 5 s)
    /// for a refresh or maintenance already running; after that the caller is served from what is indexed.
    /// </summary>
    public void RefreshIfDue(TimeSpan? maxWait = null, CancellationToken ct = default)
    {
        if (Catalog.IsSingleStore || !Due) return;
        Refresh(force: false, maxWait ?? TimeSpan.FromSeconds(5), ct);
    }

    /// <summary>Refreshes now (or, with <paramref name="force"/> false, only if still due once the gate is acquired).</summary>
    public List<SourceCatalog.RefreshOutcome> Refresh(bool force = true, TimeSpan? maxWait = null, CancellationToken ct = default)
    {
        if (Catalog.IsSingleStore) return [];
        if (!_gate.Wait(maxWait ?? Timeout.InfiniteTimeSpan, ct)) return [];
        try
        {
            if (!force && !Due) return [];
            var outcomes = Catalog.Refresh(ct);
            var now = DateTime.UtcNow;
            var byName = Catalog.Sources.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var o in outcomes)
            {
                if (!byName.TryGetValue(o.Source, out var src)) continue;
                var st = _status.GetOrAdd(src.StoreRoot, _ => new SourceStatus { Name = o.Source });
                st.Name = o.Source;
                if (o.Note is null)
                {
                    st.LastIndexedUtc = now;
                    if (o.Imported > 0) { st.LastImportUtc = now; st.RecordsImported += o.Imported; }
                }
                else { st.LastError = o.Note; st.LastErrorUtc = now; }
            }
            Interlocked.Exchange(ref _lastTicks, now.Ticks);
            return outcomes;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Re-reads the roots without indexing (serialized with refreshes).</summary>
    public void Discover()
    {
        if (Catalog.IsSingleStore) return;
        if (!_gate.Wait(TimeSpan.FromSeconds(5))) return; // a long migration runs: keep the current snapshot
        try { Catalog.Discover(); }
        finally { _gate.Release(); }
    }

    /// <summary>The next <see cref="RefreshIfDue"/> refreshes (after a migration registered a source).</summary>
    public void Invalidate() => Interlocked.Exchange(ref _lastTicks, DateTime.MinValue.Ticks);

    /// <summary>Runs <paramref name="action"/> while no refresh sharing this gate runs (maintenance: compaction, retention).</summary>
    public T Exclusive<T>(Func<T> action, CancellationToken ct = default)
    {
        _gate.Wait(ct);
        try { return action(); }
        finally { _gate.Release(); }
    }
}
