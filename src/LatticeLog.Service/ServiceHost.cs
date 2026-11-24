using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using LatticeLog.Mcp;
using LatticeLog.Sources;
using LatticeLog.Storage;
using LatticeLog.Storage.Format;

namespace LatticeLog.Service;

public sealed record ServiceOptions
{
    /// <summary>Config folder (null: LATTICELOG_CONFIG_DIR or the per-user default). The data folder follows from it.</summary>
    public string? ConfigDir { get; init; }
    /// <summary>Port on 127.0.0.1 (0 = any free port).</summary>
    public int Port { get; init; } = ServicePaths.DefaultPort;
    /// <summary>When <see cref="Port"/> is taken, listen on any free port instead of failing.</summary>
    public bool AnyPortIfTaken { get; init; } = true;
    /// <summary>Background refresh period (discovery, Python indexing, followed migrations).</summary>
    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Background refresh period while a web console listens for changes (<c>/v1/changes</c>): live updates.</summary>
    public TimeSpan LiveRefreshInterval { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Tool calls refresh first when the last refresh is older than this (same as in-process mode).</summary>
    public TimeSpan QueryRefreshInterval { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Compaction and retention period.</summary>
    public TimeSpan MaintenanceInterval { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>Owned stores (indexes, migrations) with more segments than this are compacted by maintenance.</summary>
    public int CompactWhenSegmentsAbove { get; init; } = 8;
    /// <summary>Project catalogs unused for this long are dropped (the per-user catalog stays).</summary>
    public TimeSpan CatalogIdleExpiry { get; init; } = TimeSpan.FromDays(1);
    public McpLimits? McpLimits { get; init; }
    public string Version { get; init; } = "0.1.0-alpha.1";
    /// <summary>Executable recorded in the endpoint file (diagnostics).</summary>
    public string? Exe { get; init; }
    /// <summary>Also write log lines here (foreground runs).</summary>
    public TextWriter? Echo { get; init; }
}

public sealed class ServiceAlreadyRunningException(string message) : IOException(message);

/// <summary>
/// The background service (specs/local-sources.md, "Background service"): one process per user and data folder,
/// listening only on 127.0.0.1. It owns the source catalogs — the per-user one and one per project file that clients
/// ask for — and is the only process that writes LatticeLog's own stores (Python indexes, migrations): refreshes and
/// maintenance share one gate, so several editors running MCP at once never contend for an index's writer lock.
/// MCP sessions arrive through the <c>latticelog mcp</c> shim over <see cref="Wire"/>; the web console and status are
/// served over HTTP on the same port.
/// </summary>
public sealed class ServiceHost : IDisposable
{
    private readonly ServiceOptions _options;
    private readonly IDisposable _lock;
    private readonly TcpListener _listener;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, SourceStatus> _status = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CatalogEntry> _catalogs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<TcpClient, byte> _connections = new();
    private readonly ManualResetEventSlim _stopped = new(false);
    private readonly CancellationTokenSource _cts = new();
    private readonly Queue<string> _errors = new();
    private readonly Queue<JsonObject> _maintenance = new();
    private readonly Thread _acceptThread, _backgroundThread;
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private int _active, _served;
    private DateTime _lastMaintenanceUtc = DateTime.MinValue;
    private int _stopping;

    private sealed class CatalogEntry(CatalogRefresher refresher, string? projectFile)
    {
        public CatalogRefresher Refresher { get; set; } = refresher;
        public string? ProjectFile { get; } = projectFile;
        public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
    }

    public ServiceEndpoint Endpoint { get; }
    public string ConfigDir { get; }
    public string DataDir { get; }
    public ServiceLog Log { get; }
    public int Port => Endpoint.Port;
    public int ActiveSessions => Volatile.Read(ref _active);
    public int SessionsServed => Volatile.Read(ref _served);
    internal McpLimits McpLimits => _options.McpLimits ?? new McpLimits { RefreshInterval = _options.QueryRefreshInterval };

    private ServiceHost(ServiceOptions options, string configDir, string dataDir, IDisposable lockStream, TcpListener listener, ServiceLog log)
    {
        _options = options;
        ConfigDir = configDir;
        DataDir = dataDir;
        _lock = lockStream;
        _listener = listener;
        Log = log;
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Endpoint = new ServiceEndpoint
        {
            Pid = Environment.ProcessId,
            Port = port,
            Token = Wire.NewToken(),
            Version = options.Version,
            StartedUtc = DateTime.UtcNow,
            ConfigDir = configDir,
            DataDir = dataDir,
            Exe = options.Exe ?? Environment.ProcessPath,
        };
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "latticelog-service-accept" };
        _backgroundThread = new Thread(BackgroundLoop) { IsBackground = true, Name = "latticelog-service-background" };
    }

    /// <summary>Starts listening and the background work. Throws <see cref="ServiceAlreadyRunningException"/> when another
    /// service owns this data folder.</summary>
    public static ServiceHost Start(ServiceOptions? options = null)
    {
        var o = options ?? new ServiceOptions();
        var configDir = ServicePaths.ResolveConfigDir(o.ConfigDir);
        var dataDir = ServicePaths.ResolveDataDir(configDir);
        Directory.CreateDirectory(ServicePaths.Dir(dataDir));
        Directory.CreateDirectory(ServicePaths.RendezvousDir(dataDir));
        // The lock where every client looks, plus the one versions up to 0.1.0-alpha.9 took: a service of that version
        // still running for this folder keeps this one from starting next to it.
        var lockFiles = new[] { ServicePaths.LockFile(dataDir), ServicePaths.LegacyLockFile(dataDir) }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var locks = new List<FileStream>();
        foreach (var lockFile in lockFiles)
        {
            try { locks.Add(new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
            catch (IOException)
            {
                foreach (var l in locks) l.Dispose();
                var ep = ServiceEndpoint.TryLoad(ServicePaths.EndpointFile(dataDir)) ?? ServiceEndpoint.TryLoad(ServicePaths.LegacyEndpointFile(dataDir));
                throw new ServiceAlreadyRunningException($"A LatticeLog service already runs for {dataDir}{(ep is null ? "" : $" (pid {ep.Pid}, port {ep.Port})")}.");
            }
        }
        var lockStream = new LockSet(locks);
        TcpListener listener;
        try
        {
            listener = Listen(o.Port);
        }
        catch (SocketException) when (o.AnyPortIfTaken && o.Port != 0)
        {
            listener = Listen(0);
        }
        catch
        {
            lockStream.Dispose();
            throw;
        }
        var log = new ServiceLog(ServicePaths.LogFile(dataDir), o.Echo);
        var host = new ServiceHost(o, configDir, dataDir, lockStream, listener, log);
        var package = PackageIsolation.RedirectedInto(dataDir);
        try
        {
            // Before the catalog opens: roots a packaged client added while an earlier service ran inside its package.
            foreach (var line in PackageIsolation.AdoptPrivateCopies(configDir, dataDir, redirected: package is not null)) log.Write(line);
            host.CatalogFor(null); // the per-user catalog: registered roots are indexed before any client connects
            var endpointFile = ServicePaths.EndpointFile(dataDir);
            host.Endpoint.Save(endpointFile);
            var legacy = ServicePaths.LegacyEndpointFile(dataDir);
            if (!ServicePaths.SamePath(legacy, endpointFile))
                try { File.Delete(legacy); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* stale either way: clients read the new file first */ }
        }
        catch
        {
            host.Dispose();
            throw;
        }
        host._acceptThread.Start();
        host._backgroundThread.Start();
        log.Write($"started: pid {host.Endpoint.Pid}, 127.0.0.1:{host.Port}, config {configDir}, data {dataDir}, version {o.Version}");
        if (package is not null)
            log.Write($"warning: started from inside the app package {package}; Windows gives this process a private copy of what it creates under AppData " +
                      "(config.json changes made now reach only that app). Started through the registered task ('latticelog service install'), the service runs outside it.");
        return host;
    }

    /// <summary>The locks a service holds while it runs (see <see cref="Start"/>).</summary>
    private sealed class LockSet(List<FileStream> locks) : IDisposable
    {
        public void Dispose()
        {
            foreach (var l in locks) l.Dispose();
        }
    }

    private static TcpListener Listen(int port)
    {
        var l = new TcpListener(IPAddress.Loopback, port);
        if (OperatingSystem.IsWindows()) l.ExclusiveAddressUse = true; // no other process can bind the same port over us
        l.Start();
        return l;
    }

    // ------------------------------------------------------------------ catalogs

    /// <summary>
    /// The catalog for a client's folder: the one of its nearest latticelog.toml, else the per-user catalog. Reopened
    /// when config.json or the project file changed.
    /// </summary>
    public CatalogRefresher CatalogFor(string? projectDir)
    {
        string? toml = null;
        if (projectDir is { Length: > 0 } && Directory.Exists(projectDir)) toml = ProjectConfig.Find(projectDir);
        var key = toml is null ? "" : PathPolicy.Normalize(toml);
        lock (_catalogs)
        {
            if (_catalogs.TryGetValue(key, out var e) && !e.Refresher.Catalog.IsStale)
            {
                e.LastUsedUtc = DateTime.UtcNow;
                return e.Refresher;
            }
            var catalog = SourceCatalog.Open(new CatalogOptions
            {
                ConfigDir = ConfigDir,
                ProjectDir = toml is null ? null : Path.GetDirectoryName(toml),
                SearchProject = toml is not null,
            });
            var r = new CatalogRefresher(catalog, _options.QueryRefreshInterval, _gate, _status);
            if (e is null) _catalogs[key] = new CatalogEntry(r, toml);
            else { e.Refresher = r; e.LastUsedUtc = DateTime.UtcNow; Log.Write($"reloaded catalog {(toml ?? "(per-user)")}: configuration changed"); }
            return r;
        }
    }

    private List<CatalogEntry> Entries()
    {
        lock (_catalogs) return _catalogs.Values.ToList();
    }

    /// <summary>All sources of all open catalogs, once per store (the per-user catalog's names first).</summary>
    public List<(SourceInfo Source, CatalogRefresher Refresher)> AllSources()
    {
        List<KeyValuePair<string, CatalogEntry>> entries;
        lock (_catalogs) entries = _catalogs.OrderBy(kv => kv.Key.Length == 0 ? 0 : 1).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<(SourceInfo, CatalogRefresher)>();
        foreach (var (_, e) in entries)
            foreach (var s in e.Refresher.Catalog.Sources)
                if (seen.Add(s.StoreRoot) && names.Add(s.Name)) list.Add((s, e.Refresher));
        return list;
    }

    /// <summary>Reloads changed catalogs, drops idle project catalogs and refreshes each one. <paramref name="maxWait"/>
    /// bounds the wait for a refresh or maintenance already running (the background tick passes zero: it skips).</summary>
    public void RefreshAll(TimeSpan? maxWait = null, CancellationToken ct = default)
    {
        foreach (var e in Entries())
        {
            ct.ThrowIfCancellationRequested();
            var key = e.ProjectFile is null ? "" : PathPolicy.Normalize(e.ProjectFile);
            if (key.Length > 0 && DateTime.UtcNow - e.LastUsedUtc > _options.CatalogIdleExpiry)
            {
                lock (_catalogs) _catalogs.Remove(key);
                continue;
            }
            try
            {
                var r = e.Refresher.Catalog.IsStale ? CatalogFor(e.ProjectFile is null ? null : Path.GetDirectoryName(e.ProjectFile)) : e.Refresher;
                foreach (var o in r.Refresh(force: true, maxWait, ct))
                {
                    if (o.Imported > 0) Log.Write($"indexed {o.Imported} new record(s) of {o.Source}");
                    if (o.Note is not null) Error($"{o.Source}: {o.Note}");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or FormatException)
            {
                Error($"refresh of {e.ProjectFile ?? "(per-user catalog)"} failed: {ex.Message}");
            }
        }
    }

    // ------------------------------------------------------------------ maintenance (compaction, retention)

    /// <summary>
    /// Compacts and applies retention to the stores LatticeLog owns: Python indexes and migrated sources. Originals and
    /// .NET stores (written by their applications) are never touched. Runs under the refresh gate.
    /// </summary>
    public JsonObject RunMaintenance(CancellationToken ct = default)
    {
        var report = new JsonObject { ["time"] = UnixTime.FormatRfc3339(UnixTime.NowUnixNanos()) };
        var items = new JsonArray();
        foreach (var (s, r) in AllSources())
        {
            ct.ThrowIfCancellationRequested();
            if (s.Kind == SourceKind.DotNet || !s.HasStore) continue;
            var retention = r.Catalog.Config.RetentionFor(s.Name);
            var item = new JsonObject { ["source"] = s.Name };
            try
            {
                r.Exclusive(() =>
                {
                    using var w = StoreWriter.Open(s.StoreRoot);
                    if (retention is not null)
                    {
                        var policy = new RetentionPolicy { MaxAge = retention.MaxAgeSpan, MaxBytes = retention.MaxMb * 1024L * 1024 };
                        var res = w.ApplyRetention(policy, keepNewest: true);
                        if (res.Deleted.Count > 0)
                        {
                            item["retention_deleted_segments"] = res.Deleted.Count;
                            item["retention_deleted_records"] = res.Records;
                            item["retention_deleted_bytes"] = res.Bytes;
                            Log.Write($"retention ({retention}) on {s.Name}: deleted {res.Deleted.Count} segment(s), {res.Records} record(s), {res.Bytes} bytes");
                        }
                    }
                    var segments = StoreReader.Open(s.StoreRoot).SegmentIds.Count;
                    if (segments > _options.CompactWhenSegmentsAbove)
                    {
                        var done = Compactor.CompactAll(s.StoreRoot, CompactionPolicy.Balanced, w.AllocateSegmentId, ct: ct);
                        if (done.Count > 0)
                        {
                            item["compacted_segments"] = done.Sum(c => c.Inputs.Count);
                            item["compaction_bytes_before"] = done.Sum(c => c.InputBytes);
                            item["compaction_bytes_after"] = done.Sum(c => c.OutputBytes);
                            Log.Write($"compacted {s.Name}: {segments} segment(s) -> {StoreReader.Open(s.StoreRoot).SegmentIds.Count}");
                        }
                    }
                    return 0;
                }, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or LlogFormatException or FormatException)
            {
                item["error"] = ex.Message;
                Error($"maintenance of {s.Name} failed: {ex.Message}");
            }
            if (item.Count > 1) items.Add((JsonNode)item);
        }
        report["sources"] = items;
        _lastMaintenanceUtc = DateTime.UtcNow;
        lock (_maintenance)
        {
            _maintenance.Enqueue(report);
            while (_maintenance.Count > 10) _maintenance.Dequeue();
        }
        return report;
    }

    private void BackgroundLoop()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            var live = Volatile.Read(ref _watchers) > 0;
            try
            {
                RefreshAll(TimeSpan.Zero, ct);
                if (DateTime.UtcNow - _lastMaintenanceUtc >= _options.MaintenanceInterval) RunMaintenance(ct);
                if (live) DetectChanges();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Error($"background work failed: {ex.GetType().Name}: {ex.Message}");
            }
            var wait = live && _options.LiveRefreshInterval < _options.RefreshInterval ? _options.LiveRefreshInterval : _options.RefreshInterval;
            if (ct.WaitHandle.WaitOne(wait)) break;
        }
    }

    // ------------------------------------------------------------------ change feed (the web console's live updates)

    /// <summary>What changed since the previous version: the sources whose events, size, newest event, status or error
    /// changed, and whether the list itself changed (a source or root added or removed).</summary>
    internal sealed record Change(long Version, IReadOnlyList<string> Sources, bool ListChanged);

    private readonly object _changeLock = new();
    private Change _lastChange = new(0, [], false);
    private Dictionary<string, string> _sourcePrints = new(StringComparer.OrdinalIgnoreCase);
    private string _rootsPrint = "";
    private int _watchers;

    internal long ChangeVersion { get { lock (_changeLock) return _lastChange.Version; } }

    internal bool Stopping => _cts.IsCancellationRequested;

    /// <summary>Counts a listener while <paramref name="body"/> runs: the background refresh runs more often meanwhile.</summary>
    internal void Watch(Action body)
    {
        Interlocked.Increment(ref _watchers);
        try { body(); }
        finally { Interlocked.Decrement(ref _watchers); }
    }

    /// <summary>The newest change after <paramref name="after"/>, waiting up to <paramref name="timeout"/>; null when
    /// nothing changed meanwhile or the service is stopping.</summary>
    internal Change? WaitForChange(long after, TimeSpan timeout)
    {
        lock (_changeLock)
        {
            if (_lastChange.Version <= after && !_cts.IsCancellationRequested) Monitor.Wait(_changeLock, timeout);
            return _lastChange.Version > after && !_cts.IsCancellationRequested ? _lastChange : null;
        }
    }

    /// <summary>
    /// Compares what the console shows of each source (the <c>/v1/sources</c> description without the times that move
    /// on every refresh) with the previous pass and publishes a new version when anything differs. Background thread only.
    /// </summary>
    private void DetectChanges()
    {
        var sources = Sources();
        var prints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in sources["sources"]?.AsArray() ?? [])
        {
            if (node is not JsonObject o || (string?)o["name"] is not { } name) continue;
            prints[name] = string.Join('|', o["records"], o["bytes"], o["last_time"], o["status"], o["last_error"], o["path"]);
        }
        var roots = string.Join('|', (sources["roots"]?.AsArray() ?? []).Select(x => (string?)x));
        var changed = prints.Where(kv => !_sourcePrints.TryGetValue(kv.Key, out var old) || old != kv.Value).Select(kv => kv.Key).ToList();
        var list = roots != _rootsPrint || prints.Count != _sourcePrints.Count || prints.Keys.Any(k => !_sourcePrints.ContainsKey(k));
        _sourcePrints = prints;
        _rootsPrint = roots;
        if (changed.Count == 0 && !list) return;
        lock (_changeLock)
        {
            _lastChange = new Change(_lastChange.Version + 1, changed, list);
            Monitor.PulseAll(_changeLock);
        }
    }

    private void Error(string message)
    {
        Log.Write("error: " + message);
        lock (_errors)
        {
            _errors.Enqueue($"{UnixTime.FormatRfc3339(UnixTime.NowUnixNanos())} {message}");
            while (_errors.Count > 50) _errors.Dequeue();
        }
    }

    // ------------------------------------------------------------------ connections

    private void AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = _listener.AcceptTcpClient(); }
            catch (SocketException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (InvalidOperationException) { break; }
            _connections[client] = 0;
            new Thread(() => Serve(client)) { IsBackground = true, Name = "latticelog-service-connection" }.Start();
        }
    }

    private void Serve(TcpClient client)
    {
        try
        {
            client.NoDelay = true;
            if (client.Client.RemoteEndPoint is IPEndPoint ep && !IPAddress.IsLoopback(ep.Address)) return;
            var stream = client.GetStream();
            stream.ReadTimeout = 10_000;
            stream.WriteTimeout = 30_000;
            var first = stream.ReadByte();
            if (first < 0) return;
            if (first == '{') ServeLine(stream, (byte)first);
            else LoopbackHttp.Serve(this, stream, (byte)first);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidDataException)
        {
            // client went away or sent garbage
        }
        catch (Exception ex)
        {
            Error($"connection failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _connections.TryRemove(client, out _);
            CloseGracefully(client);
        }
    }

    /// <summary>
    /// Ends the response, then reads what the client may still send (an ignored request body) before closing: closing a
    /// socket with unread data resets the connection, and the client would lose the response.
    /// </summary>
    private static void CloseGracefully(TcpClient client)
    {
        try
        {
            if (client.Client is { Connected: true } s)
            {
                s.Shutdown(SocketShutdown.Send);
                s.ReceiveTimeout = 500;
                var buf = new byte[8192];
                for (var total = 0; total < 1 << 20;)
                {
                    var n = s.Receive(buf);
                    if (n <= 0) break;
                    total += n;
                }
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or IOException) { /* the client already hung up: nothing to close gracefully */ }
        finally { client.Dispose(); }
    }

    internal bool Authorized(string? token) => Wire.TokenEquals(token, Endpoint.Token);

    private readonly object _refusalGate = new();
    private int _refusedSinceLog;
    private DateTime _refusalLoggedUtc = DateTime.MinValue;

    /// <summary>
    /// One log line per minute at most: a client polling with a stale endpoint file connects ten times a second.
    /// </summary>
    private void LogRefusal()
    {
        lock (_refusalGate)
        {
            _refusedSinceLog++;
            var now = DateTime.UtcNow;
            if (now - _refusalLoggedUtc < TimeSpan.FromMinutes(1)) return;
            Log.Write(_refusedSinceLog == 1
                ? "refused a connection with a wrong token (a client read another service's endpoint file)"
                : $"refused {_refusedSinceLog} connection(s) with a wrong token since {_refusalLoggedUtc:HH:mm:ss}Z (a client read another service's endpoint file)");
            _refusedSinceLog = 0;
            _refusalLoggedUtc = now;
        }
    }

    private void ServeLine(NetworkStream stream, byte first)
    {
        var hello = Wire.TryParse(Wire.ReadLine(stream, first: first));
        JsonObject Fail(string error, string message) => new() { ["ok"] = false, ["error"] = error, ["message"] = message };
        if (hello is null || (string?)hello["latticelog"] != Wire.Protocol)
        {
            Wire.WriteLine(stream, Fail("protocol", $"expected a {Wire.Protocol} handshake").ToJsonString());
            return;
        }
        if (!Authorized((string?)hello["token"]))
        {
            LogRefusal();
            Wire.WriteLine(stream, Fail("unauthorized", "wrong or missing token").ToJsonString());
            return;
        }
        var op = (string?)hello["op"];
        switch (op)
        {
            case "ping":
                Wire.WriteLine(stream, new JsonObject { ["ok"] = true, ["pid"] = Endpoint.Pid, ["version"] = Endpoint.Version }.ToJsonString());
                return;
            case "status":
                Wire.WriteLine(stream, new JsonObject { ["ok"] = true, ["status"] = Status() }.ToJsonString());
                return;
            case "refresh":
                RefreshAll(ct: _cts.Token);
                Wire.WriteLine(stream, new JsonObject { ["ok"] = true }.ToJsonString());
                return;
            case "maintenance":
                Wire.WriteLine(stream, new JsonObject { ["ok"] = true, ["report"] = RunMaintenance(_cts.Token) }.ToJsonString());
                return;
            case "stop":
                Log.Write("stop requested by a client");
                Wire.WriteLine(stream, new JsonObject { ["ok"] = true }.ToJsonString());
                ThreadPool.QueueUserWorkItem(_ => RequestStop());
                return;
            case "mcp":
                ServeMcp(stream, hello);
                return;
            case "config":
                Wire.WriteLine(stream, EditConfig(hello).ToJsonString());
                return;
            default:
                Wire.WriteLine(stream, Fail("unknown_op", $"unknown op '{op}'").ToJsonString());
                return;
        }
    }

    private readonly object _configLock = new();

    /// <summary>
    /// <c>config</c>: applies one <see cref="ConfigEdit"/> to this service's config.json and returns the result. Clients
    /// send their changes here so the file the service reads is the one that changes (see <see cref="ConfigEdit"/>).
    /// </summary>
    private JsonObject EditConfig(JsonObject hello)
    {
        static JsonObject Fail(string error, string message) => new() { ["ok"] = false, ["error"] = error, ["message"] = message };
        if ((string?)hello["config_dir"] is { Length: > 0 } cd && !PathPolicy.SamePath(Path.GetFullPath(cd), ConfigDir))
            return Fail("config_mismatch", $"this service uses the config folder {ConfigDir}, not {cd}");
        if (hello["edit"] is not JsonObject edit) return Fail("bad_request", "missing 'edit'");
        try
        {
            lock (_configLock)
            {
                var config = UserConfig.Load(ConfigDir);
                var changed = ConfigEdit.Apply(config, edit);
                if (changed) Log.Write($"config changed by a client: {edit.ToJsonString()}");
                return new JsonObject { ["ok"] = true, ["changed"] = changed, ["config"] = ConfigEdit.Snapshot(config) };
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return Fail("bad_request", ex.Message);
        }
    }

    private void ServeMcp(NetworkStream stream, JsonObject hello)
    {
        if ((string?)hello["config_dir"] is { Length: > 0 } cd && !PathPolicy.SamePath(Path.GetFullPath(cd), ConfigDir))
        {
            Wire.WriteLine(stream, new JsonObject { ["ok"] = false, ["error"] = "config_mismatch", ["message"] = $"this service uses the config folder {ConfigDir}, not {cd}" }.ToJsonString());
            return;
        }
        CatalogRefresher refresher;
        try { refresher = CatalogFor((string?)hello["project_dir"]); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or FormatException)
        {
            Wire.WriteLine(stream, new JsonObject { ["ok"] = false, ["error"] = "catalog", ["message"] = ex.Message }.ToJsonString());
            return;
        }
        Wire.WriteLine(stream, new JsonObject { ["ok"] = true, ["pid"] = Endpoint.Pid, ["version"] = Endpoint.Version, ["project_file"] = refresher.Catalog.Project?.FilePath }.ToJsonString());
        stream.ReadTimeout = Timeout.Infinite;
        stream.WriteTimeout = Timeout.Infinite;
        Interlocked.Increment(ref _active);
        Interlocked.Increment(ref _served);
        var client = (string?)hello["client"] ?? "mcp";
        Log.Write($"mcp session opened ({client}, project {refresher.Catalog.Project?.FilePath ?? "none"})");
        try
        {
            McpServer.Run(new McpServer(refresher, McpLimits, servedBy: "service"), stream, stream, Log.AsWriter("[mcp] "));
        }
        finally
        {
            Interlocked.Decrement(ref _active);
            Log.Write($"mcp session closed ({client})");
        }
    }

    // ------------------------------------------------------------------ status

    /// <summary>One source as <c>list_sources</c> describes it, plus what the service knows: last indexed time, last
    /// error and retention.</summary>
    private JsonObject DescribeWithStatus(SourceInfo s, CatalogRefresher r)
    {
        var o = SourceCatalog.Describe(s);
        if (_status.TryGetValue(s.StoreRoot, out var st))
        {
            if (st.LastIndexedUtc is { } li) o["last_indexed"] = Time(li);
            if (st.LastImportUtc is { } lim) o["last_new_records"] = Time(lim);
            o["records_indexed_by_service"] = st.RecordsImported;
            if (st.LastError is not null) { o["last_error"] = st.LastError; o["last_error_time"] = Time(st.LastErrorUtc!.Value); }
        }
        else if (s.Kind is SourceKind.Python || (s.Kind is SourceKind.Migrated && s.Follow)) o["last_indexed"] = null;
        if (s.Kind != SourceKind.DotNet && r.Catalog.Config.RetentionFor(s.Name) is { } ret) o["retention"] = ret.ToString();
        return o;
    }

    /// <summary>
    /// What the console's source explorer shows: every source (with or without data yet), the registered roots (also
    /// the empty ones) and the data folder. <paramref name="discover"/> rescans the roots first, so a service folder
    /// created a moment ago appears without waiting for the background refresh.
    /// </summary>
    public JsonObject Sources(bool discover = false)
    {
        var entries = Entries();
        if (discover) foreach (var e in entries) e.Refresher.Discover();
        var roots = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries) foreach (var root in e.Refresher.Catalog.Config.Roots) roots.Add(PathPolicy.Normalize(root));
        return new JsonObject
        {
            ["format"] = "latticelog.sources.v1",
            ["served_by"] = "service",
            ["sources"] = new JsonArray(AllSources().Select(x => (JsonNode?)DescribeWithStatus(x.Source, x.Refresher)).ToArray()),
            ["roots"] = new JsonArray(roots.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            ["data_folder"] = DataDir,
            ["warnings"] = new JsonArray(entries.SelectMany(e => e.Refresher.Catalog.Warnings).Distinct().Select(w => (JsonNode?)JsonValue.Create(w)).ToArray()),
        };
    }

    public JsonObject Status()
    {
        var sources = new JsonArray();
        foreach (var (s, r) in AllSources()) sources.Add((JsonNode)DescribeWithStatus(s, r));
        var entries = Entries();
        var lastRefresh = entries.Select(e => e.Refresher.LastRefreshUtc).DefaultIfEmpty(DateTime.MinValue).Max();
        var warnings = entries.SelectMany(e => e.Refresher.Catalog.Warnings).Distinct().ToList();
        long total = 0, index = 0, migrated = 0;
        try
        {
            foreach (var f in new DirectoryInfo(DataDir).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                long len;
                try { len = f.Length; } catch (IOException) { continue; }
                total += len;
                var rel = Path.GetRelativePath(DataDir, f.FullName);
                if (rel.StartsWith("index" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) index += len;
                else if (rel.StartsWith("migrated" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) migrated += len;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a file removed or locked meanwhile: disk use is a best-effort figure */ }
        JsonArray errors;
        lock (_errors) errors = new JsonArray(_errors.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray());
        JsonArray maintenance;
        lock (_maintenance) maintenance = new JsonArray(_maintenance.Select(m => m.DeepClone()).ToArray());
        return new JsonObject
        {
            ["format"] = "latticelog.service.status.v1",
            ["running"] = true,
            ["pid"] = Endpoint.Pid,
            ["version"] = Endpoint.Version,
            ["started"] = Time(Endpoint.StartedUtc),
            ["uptime_seconds"] = (long)_uptime.Elapsed.TotalSeconds,
            ["listen"] = $"127.0.0.1:{Port.ToString(CultureInfo.InvariantCulture)}",
            ["console"] = new Uri(Endpoint.BaseUri, "ui").ToString(),
            ["config_dir"] = ConfigDir,
            ["data_dir"] = DataDir,
            ["log_file"] = Log.Path,
            ["sessions"] = new JsonObject { ["active"] = ActiveSessions, ["served"] = SessionsServed },
            ["last_refresh"] = lastRefresh == DateTime.MinValue ? null : Time(lastRefresh),
            ["refresh_interval_seconds"] = _options.RefreshInterval.TotalSeconds,
            ["last_maintenance"] = _lastMaintenanceUtc == DateTime.MinValue ? null : Time(_lastMaintenanceUtc),
            ["disk"] = new JsonObject { ["data_folder_bytes"] = total, ["index_bytes"] = index, ["migrated_bytes"] = migrated },
            ["catalogs"] = new JsonArray(entries.Select(e => (JsonNode?)new JsonObject
            {
                ["project_file"] = e.ProjectFile,
                ["sources"] = e.Refresher.Catalog.Sources.Count,
                ["roots"] = new JsonArray(e.Refresher.Catalog.Config.Roots.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                ["last_used"] = Time(e.LastUsedUtc),
            }).ToArray()),
            ["sources"] = sources,
            ["warnings"] = new JsonArray(warnings.Select(w => (JsonNode?)JsonValue.Create(w)).ToArray()),
            ["errors"] = errors,
            ["maintenance"] = maintenance,
        };
    }

    private static string Time(DateTime utc) => UnixTime.FormatRfc3339((utc.ToUniversalTime().Ticks - DateTime.UnixEpoch.Ticks) * 100);

    // ------------------------------------------------------------------ stop

    public WaitHandle Stopped => _stopped.WaitHandle;

    public bool WaitForStop(TimeSpan timeout) => _stopped.Wait(timeout);

    /// <summary>Stops listening, closes every connection (shims reconnect or fall back), waits for background work and
    /// removes the endpoint file.</summary>
    public void RequestStop()
    {
        if (Interlocked.Exchange(ref _stopping, 1) == 1) { _stopped.Wait(); return; }
        try
        {
            _cts.Cancel();
            lock (_changeLock) Monitor.PulseAll(_changeLock); // change listeners return now
            try { _listener.Stop(); } catch (SocketException) { /* already stopped */ }
            foreach (var c in _connections.Keys)
            {
                try { c.Client?.Shutdown(SocketShutdown.Both); } catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { /* the client already disconnected */ }
                try { c.Dispose(); } catch (ObjectDisposedException) { /* already disposed */ }
            }
            if (_backgroundThread.IsAlive && Thread.CurrentThread != _backgroundThread) _backgroundThread.Join(TimeSpan.FromSeconds(30));
            var ep = ServiceEndpoint.TryLoad(ServicePaths.EndpointFile(DataDir));
            if (ep is not null && ep.Pid == Endpoint.Pid && ep.Port == Endpoint.Port)
                try { File.Delete(ServicePaths.EndpointFile(DataDir)); } catch (IOException) { /* already gone or held: the next start overwrites it */ }
            Log.Write("stopped");
        }
        finally
        {
            _lock.Dispose();
            _stopped.Set();
        }
    }

    public void Dispose() => RequestStop();
}
