using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LatticeLog.Mcp;
using LatticeLog.Sources;

namespace LatticeLog.Service;

public sealed record ShimOptions
{
    public string? ConfigDir { get; init; }
    /// <summary>Folder whose latticelog.toml selects the project catalog (default: the current directory).</summary>
    public string? ProjectDir { get; init; }
    /// <summary>Serve in-process only (no service): <c>latticelog mcp --no-service</c> or LATTICELOG_NO_SERVICE=1.</summary>
    public bool NoService { get; init; }
    /// <summary>Starts the service when none answers; returns its endpoint or null. Default: <see cref="ServiceLauncher.EnsureRunning"/>.</summary>
    public Func<ServiceEndpoint?>? AutoStart { get; init; }
    /// <summary>The in-process fallback's catalog. Default: the same catalog the service would use.</summary>
    public Func<SourceCatalog>? InProcessCatalog { get; init; }
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>After stdin closes, how long to wait for answers to requests already sent.</summary>
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TextWriter? Log { get; init; }
    public string ClientName { get; init; } = "latticelog mcp";
}

/// <summary>
/// <c>latticelog mcp</c>: a thin stdio shim. MCP lines from the client go to the background service and its lines come
/// back unchanged, so tools and results are those of the service's MCP server. When no service answers it is started;
/// when it cannot start, the shim serves in-process exactly as before the service existed. If the service goes away
/// mid-session (stopped, restarted, upgraded), requests in flight get a JSON-RPC error, the shim reconnects (or falls
/// back) and replays <c>initialize</c>, so the client keeps its session.
/// </summary>
public sealed class McpShim
{
    private const string ReplayIdPrefix = "latticelog-shim-replay-";
    private readonly ShimOptions _o;
    private readonly StreamWriter _out;
    private readonly object _outGate = new();
    private readonly object _sync = new();
    /// <summary>Requests sent and not answered yet: the line, the backend it went to and how often it was sent.</summary>
    private readonly Dictionary<string, (string Line, Backend? To, int Sends)> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _replayIds = new(StringComparer.Ordinal);
    private Backend? _backend;
    private string? _initializeLine, _initializedLine;
    private int _replays;
    private bool _serviceFailed;
    private bool _closing;

    /// <summary>What served the session last: <c>service</c> or <c>in-process</c> (tests, diagnostics).</summary>
    public string? Mode { get; private set; }
    public int Connects { get; private set; }

    private McpShim(ShimOptions o, Stream stdout)
    {
        _o = o;
        _out = new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
    }

    public static int Run(Stream stdin, Stream stdout, ShimOptions options) => Create(options, stdout).Serve(stdin);

    public static McpShim Create(ShimOptions options, Stream stdout) => new(options, stdout);

