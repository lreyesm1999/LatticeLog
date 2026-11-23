using System.Diagnostics;
using LatticeLog.Server;
using LatticeLog.Storage;
using Xunit.Abstractions;

namespace LatticeLog.Tests;

/// <summary>
/// Cross-language SDKs (P21) against a real server: records written by Python and PHP processes, spooled while the
/// server is unreachable, then shipped; the server store must hold them exactly once with exact value types.
/// Skipped (with a note) when the interpreter is not installed.
/// </summary>
public class SdkTests(ITestOutputHelper output)
{
    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "LatticeLog.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static string? Find(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (var name in OperatingSystem.IsWindows() ? new[] { exe + ".exe", exe + ".bat" } : [exe])
            {
                var p = Path.Combine(dir, name);
                if (File.Exists(p)) return p;
            }
        return null;
    }

    private (int Exit, string Output) Run(string exe, string[] args, Dictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env ?? []) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(60_000), "process timed out");
        var text = stdout.Result + stderr.Result;
        lock (output) output.WriteLine($"{Path.GetFileName(exe)} {string.Join(' ', args.Skip(2).Take(1))} -> {p.ExitCode}\n{text}");
        return (p.ExitCode, text);
    }

    private static async Task<LatticeServer> Start(string data) => await LatticeServer.StartAsync(new ServerOptions
    {
        DataDirectory = data,
        Tokens = TokenStore.FromTokens(TokenStore.Create("sdk", "sdk-secret", TokenScopes.Ingest, ["acme/prod"])),
        IngestTemplate = new IngestServiceOptions { StoreDirectory = "", Store = new StoreWriterOptions { MinFreeDiskBytes = 0 } },
        Url = "http://127.0.0.1:0",
    });

    private static List<LogRecord> Stored(string data) => StoreReader.Open(Path.Combine(data, "ns", "acme~prod")).ReadAll(new ScanReport()).ToList();

    private static void AssertShipped(string data, string service, bool pythonTypes)
    {
        var records = Stored(data);
        Assert.Equal(500, records.Count);
        Assert.Equal(Enumerable.Range(0, 500).Select(i => (long)i), records.Select(r => r.Attributes.First(a => a.Key == "i").Value.AsInt64()));
        Assert.All(records, r => Assert.Equal(service, r.ServiceName));
        var special = records[300];
        Assert.Equal(Severity.Error, special.Severity);
        Assert.Equal("special {i}", special.Body.AsString());
        Assert.Equal("op-300", special.OperationId);
        Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", special.TraceId.ToString());
        Assert.NotNull(special.Exception);
        Assert.Contains("payment failed", special.Exception!.Message);
        Assert.Single(special.Exception.Inner);
        Value Attr(string k) => special.Attributes.First(a => a.Key == k).Value;
        Assert.Equal(ValueKind.Double, Attr("ratio").Kind); // 1.0 stays a float, not an integer
        Assert.Equal(1.0, Attr("ratio").AsDouble());
        Assert.True(double.IsNaN(Attr("nan").AsDouble()));
        Assert.Equal(new byte[] { 0x00, 0xFF }, Attr("raw").AsBytes()); // PHP: invalid UTF-8 travels as bytes
        Assert.Equal(ValueKind.Timestamp, Attr("when").Kind);
        Assert.Equal(1_767_323_045_123_456_000L, Attr("when").AsTimestampUnixNanos());
        Assert.Equal(ValueKind.Map, Attr("nested").Kind);
        Assert.Equal("héllo 世界 🎉", Attr("unicode").AsString());
        if (!pythonTypes) return;
        Assert.Equal(ValueKind.UInt64, Attr("big").Kind);
        Assert.Equal(ValueKind.Uuid, Attr("id").Kind);
        Assert.Equal(ValueKind.Decimal, Attr("price").Kind);
        Assert.Equal("19.990", Attr("price").AsDecimal().ToString(System.Globalization.CultureInfo.InvariantCulture)); // scale kept
        Assert.Equal("a\uD800b", Attr("odd").AsString()); // lone surrogate preserved exactly
    }

    [Fact]
    public async Task Python_sdk_spools_offline_and_ships_exactly_once_with_exact_types()
    {
        var python = Find("python") ?? Find("python3");
        if (python is null) { output.WriteLine("python not found: skipped"); return; }
        var root = RepoRoot();
        var spool = TestData.TempDir();
        var data = TestData.TempDir();
        var script = Path.Combine(root, "tests", "sdk", "python_smoke.py");
        var sdk = Path.Combine(root, "sdks", "python");
        Assert.Equal(0, Run(python, ["-I", script, sdk, "offline", spool]).Exit); // no server at all: 300 records spooled
        await using var server = await Start(data);
        var (exit, text) = Run(python, ["-I", script, sdk, "ship", spool, server.BaseAddress.ToString(), "sdk-secret"]);
        Assert.True(exit == 0, text);
        AssertShipped(data, "py-orders", pythonTypes: true);
        // The first process's file (records 1..300) is fully acknowledged and deleted; the second was still open.
        Assert.Equal(["spool-00000000000000000301.jsonl"], Directory.GetFiles(spool, "spool-*.jsonl").Select(Path.GetFileName));
    }

    [Fact]
    public async Task Rust_sdk_spools_offline_and_ships_exactly_once_with_exact_types()
    {
        var (smoke, note) = RustSdk.SmokeExe;
        if (smoke is null)
        {
            if (note.StartsWith("cargo not found", StringComparison.Ordinal)) { output.WriteLine(note); return; }
            Assert.Fail(note);
        }
        var folder = TestData.TempDir();
        var data = TestData.TempDir();
        Assert.Equal(0, Run(smoke, ["offline", folder]).Exit); // local only, no server at all: 300 records
        await using var server = await Start(data);
        var (exit, text) = Run(smoke, ["ship", folder, server.BaseAddress.ToString().TrimEnd('/'), "sdk-secret"]);
        Assert.True(exit == 0, text);
        AssertShipped(data, "rs-orders", pythonTypes: true);
        // The first process's file (records 1..300) is confirmed and closed: deleted. The newest one stays.
        Assert.Equal(["latticelog-00000000000000000301.jsonl"],
            Directory.GetFiles(Path.Combine(folder, "p-1"), "latticelog-*.jsonl").Select(Path.GetFileName));
        Assert.Equal("""{"committed_through":500}""", File.ReadAllText(Path.Combine(folder, "p-1", "cursor.json")));
    }

    [Fact]
    public async Task Php_sdk_spools_offline_and_ships_exactly_once_with_exact_types()
    {
        var php = Find("php");
        if (php is null) { output.WriteLine("php not found: skipped"); return; }
        var root = RepoRoot();
        var spool = TestData.TempDir();
        var data = TestData.TempDir();
        var script = Path.Combine(root, "tests", "sdk", "php_smoke.php");
        var sdk = Path.Combine(root, "sdks", "php", "src");
        Assert.Equal(0, Run(php, [script, sdk, "offline", spool]).Exit);
        await using var server = await Start(data);
        var (exit, text) = Run(php, [script, sdk, "ship", spool, server.BaseAddress.ToString(), "sdk-secret"]);
        Assert.True(exit == 0, text);
        AssertShipped(data, "php-shop", pythonTypes: false);
    }

    [Fact]
    public async Task Concurrent_php_processes_get_unique_dense_sequences_and_the_worker_ships_them()
    {
        var php = Find("php");
        if (php is null) { output.WriteLine("php not found: skipped"); return; }
        var root = RepoRoot();
        var spool = TestData.TempDir();
        var script = Path.Combine(root, "tests", "sdk", "php_smoke.php");
        var sdk = Path.Combine(root, "sdks", "php", "src");
        var workers = Enumerable.Range(0, 6)
            .Select(w => Task.Run(() => Run(php, [script, sdk, "burst", spool, w.ToString(System.Globalization.CultureInfo.InvariantCulture)])))
            .ToArray();
        await Task.WhenAll(workers);
        Assert.All(workers, w => Assert.Equal(0, w.Result.Exit));
        var seqs = Directory.GetFiles(spool, "spool-*.jsonl").SelectMany(File.ReadAllLines)
            .Select(l => System.Text.Json.Nodes.JsonNode.Parse(l)!["seq_n"]!.GetValue<long>()).ToList();
        Assert.Equal(seqs.Order(), seqs); // appended in sequence order under the lock
        Assert.Equal(Enumerable.Range(1, 600).Select(i => (long)i), seqs); // 600 records, each sequence exactly once

        var data = TestData.TempDir();
        await using var server = await Start(data);
        var ship = Run(php, [Path.Combine(root, "sdks", "php", "bin", "latticelog-ship.php"), spool, server.BaseAddress.ToString(), "acme/prod", "30"],
            new() { ["LATTICELOG_TOKEN"] = "sdk-secret" });
        Assert.Contains("committed through 600", ship.Output);
        var stored = Stored(data);
        Assert.Equal(600, stored.Count);
        Assert.Equal(600, stored.Select(r => (r.Attributes[0].Value.AsInt64(), r.Attributes[1].Value.AsInt64())).Distinct().Count());
    }
}
