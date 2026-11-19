using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using LatticeLog.Cli;
using LatticeLog.Mcp;
using LatticeLog.Sources;
using LatticeLog.Storage;
using Xunit.Abstractions;

namespace LatticeLog.Tests;

/// <summary>Developer flow (specs/local-sources.md): sources by name, Python local mode, incremental index, migration.</summary>
public class SourcesTests(ITestOutputHelper output)
{
    /// <summary>An isolated per-user config + data folder + one registered root (no environment variables: tests run in parallel).</summary>
    private sealed class Env
    {
        public string Dir { get; } = TestData.TempDir();
        public string ConfigDir => Path.Combine(Dir, "config");
        public string DataDir => Path.Combine(Dir, "data");
        public string Root => Path.Combine(Dir, "root");
        public string Old => Path.Combine(Dir, "old-logs");
        public string Outside => Path.Combine(Dir, "elsewhere");

        public Env(bool allowOld = true)
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Old);
            Directory.CreateDirectory(Outside);
            Directory.CreateDirectory(ConfigDir);
            var cfg = new JsonObject
            {
                ["roots"] = new JsonArray((JsonNode)Root),
                ["allow"] = allowOld ? new JsonArray((JsonNode)Old) : new JsonArray(),
                ["data_dir"] = DataDir,
            };
            File.WriteAllText(Path.Combine(ConfigDir, UserConfig.FileName), cfg.ToJsonString());
        }

        public SourceCatalog Catalog(string? projectDir = null) =>
            SourceCatalog.Open(new CatalogOptions { ConfigDir = ConfigDir, ProjectDir = projectDir, SearchProject = projectDir is not null });
    }

    // ------------------------------------------------------------------ helpers: Python local-mode files written from C#

    private static string PythonSource(string parent, string service, string? env = "dev")
    {
        var dir = Path.Combine(parent, service);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, SourceCatalog.ManifestFile),
            new JsonObject { ["format"] = "latticelog.source", ["version"] = 1, ["service"] = service, ["environment"] = env, ["data_format"] = "latticelog.jsonl", ["data_version"] = 1, ["producer_id"] = Guid.NewGuid().ToString("N") }.ToJsonString());
        return dir;
    }

    private static string Rec(ulong seq, string service, int sev = 9, string? ev = null, string? body = null, string? exType = null, long t = 1_763_500_000_000_000_000)
    {
        var ts = UnixTime.FormatRfc3339(t + (long)seq * 1_000_000);
        var sb = new StringBuilder($"{{\"type\":\"record\",\"seq\":\"{seq}\",\"time\":\"{ts}\",\"severity_number\":{sev}");
        if (ev is not null) sb.Append($",\"event\":\"{ev}\"");
        if (body is not null) sb.Append($",\"body\":\"{body}\"");
        sb.Append($",\"attributes\":[[\"n\",{seq}],[\"ratio\",1.0]]");
        if (exType is not null) sb.Append($",\"exception\":{{\"type\":\"{exType}\",\"message\":\"boom {seq}\"}}");
        sb.Append(",\"context\":1}");
        return sb.ToString();
    }

    private static string StartFile(string sourceDir, string proc, ulong firstSeq, string service)
    {
        var dir = Path.Combine(sourceDir, proc);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"latticelog-{firstSeq:D20}.jsonl");
        File.WriteAllText(path, "{\"type\":\"header\",\"format\":\"latticelog.jsonl\",\"version\":1,\"store_id\":\"" + Guid.NewGuid().ToString("N") + "\"}\n"
            + "{\"type\":\"context\",\"ref\":1,\"kind\":\"Resource\",\"attributes\":[[\"service.name\",\"" + service + "\"]]}\n");
        return path;
    }

    private static void Append(string path, string text) => File.AppendAllText(path, text, new UTF8Encoding(false));

    private static long Count(SourceInfo s) => StoreReader.Open(s.StoreRoot).ReadAll(new ScanReport()).LongCount();

    private static JsonNode Call(McpServer server, string tool, JsonObject args, int id = 1)
    {
        var req = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args } };
        return JsonNode.Parse(server.Handle(req.ToJsonString())!)!["result"]!;
    }

    private static JsonNode Ok(McpServer server, string tool, JsonObject args)
    {
        var r = Call(server, tool, args);
        Assert.False((bool)r["isError"]!, r["content"]?[0]?["text"]?.ToString());
        return r["structuredContent"]!;
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void WriteStructlog(string path, int from, int count, bool append = false)
    {
        var sb = new StringBuilder();
        for (var i = from; i < from + count; i++)
        {
            var ts = $"2026-06-01T08:{i / 60 % 60:D2}:{i % 60:D2}.{i % 1000:D3}Z";
            if (i % 10 == 7)
                sb.Append("{\"event\":\"charge_failed\",\"level\":\"error\",\"timestamp\":\"").Append(ts).Append("\",\"invoice\":").Append(i)
                  .Append(",\"exception\":\"Traceback (most recent call last):\\n  File \\\"/srv/billing.py\\\", line 88, in charge\\n    resp.raise_for_status()\\nhttpx.HTTPStatusError: Server error '503'\"}\n");
            else if (i % 25 == 3) sb.Append("plain banner line ").Append(i).Append('\n');
            else sb.Append("{\"event\":\"invoice_sent\",\"level\":\"info\",\"timestamp\":\"").Append(ts).Append("\",\"invoice\":").Append(i).Append(",\"amount\":12.5}\n");
        }
        if (append) Append(path, sb.ToString()); else File.WriteAllText(path, sb.ToString());
    }

    // ------------------------------------------------------------------ Part A

    [Fact]
    public void Python_local_folder_is_indexed_incrementally_without_duplicates()
    {
        var env = new Env();
        var src = PythonSource(env.Root, "orders-api");
        var f = StartFile(src, "p-1", 1, "orders-api");
        Append(f, Rec(1, "orders-api") + "\n" + Rec(2, "orders-api") + "\n" + Rec(3, "orders-api", 17, "payment_failed", exType: "app.PaymentDeclined") + "\n");
        var c = env.Catalog();
        var s = Assert.Single(c.Sources);
        Assert.Equal(("orders-api", SourceKind.Python, "dev", 1), (s.Name, s.Kind, s.Environment, s.Processes));
        Assert.Equal(3, c.IndexPython(s));
        Assert.Equal(3, Count(s));

        // A line being written (no '\n' yet) is not indexed; once complete it is, exactly once.
        Append(f, Rec(4, "orders-api") + "\n" + Rec(5, "orders-api")[..20]);
        Assert.Equal(1, c.IndexPython(s));
        Append(f, Rec(5, "orders-api")[20..] + "\n");
        Assert.Equal(1, c.IndexPython(s));
        Assert.Equal(0, c.IndexPython(s));
        Assert.Equal(5, Count(s));

        // Crash after the records reached the index but before the checkpoint was saved: nothing is indexed twice.
        var before = File.ReadAllText(s.CheckpointPath!);
        Append(f, Rec(6, "orders-api") + "\n" + Rec(7, "orders-api") + "\n");
        Assert.Equal(2, c.IndexPython(s));
        var crashed = JsonNode.Parse(before)!.AsObject();
        crashed["pending"] = (string)crashed["files"]![0]!["key"]!;
        File.WriteAllText(s.CheckpointPath!, crashed.ToJsonString());
        Assert.Equal(0, c.IndexPython(s));
        Assert.Equal(7, Count(s));
        var seqs = StoreReader.Open(s.StoreRoot).ReadAll(new ScanReport()).Select(r => r.Attributes.Single(a => a.Key == "n").Value.AsInt64()).ToList();
        Assert.Equal(Enumerable.Range(1, 7).Select(i => (long)i), seqs);
        // Types survive: 1.0 stays a float64, the exception keeps its type.
        var all = StoreReader.Open(s.StoreRoot).ReadAll(new ScanReport()).ToList();
        Assert.Equal(ValueKind.Double, all[0].Attributes.Single(a => a.Key == "ratio").Value.Kind);
        Assert.Equal("app.PaymentDeclined", all[2].Exception!.Type);
        Assert.Equal("orders-api", all[2].ServiceName);
    }

    [Fact]
    public void Process_subfolders_are_shown_as_one_source()
    {
        var env = new Env();
        var src = PythonSource(env.Root, "worker");
        var a = StartFile(src, "p-1", 1, "worker");
        var b = StartFile(src, "p-2", 1, "worker");
        Append(a, string.Concat(Enumerable.Range(1, 4).Select(i => Rec((ulong)i, "worker", t: 1_763_500_000_000_000_000) + "\n")));
        Append(b, string.Concat(Enumerable.Range(1, 3).Select(i => Rec((ulong)i, "worker", t: 1_763_500_000_000_500_000) + "\n")));
        // A process restart continues its folder with a new file.
        var a2 = StartFile(src, "p-1", 5, "worker");
        Append(a2, Rec(5, "worker") + "\n");
        var c = env.Catalog();
        var s = Assert.Single(c.Sources);
        Assert.Equal(2, s.Processes);
        var server = new McpServer(c);
        var sources = Ok(server, "list_sources", []);
        Assert.Equal(8, (long)sources["sources"]![0]!["records"]!);
        var res = Ok(server, "search_events", new JsonObject { ["source"] = "worker", ["limit"] = 100 });
        Assert.Equal(8, res["events"]!.AsArray().Count);
        Assert.All(res["events"]!.AsArray(), e => Assert.Equal("worker", (string?)e!["source"]));
    }

    [Fact]
    public void Sources_are_discovered_from_roots_and_latticelog_toml()
    {
        var env = new Env();
        PythonSource(Path.Combine(env.Root, "services"), "api");
        // A .NET store under the root, read directly.
        var store = Path.Combine(env.Root, "dotnet", "billing-store");
        using (var w = StoreWriter.Open(store))
        {
            w.WriteBatch([new LogRecord { TimestampUnixNanos = 1_763_500_000_000_000_000, Severity = Severity.Error, EventName = "invoice_failed", Context = new ContextSnapshot(ContextKind.Resource, null, [new("service.name", "billing")], null) }]);
            w.Flush(AckLevel.FsyncConfirmed);
        }
        // Noise that must not be picked up.
        Directory.CreateDirectory(Path.Combine(env.Root, "node_modules", "x"));
        PythonSource(Path.Combine(env.Root, "node_modules"), "ignored");
        // Project file: a named source outside the roots, a second root and an allowlist entry.
        var project = Path.Combine(env.Dir, "project");
        var nested = Path.Combine(project, "src", "app");
        Directory.CreateDirectory(nested);
        var extra = PythonSource(Path.Combine(env.Dir, "far"), "jobs", env: "staging");
        PythonSource(Path.Combine(project, "logs"), "api", env: "prod"); // same service name as the root one
        File.WriteAllText(Path.Combine(project, ProjectConfig.FileName), """
            # project sources
            roots = ["logs"]
            [sources]
            batch-jobs = { path = "../far/jobs", environment = "qa" }
            [migrate]
            allow = ['old']
            """);
        var c = env.Catalog(projectDir: nested); // found in a parent folder
        Assert.NotNull(c.Project);
        var names = c.Sources.Select(s => s.Name).ToList();
        Assert.Contains("api", names);
        Assert.Contains("api@prod", names); // collision: kept, disambiguated by environment
        Assert.Contains("billing-store", names);
        Assert.Contains("batch-jobs", names);
        Assert.DoesNotContain("ignored", names);
        var jobs = c.Sources.Single(s => s.Name == "batch-jobs");
        Assert.Equal((SourceKind.Python, "qa", PathPolicy.Normalize(extra)), (jobs.Kind, jobs.Environment, jobs.Path));
        Assert.Equal(SourceKind.DotNet, c.Sources.Single(s => s.Name == "billing-store").Kind);
        Assert.Contains(PathPolicy.Normalize(Path.Combine(project, "old")), c.AllowedFolders);
        // The .NET store answers through MCP by name.
        var server = new McpServer(c);
        var groups = Ok(server, "get_error_groups", new JsonObject { ["source"] = "billing-store" });
        Assert.Equal("invoice_failed", (string?)groups["groups"]![0]!["event"]);
        Assert.Equal("billing-store", (string?)groups["groups"]![0]!["source"]);
        Assert.True((bool)Call(server, "search_events", new JsonObject { ["source"] = "nope" })["isError"]!);
        // Unsupported TOML is an error with the line number, not a silent misread.
        var bad = Path.Combine(env.Dir, "bad.toml");
        File.WriteAllText(bad, "[sources]\napi = \"\"\"multi\"\"\"\n");
        var ex = Assert.Throws<InvalidDataException>(() => ProjectConfig.Load(bad));
        Assert.Contains(":2:", ex.Message);
    }

    [Fact]
    public void Search_merges_sources_by_time_and_pages_each_event_once()
    {
        var env = new Env();
        var a = StartFile(PythonSource(env.Root, "alpha"), "p-1", 1, "alpha");
        var b = StartFile(PythonSource(env.Root, "beta"), "p-1", 1, "beta");
        Append(a, string.Concat(Enumerable.Range(1, 30).Select(i => Rec((ulong)i, "alpha", t: 1_763_500_000_000_000_000) + "\n")));
        Append(b, string.Concat(Enumerable.Range(1, 20).Select(i => Rec((ulong)i, "beta", t: 1_763_500_000_000_000_000 + 500_000) + "\n")));
        var server = new McpServer(env.Catalog());
        var seen = new List<(string, long)>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var args = new JsonObject { ["source"] = "*", ["limit"] = 7 };
            if (cursor is not null) args["cursor"] = cursor;
            var res = Ok(server, "search_events", args);
            var times = res["events"]!.AsArray().Select(e => (string)e!["time"]!).ToList();
            Assert.Equal(times.Order(StringComparer.Ordinal), times);
            seen.AddRange(res["events"]!.AsArray().Select(e => ((string)e!["source"]!, (long)e["attributes"]!["n"]!)));
            cursor = (string?)res["coverage"]!["next_cursor"];
            pages++;
        } while (cursor is not null && pages < 20);
        Assert.Equal(50, seen.Count);
        Assert.Equal(50, seen.Distinct().Count());
        Assert.Equal(30, seen.Count(x => x.Item1 == "alpha"));
    }

    // ------------------------------------------------------------------ Part B

    [Fact]
    public void Mcp_previews_migrates_and_answers_error_groups_by_source()
    {
        var env = new Env();
        var log = Path.Combine(env.Old, "billing.log");
        WriteStructlog(log, 0, 200);
        var sha = Sha(log);
        var mtime = File.GetLastWriteTimeUtc(log);
        var server = new McpServer(env.Catalog());
        var tools = JsonNode.Parse(server.Handle("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!)!["result"]!["tools"]!.AsArray();
        var migrate = tools.Single(t => (string?)t!["name"] == "migrate_logs")!;
        Assert.False((bool)migrate["annotations"]!["readOnlyHint"]!);
        Assert.False((bool)migrate["annotations"]!["destructiveHint"]!);
        Assert.True((bool)tools.Single(t => (string?)t!["name"] == "preview_migration")!["annotations"]!["readOnlyHint"]!);
        Assert.All(tools.Where(t => (string?)t!["name"] != "migrate_logs"), t => Assert.True((bool)t!["annotations"]!["readOnlyHint"]!));

        var preview = Ok(server, "preview_migration", new JsonObject { ["path"] = log });
        Assert.Equal("structlog json", (string?)preview["detected_format"]);
        Assert.Equal(200, (long)preview["line_count"]!);
        Assert.Equal(5, preview["sample_records"]!.AsArray().Count);
        Assert.Equal("event name when identifier-like, else body (message)", (string?)preview["field_mapping"]!["event"]);
        Assert.Equal("attribute (typed)", (string?)preview["field_mapping"]!["invoice"]);
        Assert.True((long)preview["estimated_store_bytes"]! > 0);
        Assert.NotEmpty(preview["cannot_keep"]!.AsArray());
        Assert.False(Directory.Exists(Path.Combine(env.DataDir, "migrated")) && Directory.EnumerateFileSystemEntries(Path.Combine(env.DataDir, "migrated")).Any());

        var report = Ok(server, "migrate_logs", new JsonObject { ["path"] = log, ["source_name"] = "legacy-billing" });
        Assert.Equal(200, (long)report["records_imported"]!);
        Assert.Equal(8, (long)report["unparsed_lines_kept_as_text"]!); // i % 25 == 3, never dropped
        Assert.True((long)report["final_store_bytes"]! > 0);
        Assert.True((bool)report["created_source"]!);

        var groups = Ok(server, "get_error_groups", new JsonObject { ["source"] = "legacy-billing" });
        var g = groups["groups"]!.AsArray().Single(x => (string?)x!["event"] == "charge_failed")!;
        Assert.Equal("httpx.HTTPStatusError", (string?)g["exception_type"]); // Python traceback type, PR #15 importer fix
        Assert.Equal(20, (long)g["count"]!);
        Assert.Equal("legacy-billing", (string?)g["source"]);
        Assert.Contains(Ok(server, "list_sources", [])["sources"]!.AsArray(), s => (string?)s!["name"] == "legacy-billing" && (long)s["records"]! == 200);
        Assert.Equal(sha, Sha(log));
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(log));
    }

    [Fact]
    public void Remigration_imports_only_new_lines_and_follows_rotation_and_truncation()
    {
        var env = new Env();
        var log = Path.Combine(env.Old, "app.log");
        WriteStructlog(log, 0, 100);
        var m = new LogMigrator(env.Catalog());
        var glob = Path.Combine(env.Old, "app.log*");
        Assert.Equal(100, m.Migrate(glob, "app").Imported);
        Assert.Equal(0, new LogMigrator(env.Catalog()).Migrate(glob, "app").Imported);

        // Appended lines, the last one still being written.
        WriteStructlog(log, 100, 30, append: true);
        Append(log, "{\"event\":\"invoice_sent\",\"lev");
        var r = new LogMigrator(env.Catalog()).Migrate(glob, "app", follow: true);
        Assert.Equal(30, r.Imported);
        Append(log, "el\":\"info\"}\n");

        // Rename rotation: app.log -> app.log.1 (with the completed line), new app.log with 10 lines.
        File.Move(log, log + ".1");
        WriteStructlog(log, 1000, 10);
        r = new LogMigrator(env.Catalog()).Migrate(glob, "app");
        Assert.Equal(11, r.Imported); // the completed line of app.log.1 + the new file
        Assert.Contains(r.Json["warnings"]!.AsArray(), w => w!.ToString().Contains("renamed by rotation"));

        // Copy-truncate rotation: app.log is emptied in place and written again.
        File.WriteAllText(log, "");
        WriteStructlog(log, 2000, 5, append: true);
        r = new LogMigrator(env.Catalog()).Migrate(glob, "app");
        Assert.Equal(5, r.Imported);

        // Follow mode: a refresh (what the MCP server does before each call) imports new lines without a re-run.
        WriteStructlog(log, 2005, 4, append: true);
        var c = env.Catalog();
        Assert.Contains(c.Refresh(), o => o.Source == "app" && o.Imported == 4);
        var src = c.Sources.Single(s => s.Name == "app");
        Assert.Equal(100 + 30 + 11 + 5 + 4, Count(src));
        var invoices = StoreReader.Open(src.StoreRoot).ReadAll(new ScanReport())
            .Select(x => x.TryGetAttribute("invoice", out var v) ? v.AsInt64() : -1).Where(x => x >= 0).ToList();
        Assert.Equal(invoices.Count, invoices.Distinct().Count()); // no duplicates
    }

    [Fact]
    public void Migration_refuses_paths_outside_the_roots_and_allowlist()
    {
        var env = new Env(allowOld: false);
        var outside = Path.Combine(env.Outside, "secret.log");
        File.WriteAllText(outside, "{\"event\":\"x\",\"level\":\"info\"}\n");
        var sha = Sha(outside);
        var server = new McpServer(env.Catalog());
        foreach (var (tool, args) in new[]
                 {
                     ("preview_migration", new JsonObject { ["path"] = outside }),
                     ("migrate_logs", new JsonObject { ["path"] = outside, ["source_name"] = "secret" }),
                     ("migrate_logs", new JsonObject { ["path"] = Path.Combine(env.Root, "..", "elsewhere", "secret.log"), ["source_name"] = "secret" }),
                     ("migrate_logs", new JsonObject { ["path"] = Path.Combine(env.Outside, "*.log"), ["source_name"] = "secret" }),
                 })
        {
            var r = Call(server, tool, args);
            Assert.True((bool)r["isError"]!);
            Assert.Contains("outside the registered roots and the migration allowlist", r["content"]![0]!["text"]!.ToString());
        }
        // The data folder itself is never a migration input.
        Directory.CreateDirectory(env.DataDir);
        File.WriteAllText(Path.Combine(env.DataDir, "x.log"), "a\n");
        var cfg = UserConfig.Load(env.ConfigDir);
        cfg.Allow.Add(env.DataDir);
        cfg.Save();
        var r2 = Call(new McpServer(env.Catalog()), "preview_migration", new JsonObject { ["path"] = Path.Combine(env.DataDir, "x.log") });
        Assert.Contains("inside the LatticeLog data folder", r2["content"]![0]!["text"]!.ToString());
        Assert.False(Directory.Exists(Path.Combine(env.DataDir, "migrated", "secret")));
        Assert.Equal(sha, Sha(outside));
        // A single-store server has no migration tools at all.
        var single = new McpServer(StoreReaderTestStore());
        var tools = JsonNode.Parse(single.Handle("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!)!["result"]!["tools"]!.AsArray();
        Assert.DoesNotContain(tools, t => (string?)t!["name"] is "migrate_logs" or "preview_migration");
    }

    private static string StoreReaderTestStore()
    {
        var d = TestData.TempDir();
        using var w = StoreWriter.Open(d);
        w.WriteBatch([new LogRecord { TimestampUnixNanos = 1, Severity = Severity.Info, EventName = "x" }]);
        return d;
    }

    [Fact]
    public void Originals_stay_byte_identical_and_a_cancelled_migration_registers_nothing()
    {
        var env = new Env();
        var a = Path.Combine(env.Old, "svc.log");
        var b = Path.Combine(env.Old, "svc.log.1");
        WriteStructlog(b, 0, 3000);
        WriteStructlog(a, 3000, 3000);
        var hashes = new[] { a, b }.ToDictionary(f => f, Sha);
        var times = new[] { a, b }.ToDictionary(f => f, File.GetLastWriteTimeUtc);
        var glob = Path.Combine(env.Old, "svc.log*");

        // Cancelled after the first batch of a first migration: no source, no staging folder left behind.
        using (var cts = new CancellationTokenSource())
        {
            var m = new LogMigrator(env.Catalog()) { BatchSize = 500, ProgressInterval = TimeSpan.Zero };
            Assert.ThrowsAny<OperationCanceledException>(() => m.Migrate(glob, "svc", progress: _ => cts.Cancel(), ct: cts.Token));
        }
        var migrated = Path.Combine(env.DataDir, "migrated");
        Assert.Empty(Directory.EnumerateFileSystemEntries(migrated));
        Assert.DoesNotContain(env.Catalog().Sources, s => s.Name == "svc");

        // Complete migration, then a cancelled incremental run, then a normal one: every line exactly once.
        Assert.Equal(6000, new LogMigrator(env.Catalog()).Migrate(glob, "svc").Imported);
        WriteStructlog(a, 6000, 2000, append: true);
        using (var cts = new CancellationTokenSource())
        {
            var m = new LogMigrator(env.Catalog()) { BatchSize = 300, ProgressInterval = TimeSpan.Zero };
            Assert.ThrowsAny<OperationCanceledException>(() => m.Migrate(glob, "svc", progress: _ => cts.Cancel(), ct: cts.Token));
        }
        var rest = new LogMigrator(env.Catalog()).Migrate(glob, "svc");
        Assert.InRange(rest.Imported, 1, 1999);
        var src = env.Catalog().Sources.Single(s => s.Name == "svc");
        Assert.Equal(8000, Count(src));
        hashes[a] = Sha(a); // we appended to it ourselves; from here on only LatticeLog touches the files
        times[a] = File.GetLastWriteTimeUtc(a);
        new LogMigrator(env.Catalog()).Migrate(glob, "svc");
        new LogMigrator(env.Catalog()).Preview(a);
        foreach (var (f, h) in hashes)
        {
            Assert.Equal(h, Sha(f));
            Assert.Equal(times[f], File.GetLastWriteTimeUtc(f));
        }
        Assert.Equal(2, Directory.EnumerateFiles(env.Old).Count()); // nothing written next to the originals
    }

    [Fact]
    public void Formats_are_detected_from_the_first_lines()
    {
        FormatDetection D(string text) => FormatSniffer.Detect(Encoding.UTF8.GetBytes(text), isWholeFile: true);
        Assert.Equal(LogFormat.Structlog, D("{\"event\":\"a\",\"level\":\"info\",\"timestamp\":\"2026-01-01T00:00:00Z\"}\nTraceback (most recent call last):\n{\"event\":\"b\",\"level\":\"error\"}\n").Format);
        Assert.Equal(LogFormat.SerilogClef, D("{\"@t\":\"2026-01-01T00:00:00Z\",\"@mt\":\"Hi {Name}\",\"Name\":\"x\"}\n").Format);
        Assert.Equal(LogFormat.Pino, D("{\"level\":30,\"time\":1700000000000,\"pid\":1,\"hostname\":\"h\",\"msg\":\"hi\"}\n").Format);
        Assert.Equal(LogFormat.GenericJson, D("{\"ts\":1,\"message\":\"x\"}\n").Format);
        Assert.Equal(LogFormat.OtlpJsonLines, D("{\"resourceLogs\":[]}\n").Format);
        Assert.Equal(LogFormat.OtlpJsonDocument, D("{\n  \"resourceLogs\": [\n  ]\n}\n").Format);
        Assert.Equal(LogFormat.LatticeJsonl, D("{\"type\":\"header\",\"format\":\"latticelog.jsonl\",\"version\":1}\n").Format);
        Assert.Equal(LogFormat.Text, D("2026-01-01 10:00:00 INFO started\n2026-01-01 10:00:01 ERROR boom\n").Format);
    }

    [Fact]
    public void Mcp_install_merges_client_config_and_asks_before_overwriting()
    {
        var dir = TestData.TempDir();
        var file = Path.Combine(dir, "mcp.json");
        File.WriteAllText(file, """{"mcpServers":{"other":{"command":"x"}},"theme":"dark"}""");
        // SourceCommands directly (Program.Main would reconfigure the test host's console).
        int Install(params string[] extra) => SourceCommands.Mcp(new Args(["install", "--client", "cursor", "--config-file", file, "--command", "latticelog", .. extra]));
        Assert.Equal(0, Install());
        var cfg = JsonNode.Parse(File.ReadAllText(file))!;
        Assert.Equal("x", (string?)cfg["mcpServers"]!["other"]!["command"]);
        Assert.Equal("dark", (string?)cfg["theme"]);
        Assert.Equal("mcp", (string?)cfg["mcpServers"]!["latticelog"]!["args"]![0]);
        Assert.Equal(0, Install()); // identical: nothing to do
        // A different existing entry is not replaced without consent (stdin is not interactive here).
        cfg["mcpServers"]!["latticelog"]!["command"] = "old-latticelog";
        File.WriteAllText(file, cfg.ToJsonString());
        var before = File.ReadAllText(file);
        Assert.Equal(ExitCodes.Usage, Assert.Throws<CliException>(() => Install()).Code);
        Assert.Equal(before, File.ReadAllText(file));
        Assert.Equal(0, Install("--yes"));
        Assert.Equal("latticelog", (string?)JsonNode.Parse(File.ReadAllText(file))!["mcpServers"]!["latticelog"]!["command"]);
        Assert.Equal(2, Directory.EnumerateFiles(dir, "mcp.json.latticelog-backup-*").Count()); // one per change of an existing file
        // VS Code uses "servers" with an explicit stdio type.
        var vs = Path.Combine(dir, "vscode.json");
        Assert.Equal(0, SourceCommands.Mcp(new Args(["install", "--client", "vscode", "--config-file", vs, "--command", "latticelog"])));
        Assert.Equal("stdio", (string?)JsonNode.Parse(File.ReadAllText(vs))!["servers"]!["latticelog"]!["type"]);
    }

    // ------------------------------------------------------------------ Python SDK end to end (skipped without python/structlog)

    [Fact]
    public void Python_structlog_service_is_found_by_name_with_real_exception_types()
    {
        var python = FindExe("python") ?? FindExe("python3");
        if (python is null) { output.WriteLine("python not found: skipped"); return; }
        var env = new Env();
        var repo = RepoRoot();
        var (exit, text) = Run(python, ["-I", Path.Combine(repo, "tests", "sdk", "python_local.py"), Path.Combine(repo, "sdks", "python"), Path.Combine(env.Root, "py-svc")]);
        if (exit == 3) { output.WriteLine("structlog not installed: skipped"); return; }
        Assert.True(exit == 0, text);
        var server = new McpServer(env.Catalog());
        var sources = Ok(server, "list_sources", []);
        var s = Assert.Single(sources["sources"]!.AsArray())!;
        Assert.Equal(("py-svc", "python-local", 2), ((string?)s["name"], (string?)s["kind"], (int)s["processes"]!));
        Assert.Equal(44, (long)s["records"]!); // 33 from the first writer, 11 from the second
        var groups = Ok(server, "get_error_groups", new JsonObject { ["source"] = "py-svc" })["groups"]!.AsArray();
        Assert.Contains(groups, g => (string?)g!["exception_type"] == "json.decoder.JSONDecodeError" && (string?)g["event"] == "parse_failed");
        Assert.Equal(2, groups.Where(g => (string?)g!["exception_type"] == "__main__.QuotaExceeded").Sum(g => (long)g!["count"]!)); // one per process folder
        var ev = Ok(server, "search_events", new JsonObject { ["source"] = "py-svc", ["where"] = "event = 'order_placed'", ["limit"] = 1 })["events"]![0]!;
        Assert.Equal(7L, (long)ev["attributes"]!["items"]!);
        Assert.Equal(19.5, (double)ev["attributes"]!["total"]!);
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "LatticeLog.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static string? FindExe(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (var name in OperatingSystem.IsWindows() ? new[] { exe + ".exe" } : [exe])
            {
                var p = Path.Combine(dir, name);
                if (File.Exists(p)) return p;
            }
        return null;
    }

    private static (int Exit, string Output) Run(string exe, string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(60_000)) { p.Kill(true); return (-1, "timeout"); }
        return (p.ExitCode, stdout.Result + stderr.Result);
    }
}
