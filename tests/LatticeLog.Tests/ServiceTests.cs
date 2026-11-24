using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using LatticeLog.Service;
using LatticeLog.Sources;
using LatticeLog.Storage;
using Xunit.Abstractions;
using static LatticeLog.Tests.SourcesTests;

namespace LatticeLog.Tests;

/// <summary>
/// Background service (specs/local-sources.md, "Background service"): the MCP shim and the service, auto-start,
/// in-process fallback, concurrent clients, restarts, the loopback console and per-user registration plans. Every test
/// uses its own config/data folders and a free port; nothing is registered on the machine.
/// </summary>
public class ServiceTests(ITestOutputHelper output)
{
    private static ServiceHost StartHost(Env env, TimeSpan? refresh = null) => ServiceHost.Start(new ServiceOptions
    {
        ConfigDir = env.ConfigDir,
        Port = 0,
        RefreshInterval = refresh ?? TimeSpan.FromMilliseconds(200),
        QueryRefreshInterval = TimeSpan.FromMilliseconds(100),
        MaintenanceInterval = TimeSpan.FromHours(1),
    });

    /// <summary>A Python local-mode service "orders" with <paramref name="n"/> records, every 10th an error.</summary>
    private static string Orders(Env env, int n, ulong first = 1)
    {
        var dir = Directory.Exists(Path.Combine(env.Root, "orders")) ? Path.Combine(env.Root, "orders") : PythonSource(env.Root, "orders");
        var file = Directory.EnumerateFiles(dir, "latticelog-*.jsonl", SearchOption.AllDirectories).FirstOrDefault() ?? StartFile(dir, "p-1", first, "orders");
        AppendOrders(file, first, n);
        return file;
    }

    private static void AppendOrders(string file, ulong first, int n)
    {
        var sb = new StringBuilder();
        for (var i = 0UL; i < (ulong)n; i++)
        {
            var seq = first + i;
            sb.Append(seq % 10 == 0 ? Rec(seq, "orders", 17, "orders.failed", "payment declined", "PaymentDeclined") : Rec(seq, "orders", 9, "orders.ok", "ok")).Append('\n');
        }
        Append(file, sb.ToString());
    }

    /// <summary>The shim on in-memory stdio, driven like an MCP client.</summary>
    private sealed class ShimClient : IDisposable
    {
        private readonly AnonymousPipeServerStream _stdinWriter = new(PipeDirection.Out);
        private readonly AnonymousPipeClientStream _stdinReader;
        private readonly AnonymousPipeServerStream _stdoutReader = new(PipeDirection.In);
        private readonly AnonymousPipeClientStream _stdoutWriter;
        private readonly StreamWriter _w;
        private readonly BlockingCollection<JsonObject> _lines = [];
        private readonly Thread _shimThread;
        private int _id;

        public McpShim Shim { get; }
        public StringWriter Log { get; } = new();

        public ShimClient(ShimOptions options)
        {
            _stdinReader = new AnonymousPipeClientStream(PipeDirection.In, _stdinWriter.ClientSafePipeHandle);
            _stdoutWriter = new AnonymousPipeClientStream(PipeDirection.Out, _stdoutReader.ClientSafePipeHandle);
            Shim = McpShim.Create(options with { Log = TextWriter.Synchronized(Log) }, _stdoutWriter);
            _shimThread = new Thread(() => { Shim.Serve(_stdinReader); _stdoutWriter.Dispose(); }) { IsBackground = true };
            _shimThread.Start();
            new Thread(() =>
            {
                using var r = new StreamReader(_stdoutReader);
                try { while (r.ReadLine() is { } l) _lines.Add((JsonObject)JsonNode.Parse(l)!); }
                catch (IOException) { }
                _lines.CompleteAdding();
            })
            { IsBackground = true }.Start();
            _w = new StreamWriter(_stdinWriter, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            var init = Request("initialize", new JsonObject { ["protocolVersion"] = "2025-11-25", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "test", ["version"] = "1" } });
            Assert.Equal("latticelog", (string?)init["result"]?["serverInfo"]?["name"]);
            _w.WriteLine("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        }

        public JsonObject Request(string method, JsonObject? p = null)
        {
            var id = Interlocked.Increment(ref _id);
            _w.WriteLine(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = p ?? new JsonObject() }.ToJsonString());
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(120))
            {
                if (!_lines.TryTake(out var msg, TimeSpan.FromSeconds(1))) { if (_lines.IsCompleted) break; continue; }
                if ((int?)msg["id"] == id) return msg;
            }
            throw new TimeoutException($"no response to {method} #{id}; shim log: {Log}");
        }

        /// <summary>tools/call; returns the result (isError, structuredContent).</summary>
        public JsonObject Tool(string name, JsonObject? args = null)
        {
            var r = Request("tools/call", new JsonObject { ["name"] = name, ["arguments"] = args ?? new JsonObject() });
            return r["result"] as JsonObject ?? throw new InvalidOperationException("JSON-RPC error: " + r.ToJsonString());
        }

        public void Dispose()
        {
            _w.Dispose();
            _shimThread.Join(TimeSpan.FromSeconds(30));
        }
    }

