using System.Text.RegularExpressions;

namespace LatticeLog.Server;

/// <summary>
/// Namespaces (P17-T01): <c>tenant[/project[/environment]]</c>, lowercase letters, digits, <c>.</c>, <c>_</c> and
/// <c>-</c>. Each namespace is a separate store (<c>&lt;data&gt;/ns/&lt;tenant~project~env&gt;</c>): data, sequence
/// numbers, dedup state and indexes never mix, and a query can only see one namespace.
/// </summary>
public static partial class NamespaceNames
{
    public const string Default = "default";

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,62}(/[a-z0-9][a-z0-9._-]{0,62}){0,2}$")]
    private static partial Regex Pattern();

    public static bool IsValid(string? ns) => ns is not null && Pattern().IsMatch(ns) && !ns.Contains("..", StringComparison.Ordinal);

    /// <summary>Directory name for a valid namespace (no separators, no traversal).</summary>
    public static string DirectoryName(string ns) => ns.Replace('/', '~');

    public static string FromDirectoryName(string dir) => dir.Replace('~', '/');
}

/// <summary>Opens one <see cref="IngestService"/> per namespace on first use and owns them.</summary>
public sealed class NamespaceRegistry : IDisposable
{
    private readonly string _root;
    private readonly IngestServiceOptions _template;
    private readonly Dictionary<string, IngestService> _open = new(StringComparer.Ordinal);
    private bool _disposed;

    public NamespaceRegistry(string dataDirectory, IngestServiceOptions template)
    {
        _root = Path.Combine(dataDirectory, "ns");
        _template = template;
        Directory.CreateDirectory(_root);
    }

    public string StorePath(string ns) => Path.Combine(_root, NamespaceNames.DirectoryName(ns));

    public bool Exists(string ns) => StoreLayoutExists(StorePath(ns));

    private static bool StoreLayoutExists(string path) => File.Exists(Path.Combine(path, "STORE.json"));

    /// <summary>Namespaces that have a store on disk.</summary>
    public IReadOnlyList<string> List()
        => Directory.EnumerateDirectories(_root).Where(StoreLayoutExists).Select(d => NamespaceNames.FromDirectoryName(Path.GetFileName(d)))
            .Where(NamespaceNames.IsValid).Order(StringComparer.Ordinal).ToList();

    /// <summary>The ingestion service (single writer) of a namespace, opened on demand.</summary>
    public IngestService Ingest(string ns)
    {
        if (!NamespaceNames.IsValid(ns)) throw new ArgumentException($"invalid namespace '{ns}'");
        lock (_open)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_open.TryGetValue(ns, out var svc))
                _open[ns] = svc = new IngestService(_template with { StoreDirectory = StorePath(ns) });
            return svc;
        }
    }

    public IReadOnlyList<(string Namespace, IngestService Service)> OpenServices()
    {
        lock (_open) return _open.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>Test-only crash simulation for every open namespace.</summary>
    public void Abort()
    {
        lock (_open)
        {
            _disposed = true;
            foreach (var s in _open.Values) s.Abort();
            _open.Clear();
        }
    }

    public void Dispose()
    {
        lock (_open)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var s in _open.Values) s.Dispose();
            _open.Clear();
        }
    }
}