    public int Serve(Stream stdin)
    {
        using var reader = new StreamReader(stdin, new UTF8Encoding(false));
        lock (_sync) EnsureBackend();
        try
        {
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;
                var msg = Parse(line);
                var method = (string?)msg?["method"];
                var id = msg?["id"]?.ToJsonString();
                lock (_sync)
                {
                    if (method == "initialize") _initializeLine = line;
                    else if (method == "notifications/initialized") _initializedLine = line;
                    var request = id is not null && method is not null;
                    if (request) _pending[id!] = (line, null, 0);
                    Dispatch(request ? id : null, line);
                }
            }
            // stdin closed: answer what was already asked (a client may write its requests and close its end).
            lock (_sync)
            {
                var deadline = DateTime.UtcNow + _o.DrainTimeout;
                while (_pending.Count > 0 && DateTime.UtcNow < deadline) Monitor.Wait(_sync, TimeSpan.FromMilliseconds(200));
            }
        }
        finally
        {
            lock (_sync) { _closing = true; _backend?.Dispose(); _backend = null; }
        }
        return 0;
    }

    /// <summary>Sends a line to the current backend, reconnecting when it is gone. Called with _sync held.</summary>
    private void Dispatch(string? id, string line)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var b = EnsureBackend();
            if (b.TrySend(line))
            {
                if (id is not null && _pending.TryGetValue(id, out var p)) _pending[id] = (p.Line, b, p.Sends + 1);
                return;
            }
            b.Closed = true;
        }
        if (id is not null) Fail(id, "The LatticeLog service could not be reached; retry the call.");
    }

    private static JsonObject? Parse(string line)
    {
        try { return JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private void Write(string line)
    {
        lock (_outGate) _out.WriteLine(line);
    }

    /// <summary>Called with _sync held.</summary>
    private void Fail(string idJson, string message)
    {
        Write($"{{\"jsonrpc\":\"2.0\",\"id\":{idJson},\"error\":{{\"code\":-32603,\"message\":{JsonValue.Create(message).ToJsonString()}}}}}");
        _pending.Remove(idJson);
        Monitor.PulseAll(_sync);
    }

    private void Log(string message) => _o.Log?.WriteLine("[latticelog mcp] " + message);

    // ------------------------------------------------------------------ backends

    /// <summary>Called with _sync held.</summary>
    private Backend EnsureBackend()
    {
        if (_backend is { Closed: false } b) return b;
        _backend?.Dispose();
        _backend = Connect();
        Connects++;
        Replay(_backend);
        return _backend;
    }

    private Backend Connect()
    {
        if (!_o.NoService && !_serviceFailed)
        {
            var reason = "";
            try
            {
                var ep = ServiceClient.FindEndpoint(_o.ConfigDir);
                if (ep is not null)
                {
                    try { return ServiceBackend(ep); }
                    catch (Exception ex) when (ex is IOException or SocketException) { reason = ex.Message; }
                }
                var start = _o.AutoStart ?? (() => ServiceLauncher.EnsureRunning(_o.ConfigDir, TimeSpan.FromSeconds(15), Log));
                ep = start();
                if (ep is not null) return ServiceBackend(ep);
                reason = "it did not start";
            }
            catch (ServiceRefusedException ex) { reason = ex.Message; }
            catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException or UnauthorizedAccessException)
            {
                reason = ex.Message;
            }
            _serviceFailed = true; // for this session: do not wait for a start again on every reconnect
            Log($"background service unavailable ({reason}); serving in-process");
        }
        Mode = "in-process";
        var catalog = _o.InProcessCatalog?.Invoke() ?? SourceCatalog.Open(new CatalogOptions { ConfigDir = _o.ConfigDir, ProjectDir = _o.ProjectDir });
        return new InProcessBackend(catalog, this, _o.Log ?? TextWriter.Null);
    }

    private Backend ServiceBackend(ServiceEndpoint ep)
    {
        var extra = new JsonObject
        {
            ["project_dir"] = Path.GetFullPath(_o.ProjectDir ?? Environment.CurrentDirectory),
            ["config_dir"] = ServicePaths.ResolveConfigDir(_o.ConfigDir),
            ["client"] = _o.ClientName,
        };
        var (client, stream, _) = ServiceClient.Connect(ep, "mcp", _o.ConnectTimeout, extra);
        Mode = "service";
        return new SocketBackend(client, stream, this);
    }

    /// <summary>A new backend gets the client's initialize again (its answer is swallowed) and the initialized notification.</summary>
    private void Replay(Backend b)
    {
        if (_initializeLine is null) return;
        var init = Parse(_initializeLine)!;
        var rid = ReplayIdPrefix + (++_replays);
        init["id"] = rid;
        _replayIds.Add(JsonValue.Create(rid).ToJsonString());
        b.TrySend(init.ToJsonString());
        if (_initializedLine is not null) b.TrySend(_initializedLine);
    }

    /// <summary>A line from the backend: forwarded unless it answers a replayed initialize.</summary>
    private void FromBackend(string line)
    {
        var id = Parse(line) is { } msg && msg["method"] is null ? msg["id"]?.ToJsonString() : null;
        if (id is not null)
            lock (_sync)
                if (_replayIds.Remove(id)) return;
        Write(line);
        if (id is not null)
            lock (_sync)
                if (_pending.Remove(id)) Monitor.PulseAll(_sync); // after the write: the drain at stdin EOF must not exit before it
    }

    /// <summary>
    /// The backend closed (from its reader thread, after its last line). Requests it had not answered are sent again to
    /// a new backend — every tool is safe to repeat (reads; migrate_logs is idempotent) — once; a second loss answers
    /// them with an error.
    /// </summary>
    private void Lost(Backend b)
    {
        lock (_sync)
        {
            if (b.LossHandled) return;
            b.LossHandled = true;
            b.Dispose();
            if (ReferenceEquals(_backend, b)) _backend = null;
            if (_closing) return;
            var orphans = _pending.Where(kv => ReferenceEquals(kv.Value.To, b)).ToList();
            if (orphans.Count == 0) return;
            Log($"connection to the {Mode} backend lost with {orphans.Count} request(s) in flight; resending them");
            foreach (var (id, p) in orphans)
            {
                if (p.Sends >= 2) Fail(id, "The LatticeLog service connection was lost twice while this request ran; retry the call.");
                else Dispatch(id, p.Line);
            }
        }
    }

    private abstract class Backend : IDisposable
    {
        public volatile bool Closed;
        public bool LossHandled;
        public abstract bool TrySend(string line);
        public abstract void Dispose();
    }

    private sealed class SocketBackend : Backend
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly object _writeGate = new();

        public SocketBackend(TcpClient client, NetworkStream stream, McpShim shim)
        {
            _client = client;
            _stream = stream;
            var t = new Thread(() =>
            {
                try
                {
                    using var r = new StreamReader(stream, new UTF8Encoding(false), false, 64 * 1024, leaveOpen: true);
                    while (r.ReadLine() is { } line) shim.FromBackend(line);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) { }
                Closed = true;
                shim.Lost(this);
            })
            { IsBackground = true, Name = "latticelog-shim-reader" };
            t.Start();
        }

        public override bool TrySend(string line)
        {
            if (Closed) return false;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                lock (_writeGate) { _stream.Write(bytes); _stream.Flush(); }
                return true;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
            {
                Closed = true;
                return false;
            }
        }

        public override void Dispose()
        {
            Closed = true;
            try { _client.Dispose(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>Today's in-process MCP server, behind in-memory pipes (same request loop, cancellation and progress).</summary>
    private sealed class InProcessBackend : Backend
    {
        private readonly AnonymousPipeServerStream _toServer;
        private readonly AnonymousPipeServerStream _fromServer;
        private readonly AnonymousPipeClientStream _serverIn, _serverOut;
        private readonly StreamWriter _writer;

        public InProcessBackend(SourceCatalog catalog, McpShim shim, TextWriter log)
        {
            _toServer = new AnonymousPipeServerStream(PipeDirection.Out);
            _serverIn = new AnonymousPipeClientStream(PipeDirection.In, _toServer.ClientSafePipeHandle);
            _fromServer = new AnonymousPipeServerStream(PipeDirection.In);
            _serverOut = new AnonymousPipeClientStream(PipeDirection.Out, _fromServer.ClientSafePipeHandle);
            _writer = new StreamWriter(_toServer, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            new Thread(() =>
            {
                try { McpServer.Run(new McpServer(catalog), _serverIn, _serverOut, log); }
                catch (IOException) { }
                finally { try { _serverOut.Dispose(); } catch (IOException) { } }
            })
            { IsBackground = true, Name = "latticelog-mcp-inprocess" }.Start();
            new Thread(() =>
            {
                try
                {
                    using var r = new StreamReader(_fromServer, new UTF8Encoding(false));
                    while (r.ReadLine() is { } line) shim.FromBackend(line);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
                Closed = true;
            })
            { IsBackground = true, Name = "latticelog-shim-inprocess-reader" }.Start();
        }

        public override bool TrySend(string line)
        {
            if (Closed) return false;
            try { _writer.WriteLine(line); return true; }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { Closed = true; return false; }
        }

        public override void Dispose()
        {
            Closed = true;
            try { _writer.Dispose(); } catch (IOException) { }
        }
    }
}