    private static ShimOptions Options(Env env, Func<ServiceEndpoint?>? autoStart = null) => new()
    {
        ConfigDir = env.ConfigDir,
        ProjectDir = env.Dir,
        AutoStart = autoStart ?? (() => null),
        ConnectTimeout = TimeSpan.FromSeconds(5),
    };

    private static JsonObject Ok(JsonObject result)
    {
        Assert.False((bool?)result["isError"] ?? false, result.ToJsonString());
        return (JsonObject)result["structuredContent"]!;
    }

    // ------------------------------------------------------------------ shim ↔ service

    [Fact]
    public void Shim_forwards_mcp_to_the_service_and_back()
    {
        var env = new Env();
        Orders(env, 200);
        using var host = StartHost(env);
        using var client = new ShimClient(Options(env));
        var sources = Ok(client.Tool("list_sources"));
        Assert.Equal("service", (string?)sources["served_by"]);
        Assert.Contains(sources["sources"]!.AsArray(), s => (string?)s!["name"] == "orders" && (long)s["records"]! == 200);
        var groups = Ok(client.Tool("get_error_groups", new JsonObject { ["source"] = "orders" }));
        Assert.Equal(20, groups["groups"]!.AsArray().Sum(g => (long)g!["count"]!));
        var tools = client.Request("tools/list")["result"]!["tools"]!.AsArray().Select(t => (string)t!["name"]!).ToList();
        Assert.Contains("migrate_logs", tools); // same tool set as in-process
        Assert.Equal("service", client.Shim.Mode);
        Assert.Equal(1, host.SessionsServed);
        Assert.Equal(1, host.ActiveSessions);
    }

    [Fact]
    public void Shim_starts_the_service_when_none_is_running()
    {
        var env = new Env();
        Orders(env, 50);
        ServiceHost? started = null;
        var calls = 0;
        try
        {
            using (var client = new ShimClient(Options(env, () => { calls++; started = StartHost(env); return started.Endpoint; })))
            {
                Assert.Equal("service", (string?)Ok(client.Tool("list_sources"))["served_by"]);
                Assert.Equal(50, Ok(client.Tool("search_events", new JsonObject { ["source"] = "orders", ["limit"] = 200 }))["events"]!.AsArray().Count);
            }
            Assert.Equal(1, calls);
            Assert.NotNull(started);
            // A second client finds the running service through its endpoint file: no second start.
            using var second = new ShimClient(Options(env, () => throw new InvalidOperationException("must not start again")));
            Assert.Equal("service", (string?)Ok(second.Tool("list_sources"))["served_by"]);
            Assert.Equal(2, started!.SessionsServed);
        }
        finally { started?.Dispose(); }
    }

    [Fact]
    public void Shim_falls_back_to_in_process_when_the_service_cannot_start()
    {
        var env = new Env();
        Orders(env, 30);
        using var client = new ShimClient(Options(env, () => null));
        var sources = Ok(client.Tool("list_sources"));
        Assert.Equal("in-process", (string?)sources["served_by"]);
        Assert.Contains(sources["sources"]!.AsArray(), s => (string?)s!["name"] == "orders");
        Assert.Equal(3, Ok(client.Tool("get_error_groups", new JsonObject { ["source"] = "orders" }))["groups"]!.AsArray().Sum(g => (long)g!["count"]!));
        Assert.Contains("serving in-process", client.Log.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(ServicePaths.EndpointFile(env.DataDir)));
    }

    [Fact]
    public void Stale_endpoint_file_and_wrong_token_fall_back_without_hanging()
    {
        var env = new Env();
        Orders(env, 10);
        // A crashed service left its endpoint file behind, pointing at a port nobody listens on.
        new ServiceEndpoint { Pid = 999999, Port = 1, Token = "x", Version = "0", StartedUtc = DateTime.UtcNow, ConfigDir = env.ConfigDir, DataDir = env.DataDir }
            .Save(ServicePaths.EndpointFile(env.DataDir));
        var sw = Stopwatch.StartNew();
        using (var client = new ShimClient(Options(env, () => null)))
            Assert.Equal("in-process", (string?)Ok(client.Tool("list_sources"))["served_by"]);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"fallback took {sw.Elapsed}");

