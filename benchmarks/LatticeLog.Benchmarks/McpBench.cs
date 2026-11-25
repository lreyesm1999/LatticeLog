using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LatticeLog;
using LatticeLog.Mcp;
using LatticeLog.Storage;

/// <summary>
/// Reproducible, model-free MCP benchmark. Each scenario has known evidence so response reduction cannot be
/// mistaken for diagnostic success. The same executable is used before and after an optimization.
/// </summary>
internal static class McpBench
{
    private sealed record Scenario(string Id, string Name, Func<McpServer, string> Request, Func<JsonNode, bool> Accurate, int Parallelism = 1);
    private sealed record Measurement(string Id, string Name, int Iterations, double P50Ms, double P95Ms, double P99Ms,
        long MeanTransportBytes, long MeanStructuredBytes, long ApproxStructuredTokensUtf8Div4, long MeanAllocatedBytes,
        double MeanCpuMs, bool Accurate, int McpCalls);

    public static int Run(string[] args)
    {
        var events = IntArg(args, "--events", 100_000);
        var iterations = IntArg(args, "--iterations", 15);
        var output = StringArg(args, "--output");
        var root = Path.Combine(Path.GetTempPath(), "latticelog-mcp-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var known = BuildStore(root, events);
            var scenarios = Scenarios(known.Trace, known.ErrorRef, known.NoTraceCorrelation);
            var measurements = scenarios.Select(s => Measure(root, s, iterations)).ToList();
            var report = new
            {
                format = "latticelog.mcp-benchmark.v1",
                generated_utc = DateTimeOffset.UtcNow,
                runtime = Environment.Version.ToString(),
                events,
                iterations,
                token_estimator = "ceil(UTF-8 response bytes / 4); estimate, not a model tokenizer",
                scenarios = measurements,
            };
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            if (output is not null)
            {
                var full = Path.GetFullPath(output);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, json + Environment.NewLine, new UTF8Encoding(false));
            }
            Console.WriteLine(json);
            return measurements.All(x => x.Accurate) ? 0 : 2;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static (TraceId Trace, string ErrorRef, string NoTraceCorrelation) BuildStore(string root, int count)
    {
        var trace = new TraceId(0x4df0_0000_0000_0001, 0xabc);
        const string noTrace = "checkout-nt-42";
        var service = new ContextSnapshot(ContextKind.Resource, null, [new("service.name", "bench-api")], null);
        var inventory = new ContextSnapshot(ContextKind.Resource, null, [new("service.name", "bench-inventory")], null);
        var records = new List<LogRecord>(count + 16);
        var start = 1_800_000_000_000_000_000L;
        for (var i = 0; i < count; i++)
        {
            var severity = i % 100 == 7 ? Severity.Error : i % 31 == 0 ? Severity.Warn : Severity.Info;
            records.Add(new LogRecord
            {
                TimestampUnixNanos = start + i * 1_000_000L,
                Severity = severity,
                EventName = severity >= Severity.Error ? "db.timeout" : severity == Severity.Warn ? "cache.miss" : "request.completed",
                Context = service,
                CorrelationId = "req-" + (i % 10_000),
                Attributes = [new("tenant", "t-" + (i % 1_000)), new("route", "/items/" + (i % 500)), new("status", severity >= Severity.Error ? 500L : 200L)],
                Exception = severity >= Severity.Error ? new ExceptionInfo("TimeoutException", "database timeout after " + (i % 9 + 1) + " seconds", null, []) : null,
            });
        }

        var incidentAt = start + count * 1_000_000L + 1_000_000L;
        records.Add(new LogRecord { TimestampUnixNanos = incidentAt, Severity = Severity.Info, EventName = "order.started", Context = service, TraceId = trace, OperationId = "root" });
        records.Add(new LogRecord { TimestampUnixNanos = incidentAt + 1_000_000, Severity = Severity.Info, EventName = "inventory.started", Context = inventory, TraceId = trace, OperationId = "inventory", ParentOperationId = "root" });
        records.Add(new LogRecord { TimestampUnixNanos = incidentAt + 2_000_000, Severity = Severity.Error, EventName = "inventory.stock.timeout", Context = inventory, TraceId = trace, OperationId = "inventory", ParentOperationId = "root", Exception = new ExceptionInfo("TimeoutException", "stock lookup timed out", null, []) });
        var rootFailure = new LogRecord { TimestampUnixNanos = incidentAt + 3_000_000, Severity = Severity.Error, EventName = "order.failed", Context = service, TraceId = trace, OperationId = "root", Exception = new ExceptionInfo("TimeoutException", "inventory reservation failed", null, []) };
        records.Add(rootFailure);
        records.Add(new LogRecord { TimestampUnixNanos = incidentAt + 4_000_000, Severity = Severity.Info, EventName = "checkout.started", Context = service, CorrelationId = noTrace });
        records.Add(new LogRecord { TimestampUnixNanos = incidentAt + 5_000_000, Severity = Severity.Error, EventName = "payment.declined", Context = service, CorrelationId = noTrace, Exception = new ExceptionInfo("PaymentException", "issuer declined", null, []) });

        using (var writer = StoreWriter.Open(root, new StoreWriterOptions { MinFreeDiskBytes = 0, SegmentMaxBytes = 8L * 1024 * 1024 }))
            writer.WriteBatch(records);
        var storeId = StoreWriter.ReadStoreFile(root).StoreId;
        return (trace, $"llog://{storeId}/e/{rootFailure.Sequence}", noTrace);
    }

    private static List<Scenario> Scenarios(TraceId trace, string errorRef, string noTrace) =>
    [
        new("A", "single indexed event", _ => Call("search_events", new() { ["where"] = "event = 'order.failed'", ["limit"] = 1 }),
            n => Events(n).Any() && Text(n).Contains("order.failed", StringComparison.Ordinal)),
        new("B", "repetitive errors", _ => Call("get_context_pack", new() { ["where"] = "level >= ERROR", ["budget_bytes"] = 16_000 }),
            n => Text(n).Contains("db.timeout", StringComparison.Ordinal) && Coverage(n, "truncated")),
        new("C", "cross-service trace", _ => Call("get_trace", new() { ["trace_id"] = trace.ToString(), ["budget_bytes"] = 16_000 }),
            n => Text(n).Contains("inventory.stock.timeout", StringComparison.Ordinal) && Text(n).Contains("order.failed", StringComparison.Ordinal)),
        new("D", "incident without trace id", _ => Call("get_context_pack", new() { ["where"] = $"correlation_id = '{noTrace}'", ["budget_bytes"] = 8_000 }),
            n => Text(n).Contains("payment.declined", StringComparison.Ordinal)),
        new("E", "high-cardinality grouping", _ => Call("get_error_groups", new() { ["where"] = "level >= ERROR", ["top"] = 20 }),
            n => Text(n).Contains("fingerprint", StringComparison.Ordinal)),
        new("F", "independent investigation", _ => Call("get_event", new() { ["ref"] = errorRef }),
            n => Text(n).Contains("order.failed", StringComparison.Ordinal), Parallelism: 8),
        new("G", "bounded incomplete result", _ => Call("get_context_pack", new() { ["budget_bytes"] = 2_000 }),
            n => Coverage(n, "truncated") && Text(n).Contains("coverage", StringComparison.Ordinal)),
    ];

    private static Measurement Measure(string root, Scenario scenario, int iterations)
    {
        _ = Invoke(new McpServer(root), scenario.Request(null!));
        var elapsed = new List<double>(iterations);
        long transportBytes = 0, structuredBytes = 0, allocated = 0;
        var accurate = true;
        var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime;
        for (var i = 0; i < iterations; i++)
        {
            var beforeAlloc = GC.GetTotalAllocatedBytes(false);
            var sw = Stopwatch.StartNew();
            var responses = new JsonNode[scenario.Parallelism];
            if (scenario.Parallelism == 1)
            {
                var server = new McpServer(root);
                responses[0] = Invoke(server, scenario.Request(server));
            }
            else
            {
                Parallel.For(0, scenario.Parallelism, p =>
                {
                    var server = new McpServer(root);
                    responses[p] = Invoke(server, scenario.Request(server));
                });
            }
            sw.Stop();
            allocated += GC.GetTotalAllocatedBytes(false) - beforeAlloc;
            elapsed.Add(sw.Elapsed.TotalMilliseconds);
            foreach (var response in responses)
            {
                transportBytes += Encoding.UTF8.GetByteCount(response.ToJsonString());
                structuredBytes += Encoding.UTF8.GetByteCount(response["structuredContent"]?.ToJsonString() ?? "null");
                accurate &= scenario.Accurate(response);
            }
        }
        var cpu = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
        elapsed.Sort();
        var meanTransport = transportBytes / iterations;
        var meanStructured = structuredBytes / iterations;
        return new Measurement(scenario.Id, scenario.Name, iterations, Percentile(elapsed, .50), Percentile(elapsed, .95), Percentile(elapsed, .99),
            meanTransport, meanStructured, (meanStructured + 3) / 4, allocated / iterations, cpu / iterations, accurate, scenario.Parallelism);
    }

    private static JsonNode Invoke(McpServer server, string request)
        => JsonNode.Parse(server.Handle(request)!)?["result"] ?? throw new InvalidDataException("Missing MCP result.");

    private static string Call(string name, JsonObject arguments) => new JsonObject
    {
        ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
        ["params"] = new JsonObject { ["name"] = name, ["arguments"] = arguments },
    }.ToJsonString();

    private static IEnumerable<JsonNode?> Events(JsonNode n) => n["structuredContent"]?["events"]?.AsArray() ?? [];
    private static string Text(JsonNode n) => n.ToJsonString();
    private static bool Coverage(JsonNode n, string name) => (bool?)n["structuredContent"]?["coverage"]?[name] == true;
    private static double Percentile(List<double> sorted, double p) => sorted[Math.Clamp((int)Math.Ceiling(sorted.Count * p) - 1, 0, sorted.Count - 1)];
    private static int IntArg(string[] args, string name, int fallback) => int.TryParse(StringArg(args, name), out var n) ? n : fallback;
    private static string? StringArg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