        using var host = StartHost(env);
        var ep = host.Endpoint with { Token = new string('0', 64) };
        Assert.Throws<ServiceRefusedException>(() => ServiceClient.Connect(ep, "status", TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Shim_reconnects_after_a_service_restart_and_keeps_the_session()
    {
        var env = new Env();
        Orders(env, 40);
        var first = StartHost(env);
        ServiceHost? second = null;
        try
        {
            using var client = new ShimClient(Options(env, () => (second = StartHost(env)).Endpoint));
            Assert.Equal("service", (string?)Ok(client.Tool("list_sources"))["served_by"]);
            first.Dispose(); // stop (or upgrade) while the editor keeps its MCP session
            var after = Ok(client.Tool("search_events", new JsonObject { ["source"] = "orders", ["limit"] = 100 }));
            Assert.Equal(40, after["events"]!.AsArray().Count);
            Assert.NotNull(second);
            Assert.Equal(1, second!.SessionsServed);
            Assert.Equal("service", client.Shim.Mode);
            Assert.Equal(2, client.Shim.Connects);
        }
        finally
        {
            first.Dispose();
            second?.Dispose();
        }
    }

    // ------------------------------------------------------------------ concurrency and restarts

    [Fact]
    public void Two_mcp_clients_query_one_live_source_without_lock_errors()
    {
        var env = new Env();
        var file = Orders(env, 500);
        using var host = StartHost(env, TimeSpan.FromMilliseconds(50));
        using var a = new ShimClient(Options(env));
        using var b = new ShimClient(Options(env));
        using var stop = new CancellationTokenSource();
        var written = 500;
        var writer = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                AppendOrders(file, (ulong)written + 1, 50);
                written += 50;
                Thread.Sleep(20);
            }
        });
        var problems = new ConcurrentBag<string>();
        void Hammer(ShimClient c)
        {
            for (var i = 0; i < 40; i++)
            {
                foreach (var (tool, args) in new[]
                {
                    ("search_events", new JsonObject { ["source"] = "orders", ["where"] = "level >= ERROR", ["limit"] = 20, ["newest_first"] = true }),
                    ("get_error_groups", new JsonObject { ["source"] = "orders" }),
                    ("list_sources", new JsonObject()),
                })
                {
                    var r = c.Tool(tool, args);
                    if ((bool?)r["isError"] == true) problems.Add($"{tool}: {r["content"]![0]!["text"]}");
                    else if (tool == "list_sources")
                        foreach (var w in r["structuredContent"]!["warnings"]!.AsArray())
                            problems.Add("warning: " + w);
                }
            }
        }
        Parallel.Invoke(() => Hammer(a), () => Hammer(b));
        stop.Cancel();
        writer.Wait();
        Assert.Empty(problems);
        var status = host.Status();
        var orders = status["sources"]!.AsArray().Single(s => (string?)s!["name"] == "orders")!;
        Assert.Null(orders["last_error"]);
        Assert.Empty(status["errors"]!.AsArray());
        // Everything written is indexed exactly once.
        host.RefreshAll();
        var src = host.AllSources().Single(s => s.Source.Name == "orders").Source;
        Assert.Equal(written, Count(src));
        output.WriteLine($"{written} records written while 2 clients ran 240 tool calls; sessions served {host.SessionsServed}");
    }

    [Fact]
    public void Service_restart_resumes_checkpoints_without_duplicates()
    {
        var env = new Env();
        var file = Orders(env, 60_000);
        // Stopped while the background refresh is importing: the checkpoint stays consistent.
        var host = StartHost(env, TimeSpan.FromMilliseconds(10));
        Thread.Sleep(150);
        host.Dispose();
        var orders = host.AllSources().Single(s => s.Source.Name == "orders").Source;
        output.WriteLine($"indexed when stopped: {(orders.HasStore ? Count(orders) : 0)} of 60000");
        AppendOrders(file, 60_001, 5_000);
        using var again = StartHost(env, TimeSpan.FromHours(1));
        again.RefreshAll();
        var src = again.AllSources().Single(s => s.Source.Name == "orders").Source;
        var records = StoreReader.Open(src.StoreRoot).ReadAll(new ScanReport()).ToList();
        Assert.Equal(65_000, records.Count);
        var ns = records.Select(r => r.TryGetAttribute("n", out var v) ? v.ToString() : "?").ToHashSet();
        Assert.Equal(65_000, ns.Count);
        again.RefreshAll(); // idle refresh: nothing new
        Assert.Equal(65_000, Count(src));
    }

    // ------------------------------------------------------------------ maintenance and status

    [Fact]
    public void Maintenance_compacts_indexes_and_applies_retention_to_owned_stores_only()
    {
        var env = new Env();
        var file = Orders(env, 10);
        using var host = StartHost(env, TimeSpan.FromHours(1));
        for (var i = 0; i < 12; i++) // each refresh that finds new lines seals a small index segment
        {
            AppendOrders(file, 11 + (ulong)i * 10, 10);
            host.RefreshAll();
        }
        var src = host.AllSources().Single(s => s.Source.Name == "orders").Source;
        var before = StoreReader.Open(src.StoreRoot).SegmentIds.Count;
        Assert.True(before > 8, $"{before} segments");
        var report = host.RunMaintenance();
        Assert.True(StoreReader.Open(src.StoreRoot).SegmentIds.Count < before, report.ToJsonString());
        Assert.Equal(130, Count(src));

        // The test records are from November 2025: a one-day retention removes all but the newest segment.
        var cfg = UserConfig.Load(env.ConfigDir);
        cfg.Retention["*"] = new RetentionSetting("1d", null);
        cfg.Save();
        AppendOrders(file, 131, 10);
        host.RefreshAll(); // reloads the changed config, indexes the new lines
        host.RunMaintenance();
        src = host.AllSources().Single(s => s.Source.Name == "orders").Source;
        var left = Count(src);
        Assert.True(left < 140 && left > 0, $"{left} records left");
        // The index keeps working after retention: new lines arrive once, no 'lost records' warning.
        AppendOrders(file, 141, 10);
        host.RefreshAll();
        Assert.Equal(left + 10, Count(src));
        Assert.Empty(host.Status()["errors"]!.AsArray());
        Assert.True(File.Exists(file)); // the original is untouched
    }

    [Fact]
    public void Status_reports_sources_last_indexed_time_disk_use_and_errors()
    {
        var env = new Env();
        Orders(env, 25);
        using var host = StartHost(env, TimeSpan.FromHours(1));
        host.RefreshAll();
        var status = ServiceClient.Request(env.ConfigDir, "status")!["status"]!;
        Assert.True((bool)status["running"]!);
        var orders = status["sources"]!.AsArray().Single(s => (string?)s!["name"] == "orders")!;
        Assert.NotNull((string?)orders["last_indexed"]);
        Assert.Equal(25, (long)orders["records"]!);
        Assert.True((long)status["disk"]!["index_bytes"]! > 0);
        Assert.Equal($"127.0.0.1:{host.Port}", (string?)status["listen"]);
    }

    // ------------------------------------------------------------------ web console on loopback

    [Fact]
    public async Task Console_and_read_api_are_served_on_loopback_with_the_token()
    {
        var env = new Env();
        Orders(env, 30);
        using var host = StartHost(env);
        host.RefreshAll();
        using var http = new HttpClient { BaseAddress = host.Endpoint.BaseUri };
        var ui = await http.GetAsync("ui");
        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
        Assert.Contains("<title>LatticeLog</title>", await ui.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.NotNull(ui.Headers.GetValues("Content-Security-Policy").SingleOrDefault());
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("v1/status")).StatusCode);

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", host.Endpoint.Token);
        var ns = JsonNode.Parse(await http.GetStringAsync("v1/namespaces"))!;
        Assert.Contains(ns["namespaces"]!.AsArray(), n => (string?)n == "orders");
        var search = JsonNode.Parse(await http.GetStringAsync("v1/search?ns=orders&where=level%20%3E%3D%20ERROR&order=desc"))!;
        Assert.Equal(3, search["events"]!.AsArray().Count);
        var stats = JsonNode.Parse(await http.GetStringAsync("v1/stats?ns=orders&by=level"))!;
        Assert.Equal(30, (long)stats["total"]!);
        // The console's histogram: bucket groups by time (keys are bucket starts) and still counts every event.
        var hist = JsonNode.Parse(await http.GetStringAsync("v1/stats?ns=orders&bucket=1d"))!;
        Assert.StartsWith("time", (string)hist["group_by"]!, StringComparison.Ordinal);
        Assert.Equal(30, (long)hist["total"]!);
        Assert.Equal(30, hist["groups"]!.AsArray().Sum(g => (long)g!["count"]!));
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("v1/stats?ns=orders&bucket=soon")).StatusCode);
        Assert.NotEmpty(JsonNode.Parse(await http.GetStringAsync("v1/catalog?ns=orders"))!["segments"]!.AsArray());

        // The source explorer: every source with where it was found, and the registered roots.
        var sources = JsonNode.Parse(await http.GetStringAsync("v1/sources"))!;
        var orders = sources["sources"]!.AsArray().Single(s => (string?)s!["name"] == "orders")!;
        Assert.Equal("python-local", (string?)orders["kind"]);
        Assert.Equal("ok", (string?)orders["status"]);
        Assert.Equal(30, (long)orders["records"]!);
        Assert.StartsWith("root:", (string)orders["origin"]!, StringComparison.Ordinal);
        Assert.Contains(sources["roots"]!.AsArray(), r => string.Equals((string?)r, PathPolicy.Normalize(env.Root), StringComparison.OrdinalIgnoreCase));
        // refresh=1 rescans the roots: a service folder created a moment ago is listed before it has any data.
        PythonSource(env.Root, "billing");
        var rescanned = JsonNode.Parse(await http.GetStringAsync("v1/sources?refresh=1"))!;
        var billing = rescanned["sources"]!.AsArray().Single(s => (string?)s!["name"] == "billing")!;
        Assert.NotEqual("ok", (string?)billing["status"]);
        Assert.Equal(0, (long)billing["records"]!);
        using (var anonymous = new HttpClient { BaseAddress = host.Endpoint.BaseUri })
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("v1/sources")).StatusCode);

        // DNS rebinding: a page on another host name reaching this port is refused.
        using var rebound = new HttpRequestMessage(HttpMethod.Get, "v1/status");
        rebound.Headers.Host = "attacker.example:" + host.Port;
        Assert.Equal((HttpStatusCode)421, (await http.SendAsync(rebound)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await http.PostAsync("v1/status", new StringContent("{}"))).StatusCode);
    }

    [Fact]
    public async Task Change_feed_pushes_a_change_when_a_source_gets_new_events()
    {
        var env = new Env();
        var file = Orders(env, 10);
        using var host = StartHost(env);
        host.RefreshAll();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var http = new HttpClient { BaseAddress = host.Endpoint.BaseUri, Timeout = Timeout.InfiniteTimeSpan };
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("v1/changes", cts.Token)).StatusCode);

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", host.Endpoint.Token);
        using var resp = await http.GetAsync("v1/changes", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(cts.Token));
        async Task<(string Event, JsonNode Data)> Next()
        {
            string? ev = null, data = null;
            while (await reader.ReadLineAsync(cts.Token) is { } line)
            {
                if (line.StartsWith("event: ", StringComparison.Ordinal)) ev = line[7..];
                else if (line.StartsWith("data: ", StringComparison.Ordinal)) data = line[6..];
                else if (line.Length == 0 && ev is not null && data is not null) return (ev, JsonNode.Parse(data)!);
            }
            throw new EndOfStreamException();
        }

        var hello = await Next();
        Assert.Equal("hello", hello.Event);
        // The first pass with a listener compares against nothing yet, so it may report every source once: wait it out.
        var version = (long)hello.Data["version"]!;
        var settle = Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
        var first = Next();
        if (await Task.WhenAny(first, settle) == first) version = (long)(await first).Data["version"]!;

        AppendOrders(file, 11, 5);
        var change = first.IsCompleted ? await Next() : await first;
        while ((long)change.Data["version"]! <= version) change = await Next();
        Assert.Equal("change", change.Event);
        Assert.Contains(change.Data["sources"]!.AsArray(), s => (string?)s == "orders");
        var sources = JsonNode.Parse(await http.GetStringAsync("v1/sources", cts.Token))!;
        Assert.Equal(15, (long)sources["sources"]!.AsArray().Single(s => (string?)s!["name"] == "orders")!["records"]!);
    }

    // ------------------------------------------------------------------ registration (plans only, nothing registered)

    private sealed class FakeRunner(Func<string, IReadOnlyList<string>, int>? exit = null) : ICommandRunner
    {
        public List<string> Ran { get; } = [];
        public (int ExitCode, string Output) Run(string file, IReadOnlyList<string> args)
        {
            Ran.Add(file + " " + string.Join(' ', args));
            return (exit?.Invoke(file, args) ?? 0, file == "id" ? "501" : "");
        }
    }

    [Fact]
    public void Windows_registration_is_a_logon_task_for_the_user_with_a_windowless_copy()
    {
        var env = new Env();
        var runner = new FakeRunner((f, a) => f == "schtasks" && a[0] == "/Query" ? 1 : f == "reg" && a[0] == "query" ? 1 : 0);
        var reg = new ServiceRegistration(runner, @"C:\Users\Ana Pérez\AppData\Local\Programs\LatticeLog\latticelogw.exe", [], null, env.DataDir, "windows", @"C:\Users\Ana Pérez", @"PC\ana");
        Assert.Null(reg.Detect());
        var plan = reg.InstallPlan();
        Assert.Equal("task", plan.Method);
        var xml = plan.WriteFiles.Single().Content;
        Assert.Contains("<LogonTrigger>", xml, StringComparison.Ordinal);
        Assert.Contains(@"<UserId>PC\ana</UserId>", xml, StringComparison.Ordinal);
        Assert.Contains("<RunLevel>LeastPrivilege</RunLevel>", xml, StringComparison.Ordinal);
        Assert.Contains(@"<Command>C:\Users\Ana Pérez\AppData\Local\Programs\LatticeLog\latticelogw.exe</Command>", xml, StringComparison.Ordinal);
        Assert.Contains("<Arguments>service run</Arguments>", xml, StringComparison.Ordinal);
        Assert.NotNull(System.Xml.Linq.XDocument.Parse(xml));
        reg.Apply(plan);
        Assert.Contains(runner.Ran, r => r.StartsWith("schtasks /Create /TN LatticeLog /XML ", StringComparison.Ordinal) && r.EndsWith(" /F", StringComparison.Ordinal));
        Assert.True(File.Exists(reg.TaskXmlPath));
        Assert.Equal(Encoding.Unicode.GetPreamble(), File.ReadAllBytes(reg.TaskXmlPath)[..2]);

        var runKey = reg.InstallPlan("run-key");
        Assert.Contains("\"C:\\Users\\Ana Pérez\\AppData\\Local\\Programs\\LatticeLog\\latticelogw.exe\" service run", string.Join(' ', runKey.Commands.Single().Args), StringComparison.Ordinal);
        var un = reg.UninstallPlan("task");
        Assert.Equal(["/Delete", "/TN", "LatticeLog", "/F"], un.Commands.Single().Args);
        Assert.Throws<ArgumentException>(() => reg.InstallPlan("systemd"));
    }

    [Fact]
    public void Linux_and_macos_registration_are_user_units_that_run_the_service()
    {
        var env = new Env();
        var home = Path.Combine(env.Dir, "home");
        var runner = new FakeRunner();
        var linux = new ServiceRegistration(runner, "/home/ana/.local/bin/latticelog", [], "/home/ana/my config", env.DataDir, "linux", home, "ana");
        var plan = linux.InstallPlan();
        var unit = plan.WriteFiles.Single();
        Assert.Equal(Path.Combine(home, ".config", "systemd", "user", "latticelog.service"), unit.Path);
        Assert.Contains("ExecStart=\"/home/ana/.local/bin/latticelog\" \"service\" \"run\" \"--config-dir\" \"/home/ana/my config\"", unit.Content, StringComparison.Ordinal);
        Assert.Contains("WantedBy=default.target", unit.Content, StringComparison.Ordinal);
        linux.Apply(plan);
        Assert.Equal("systemd", linux.Detect());
        Assert.Contains("systemctl --user enable latticelog.service", runner.Ran);
        linux.Apply(linux.UninstallPlan());
        Assert.Null(linux.Detect());
        Assert.Contains("systemctl --user disable --now latticelog.service", runner.Ran);

        var mac = new ServiceRegistration(runner, "/Users/ana/.local/bin/latticelog", [], null, env.DataDir, "macos", home, "ana");
        var mplan = mac.InstallPlan();
        Assert.Contains("<string>/Users/ana/.local/bin/latticelog</string>", mplan.WriteFiles.Single().Content, StringComparison.Ordinal);
        Assert.NotNull(System.Xml.Linq.XDocument.Parse(mplan.WriteFiles.Single().Content, System.Xml.Linq.LoadOptions.None));
        Assert.Equal(["bootstrap", "gui/501", mac.LaunchdPlistPath], mplan.Commands.Single().Args);
    }

    [Fact]
    public void Windowless_copy_flips_only_the_pe_subsystem()
    {
        var pe = new byte[512];
        pe[0] = (byte)'M'; pe[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(pe, 0x3C);
        pe[0x80] = (byte)'P'; pe[0x81] = (byte)'E';
        pe[0x80 + 24 + 68] = 3; // console
        var copy = (byte[])pe.Clone();
        Assert.True(ServiceLauncher.SetGuiSubsystem(copy));
        Assert.Equal(2, copy[0x80 + 24 + 68]);
        Assert.Equal(1, pe.Zip(copy).Count(x => x.First != x.Second));
        Assert.False(ServiceLauncher.SetGuiSubsystem(new byte[100]));
        Assert.Equal("\"a b\\\\\"", ServiceRegistration.WinQuote("a b\\"));
        Assert.Equal("plain", ServiceRegistration.WinQuote("plain"));
    }

    // ------------------------------------------------------------------ the real CLI: auto-start of a detached service

    [Fact]
    public void Cli_mcp_auto_starts_a_detached_service_that_outlives_it()
    {
        var env = new Env();
        Orders(env, 20);
        var dll = Path.Combine(AppContext.BaseDirectory, "latticelog.dll");
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } hp ? hp
            : Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root ? Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")
            : "dotnet";
        try
        {
            var psi = new ProcessStartInfo(dotnet) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = env.Dir };
            foreach (var x in new[] { dll, "mcp", "--config-dir", env.ConfigDir }) psi.ArgumentList.Add(x);
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            p.StandardInput.WriteLine("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""");
            p.StandardInput.WriteLine("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            p.StandardInput.WriteLine("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"list_sources","arguments":{}}}""");
            p.StandardInput.Flush();
            JsonNode? listed = null;
            var read = Task.Run(() =>
            {
                while (p.StandardOutput.ReadLine() is { } line)
                    if ((int?)JsonNode.Parse(line)!["id"] == 2) { listed = JsonNode.Parse(line); return; }
            });
            Assert.True(read.Wait(TimeSpan.FromSeconds(90)), "no list_sources answer");
            p.StandardInput.Close();
            Assert.True(p.WaitForExit(30_000));
            output.WriteLine(err.Result);
            Assert.Equal("service", (string?)listed!["result"]!["structuredContent"]!["served_by"]);
            // The shim exited; the service it started keeps running for the next client.
            var ep = ServiceClient.Running(env.ConfigDir);
            Assert.NotNull(ep);
            Assert.NotEqual(p.Id, ep!.Pid);
        }
        finally
        {
            ServiceClient.Stop(env.ConfigDir, TimeSpan.FromSeconds(30));
        }
        Assert.Null(ServiceClient.Running(env.ConfigDir));
    }
}

/// <summary>Fixes from a real Windows install: config edits go through the service, in-use executables, registered task,
/// and a service started from inside a packaged app (Claude desktop) that other clients could not reach.</summary>
public class ServiceInstallFixTests
{
    private static ServiceHost StartHost(Env env) =>
        ServiceHost.Start(new ServiceOptions { ConfigDir = env.ConfigDir, Port = 0, MaintenanceInterval = TimeSpan.FromHours(1) });

    [Fact]
    public void Config_edits_go_through_the_running_service_and_reach_its_catalog()
    {
        var env = new Env(allowOld: false);
        using var host = ServiceHost.Start(new ServiceOptions { ConfigDir = env.ConfigDir, Port = 0, MaintenanceInterval = TimeSpan.FromHours(1) });
        Assert.DoesNotContain(PathPolicy.Normalize(env.Old), host.CatalogFor(null).Catalog.AllowedFolders);

        var reply = ServiceClient.EditConfig(env.ConfigDir, ConfigEdit.Folder("allow", "add", env.Old));
        Assert.NotNull(reply);
        Assert.True((bool?)reply!["changed"]);
        Assert.Contains(PathPolicy.Normalize(env.Old), ((JsonArray)reply["config"]!["allow"]!).Select(x => (string?)x));
        // The service wrote its own config.json, and its catalog reopens on the change.
        Assert.Contains(PathPolicy.Normalize(env.Old), UserConfig.Load(env.ConfigDir).Allow);
        Assert.Contains(PathPolicy.Normalize(env.Old), host.CatalogFor(null).Catalog.AllowedFolders);

        // The same edit again changes nothing; a malformed one is refused, not applied.
        Assert.False((bool?)ServiceClient.EditConfig(env.ConfigDir, ConfigEdit.Folder("allow", "add", env.Old))!["changed"]);
        var refused = Assert.Throws<ServiceRefusedException>(() => ServiceClient.EditConfig(env.ConfigDir, ConfigEdit.Folder("allow", "add", "relative/path")));
        Assert.Equal("bad_request", refused.Error);

        // Retention goes the same way.
        Assert.True((bool?)ServiceClient.EditConfig(env.ConfigDir, ConfigEdit.Retention("*", "30d", null, clear: false))!["changed"]);
        Assert.Equal("30d", UserConfig.Load(env.ConfigDir).Retention["*"].MaxAge);
        Assert.True((bool?)ServiceClient.EditConfig(env.ConfigDir, ConfigEdit.Retention("*", null, null, clear: true))!["changed"]);
        Assert.Empty(UserConfig.Load(env.ConfigDir).Retention);
    }

    [Fact]
    public void Without_a_service_EditConfig_returns_null_so_the_caller_writes_the_file()
    {
        var env = new Env(allowOld: false);
        Assert.Null(ServiceClient.EditConfig(env.ConfigDir, ConfigEdit.Get()));
        var config = UserConfig.Load(env.ConfigDir);
        Assert.True(ConfigEdit.Apply(config, ConfigEdit.Folder("roots", "add", env.Outside)));
        Assert.Contains(PathPolicy.Normalize(env.Outside), UserConfig.Load(env.ConfigDir).Roots);
        Assert.Throws<ArgumentException>(() => ConfigEdit.Apply(config, ConfigEdit.Retention("*", null, null, clear: false)));
        Assert.Throws<ArgumentException>(() => ConfigEdit.Apply(config, new JsonObject { ["kind"] = "nonsense" }));
    }

    [Fact]
    public void A_running_executable_is_moved_aside_instead_of_overwritten()
    {
        var dir = TestData.TempDir();
        var target = Path.Combine(dir, OperatingSystem.IsWindows() ? "tool.exe" : "tool");
        var fresh = Path.Combine(dir, "tool.new");
        File.WriteAllText(fresh, "new version");
        if (!OperatingSystem.IsWindows())
        {
            File.WriteAllText(target, "old");
            Assert.Null(ServiceLauncher.ReplaceFile(fresh, target)); // elsewhere a running file can simply be replaced
            Assert.Equal("new version", File.ReadAllText(target));
            return;
        }
        // A real running copy of a system executable: Windows refuses to overwrite it but allows the rename.
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), target);
        using var running = Process.Start(new ProcessStartInfo(target, "-n 30 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        try
        {
            var aside = ServiceLauncher.ReplaceFile(fresh, target);
            Assert.NotNull(aside);
            Assert.Equal("new version", File.ReadAllText(target));
            Assert.True(File.Exists(aside));
            Assert.Equal(0, ServiceLauncher.DeleteOldCopies(dir)); // still running: kept
        }
        finally
        {
            running.Kill();
            running.WaitForExit();
        }
        Assert.Equal(1, ServiceLauncher.DeleteOldCopies(dir));
        Assert.Empty(Directory.EnumerateFiles(dir, "*.old-*"));
    }

    [Fact]
    public void The_registered_service_is_only_started_for_the_default_folders()
    {
        Assert.False(ServiceLauncher.RegisteredServiceMatches(Path.Combine(Path.GetTempPath(), "some-config")));
        var overridden = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LATTICELOG_CONFIG_DIR"))
                         || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LATTICELOG_DATA_DIR"));
        Assert.Equal(!overridden, ServiceLauncher.RegisteredServiceMatches(null));
    }

    [Fact]
    public void Endpoint_and_lock_leave_AppData_only_for_the_default_folder_on_Windows()
    {
        var root = TestData.TempDir();
        var data = Path.Combine(root, "Local", "LatticeLog");
        var profile = Path.Combine(root, "profile");
        Assert.Equal(Path.Combine(profile, ".latticelog", "service"), ServicePaths.RendezvousDir(data, windows: true, data, profile));
        Assert.Equal(Path.Combine(profile, ".latticelog", "service"), ServicePaths.RendezvousDir(data + Path.DirectorySeparatorChar, windows: true, data, profile));
        Assert.Equal(ServicePaths.Dir(data), ServicePaths.RendezvousDir(data, windows: false, data, profile));
        var custom = Path.Combine(root, "elsewhere");
        Assert.Equal(ServicePaths.Dir(custom), ServicePaths.RendezvousDir(custom, windows: true, data, profile));
    }

    [Fact]
    public void A_client_finds_the_endpoint_file_an_older_version_wrote()
    {
        var env = new Env();
        // Off the default folder both places are one; the fallback matters for %LOCALAPPDATA%\LatticeLog on Windows.
        Assert.Equal(ServicePaths.LegacyEndpointFile(env.DataDir), ServicePaths.EndpointFile(env.DataDir));
        using var host = StartHost(env);
        Assert.Equal(host.Endpoint.Port, ServiceClient.FindEndpoint(env.ConfigDir)!.Port);
        Assert.Equal(ServiceClient.ProbeResult.Running, ServiceClient.Probe(env.ConfigDir).Result);
    }

    [Fact]
    public void Ensure_running_gives_up_at_once_when_the_service_refuses_the_token()
    {
        var env = new Env();
        using var host = StartHost(env);
        // The endpoint file names the running service's port with another token, as a stale or private copy would.
        (host.Endpoint with { Token = new string('0', 64) }).Save(ServicePaths.EndpointFile(env.DataDir));
        Assert.Equal(ServiceClient.ProbeResult.Refused, ServiceClient.Probe(env.ConfigDir).Result);
        var log = new List<string>();
        var sw = Stopwatch.StartNew();
        Assert.Null(ServiceLauncher.EnsureRunning(env.ConfigDir, TimeSpan.FromSeconds(30), log.Add));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
        Assert.Contains(log, l => l.Contains("refuses the token", StringComparison.Ordinal));
        Assert.DoesNotContain(log, l => l.StartsWith("started", StringComparison.Ordinal));
    }

    [Fact]
    public void A_private_package_copy_of_the_config_is_merged_and_set_aside()
    {
        var root = TestData.TempDir();
        var configDir = Path.Combine(root, "Roaming", "LatticeLog");
        var dataDir = Path.Combine(root, "Local", "LatticeLog");
        var packages = Path.Combine(root, "Local", "Packages");
        var cache = Path.Combine(packages, "Claude_test", "LocalCache");
        var copyDir = Path.Combine(cache, "Roaming", "LatticeLog");
        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(copyDir);
        File.WriteAllText(Path.Combine(configDir, UserConfig.FileName), new JsonObject { ["roots"] = new JsonArray((JsonNode)@"C:\a") }.ToJsonString());
        File.WriteAllText(Path.Combine(copyDir, UserConfig.FileName), new JsonObject
        {
            ["roots"] = new JsonArray((JsonNode)@"C:\A", (JsonNode)@"D:\b"),
            ["allow"] = new JsonArray((JsonNode)@"D:\old"),
            ["retention"] = new JsonObject { ["*"] = new JsonObject { ["max_age"] = "30d" } },
        }.ToJsonString());
        var stale = Path.Combine(cache, "Local", "LatticeLog", "service", "service.json");
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
        File.WriteAllText(stale, "{}");
        Directory.CreateDirectory(Path.Combine(packages, "Other_app", "LocalCache")); // nothing of ours in it

        var now = new DateTime(2026, 11, 24, 18, 0, 0, DateTimeKind.Utc);
        var lines = PackageIsolation.AdoptPrivateCopies(configDir, dataDir, packages, configDir, dataDir, now);

        var real = UserConfig.Load(configDir);
        Assert.Equal([@"C:\a", @"D:\b"], real.Roots); // C:\A is the same folder as C:\a
        Assert.Equal([@"D:\old"], real.Allow);
        Assert.Equal("30d", real.Retention["*"].MaxAge);
        Assert.False(File.Exists(Path.Combine(copyDir, UserConfig.FileName)));
        Assert.True(File.Exists(Path.Combine(copyDir, UserConfig.FileName + ".adopted-20261124180000")));
        Assert.False(File.Exists(stale));
        Assert.Equal(2, lines.Count);

        Assert.Empty(PackageIsolation.AdoptPrivateCopies(configDir, dataDir, packages, configDir, dataDir, now)); // nothing left to adopt
        // Folders other than the per-user defaults never look at package copies.
        Assert.Empty(PackageIsolation.AdoptPrivateCopies(Path.Combine(root, "x"), Path.Combine(root, "y"), packages, configDir, dataDir, now));
    }
}

public class PackageRedirectTests
{
    [Fact]
    public void A_write_redirected_into_a_package_copy_is_found()
    {
        var dir = TestData.TempDir();
        var roaming = Path.Combine(dir, "Roaming");
        var local = Path.Combine(dir, "Local");
        var path = Path.Combine(roaming, "LatticeLog", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}");
        Directory.CreateDirectory(Path.Combine(local, "Packages", "Other_123"));
        var before = DateTime.UtcNow.AddSeconds(-2);
        Assert.Null(PackageContext.RedirectedCopy(path, before, roaming, local));

        var copy = Path.Combine(local, "Packages", "Claude_abc", "LocalCache", "Roaming", "LatticeLog", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.WriteAllText(copy, "{\"allow\":[]}");
        Assert.Equal(copy, PackageContext.RedirectedCopy(path, before, roaming, local));
        // An older copy is not this write; a path outside AppData is never looked up.
        File.SetLastWriteTimeUtc(copy, before.AddMinutes(-10));
        Assert.Null(PackageContext.RedirectedCopy(path, before, roaming, local));
        Assert.Null(PackageContext.RedirectedCopy(Path.Combine(dir, "elsewhere", "config.json"), before, roaming, local));
    }
}
