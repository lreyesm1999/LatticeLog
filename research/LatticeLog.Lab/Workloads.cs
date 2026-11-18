namespace LatticeLog.Lab;

/// <summary>
/// Deterministic synthetic workloads (P02-T03..T05). Same (name, events, seed) => identical records on every machine.
/// Ground truth: each workload injects known incidents (ids recorded in <see cref="Dataset.GroundTruth"/>).
/// </summary>
public sealed record Dataset(string Name, int Seed, List<LogRecord> Records, List<string> GroundTruth);

public static class Workloads
{
    public static readonly string[] Names = ["http-api", "inventory", "batch", "iot", "queue", "microservices", "high-cardinality", "adversarial-random"];

    private const long T0 = 1_763_400_000_000_000_000; // 2025-11-17T17:20:00Z

    public static Dataset Generate(string name, int events, int seed = 1) => name switch
    {
        "http-api" => HttpApi(events, seed),
        "inventory" => Inventory(events, seed),
        "batch" => Batch(events, seed),
        "iot" => Iot(events, seed),
        "queue" => Queue(events, seed),
        "microservices" => Microservices(events, seed),
        "high-cardinality" => HighCardinality(events, seed),
        "adversarial-random" => Adversarial(events, seed),
        _ => throw new ArgumentException($"Unknown workload '{name}'. Known: {string.Join(", ", Names)}"),
    };

    private static ContextSnapshot Resource(string svc, string version = "1.0.0", string host = "node-1")
        => new(ContextKind.Resource, null, [new("service.name", svc), new("service.version", version), new("host.name", host), new("deployment.environment", "bench")], null);

    private static TraceId Trace(Random r) => new((ulong)r.NextInt64() | 1, (ulong)r.NextInt64());
    private static SpanId Span(Random r) => new((ulong)r.NextInt64() | 1);

    private static Dataset HttpApi(int n, int seed)
    {
        var r = new Random(seed);
        var res = Resource("web-api");
        var req = new EventDescriptor(100, "http.server.request", 1, Severity.Info, "{method} {route} -> {status} in {elapsed_ms} ms",
            [new("method", ValueKind.String), new("route", ValueKind.String), new("status", ValueKind.Int64), new("elapsed_ms", ValueKind.Double), new("client_ip", ValueKind.String, Sensitivity.Personal), new("user_agent", ValueKind.String)]);
        var err = new EventDescriptor(101, "http.server.error", 1, Severity.Error, "Unhandled error on {route}", [new("route", ValueKind.String), new("status", ValueKind.Int64)]);
        string[] routes = ["/api/items", "/api/items/{id}", "/api/orders", "/api/orders/{id}", "/api/users/{id}", "/health", "/api/search"];
        string[] methods = ["GET", "GET", "GET", "POST", "PUT", "DELETE"];
        string[] agents = ["Mozilla/5.0 (Windows NT 10.0; Win64; x64)", "okhttp/4.12.0", "curl/8.5.0", "python-requests/2.32"];
        var list = new List<LogRecord>(n);
        var gt = new List<string>();
        var t = T0;
        for (var i = 0; i < n; i++)
        {
            t += r.Next(50_000, 2_000_000);
            var route = routes[r.Next(routes.Length)];
            var status = r.Next(100) switch { < 90 => 200, < 95 => 404, < 99 => 400, _ => 500 };
            var rec = new LogRecord
            {
                TimestampUnixNanos = t, Severity = Severity.Info, Event = req, Context = res, TraceId = Trace(r), SpanId = Span(r), TraceFlags = 1,
                Attributes = [new("method", methods[r.Next(methods.Length)]), new("route", route), new("status", (long)status), new("elapsed_ms", Math.Round(r.NextDouble() * 120, 3)),
                    new("client_ip", $"10.0.{r.Next(4)}.{r.Next(1, 255)}"), new("user_agent", agents[r.Next(agents.Length)])],
            };
            list.Add(rec);
            if (status == 500)
            {
                list.Add(new LogRecord
                {
                    TimestampUnixNanos = t + 1000, Severity = Severity.Error, Event = err, Context = res, TraceId = rec.TraceId, SpanId = rec.SpanId,
                    Attributes = [new("route", route), new("status", 500L)],
                    Exception = new ExceptionInfo("System.NullReferenceException", "Object reference not set to an instance of an object.",
                        "   at Api.Controllers.ItemsController.Get(Int32 id) in /src/Api/ItemsController.cs:line 42\n   at Microsoft.AspNetCore.Mvc.Infrastructure.ActionMethodExecutor.Execute()", []),
                });
                if (gt.Count < 5) gt.Add(rec.TraceId.ToString());
            }
        }
        return new Dataset("http-api", seed, list.Take(n).ToList(), gt);
    }

    private static Dataset Inventory(int n, int seed)
    {
        var r = new Random(seed);
        var res = Resource("inventory", "2.3.1");
        var reserve = new EventDescriptor(2001, "inventory.stock.reserved", 1, Severity.Info, "Reserved {qty} of {sku}", [new("sku", ValueKind.String), new("qty", ValueKind.Int64), new("warehouse", ValueKind.String), new("reservation_id", ValueKind.Uuid)]);
        var low = new EventDescriptor(2002, "inventory.stock.low", 1, Severity.Warn, "Low stock {sku}: {remaining}", [new("sku", ValueKind.String), new("remaining", ValueKind.Int64)]);
        var timeout = new EventDescriptor(2101, "inventory.stock.timeout", 1, Severity.Error, "Stock query for {sku} timed out after {elapsed_ms} ms", [new("sku", ValueKind.String), new("elapsed_ms", ValueKind.Int64), new("warehouse", ValueKind.String)]);
        var list = new List<LogRecord>(n);
        var gt = new List<string>();
        var t = T0;
        var op = 0;
        while (list.Count < n)
        {
            var trace = Trace(r);
            var ctx = new ContextSnapshot(ContextKind.Operation, "ReserveStock", [new("operation.name", "ReserveStock"), new("order.id", (long)(500_000 + op))], res);
            var opId = "op-" + op++;
            var items = r.Next(1, 6);
            for (var k = 0; k < items && list.Count < n; k++)
            {
                t += r.Next(100_000, 3_000_000);
                var sku = "SKU-" + r.Next(1, 300).ToString("D5");
                if (r.Next(2000) == 0)
                {
                    list.Add(new LogRecord { TimestampUnixNanos = t, Severity = Severity.Error, Event = timeout, Context = ctx, TraceId = trace, OperationId = opId, Attributes = [new("sku", sku), new("elapsed_ms", (long)r.Next(5000, 15000)), new("warehouse", "wh-eu-1")] });
                    if (gt.Count < 5) gt.Add(trace.ToString());
                    continue;
                }
                list.Add(new LogRecord { TimestampUnixNanos = t, Severity = Severity.Info, Event = reserve, Context = ctx, TraceId = trace, OperationId = opId, Attributes = [new("sku", sku), new("qty", (long)r.Next(1, 10)), new("warehouse", r.Next(3) == 0 ? "wh-us-2" : "wh-eu-1"), new("reservation_id", Value.From(new Guid(BytesOf(r, 16))))] });
                if (r.Next(40) == 0 && list.Count < n) list.Add(new LogRecord { TimestampUnixNanos = t + 10, Severity = Severity.Warn, Event = low, Context = ctx, TraceId = trace, OperationId = opId, Attributes = [new("sku", sku), new("remaining", (long)r.Next(0, 5))] });
            }
        }
        return new Dataset("inventory", seed, list, gt);
    }

    private static byte[] BytesOf(Random r, int n) { var b = new byte[n]; r.NextBytes(b); return b; }

    private static Dataset Batch(int n, int seed)
    {
        var r = new Random(seed);
        var res = Resource("billing-batch");
        var list = new List<LogRecord>(n);
        var t = T0;
        var job = 0;
        while (list.Count < n)
        {
            var jobId = "job-" + job++;
            var ctx = new ContextSnapshot(ContextKind.Operation, "InvoiceRun", [new("job.id", jobId)], res);
            var rows = r.Next(50, 500);
            var start = new LogRecord { TimestampUnixNanos = t, Severity = Severity.Info, EventName = "batch.started", Context = ctx, OperationId = jobId, Attributes = [new("rows", (long)rows)] };
            start.SetBody(Value.From($"Starting invoice run {jobId} with {rows} rows"));
            list.Add(start);
            for (var i = 0; i < rows && list.Count < n; i++)
            {
                t += r.Next(10_000, 200_000);
                var rec = new LogRecord { TimestampUnixNanos = t, Severity = Severity.Debug, EventName = "batch.row.processed", Context = ctx, OperationId = jobId, Attributes = [new("row", (long)i), new("amount", Math.Round(r.NextDouble() * 1000, 2)), new("currency", "EUR")] };
                rec.SetBody(Value.From($"Processed row {i} of {rows}"));
                list.Add(rec);
            }
            if (list.Count < n)
            {
                var done = new LogRecord { TimestampUnixNanos = t + 5, Severity = Severity.Info, EventName = "batch.completed", Context = ctx, OperationId = jobId, Attributes = [new("rows", (long)rows)] };
                done.SetBody(Value.From($"Invoice run {jobId} completed"));
                list.Add(done);
            }
        }
        return new Dataset("batch", seed, list, []);
    }

    private static Dataset Iot(int n, int seed)
    {
        var r = new Random(seed);
        var res = Resource("telemetry-gateway");
        var reading = new EventDescriptor(3001, "sensor.reading", 1, Severity.Debug, null, [new("device", ValueKind.String), new("temperature_c", ValueKind.Double), new("humidity", ValueKind.Double), new("battery", ValueKind.Int64)]);
        var devices = Enumerable.Range(0, 200).Select(i => "dev-" + i.ToString("D4")).ToArray();
        var temp = devices.Select(_ => 20 + r.NextDouble() * 5).ToArray();
        var list = new List<LogRecord>(n);
        var t = T0;
        for (var i = 0; i < n; i++)
        {
            var d = i % devices.Length;
            t += 5_000_000;
            temp[d] += (r.NextDouble() - 0.5) * 0.1;
            list.Add(new LogRecord { TimestampUnixNanos = t, Severity = Severity.Debug, Event = reading, Context = res, Attributes = [new("device", devices[d]), new("temperature_c", Math.Round(temp[d], 2)), new("humidity", Math.Round(40 + r.NextDouble() * 10, 1)), new("battery", (long)(100 - i / 10000 % 100))] });
        }
        return new Dataset("iot", seed, list, []);
    }

    private static Dataset Queue(int n, int seed)
    {
        var r = new Random(seed);
        var res = Resource("order-worker");
        var recv = new EventDescriptor(4001, "queue.message.received", 1, Severity.Info, null, [new("queue", ValueKind.String), new("message_id", ValueKind.String), new("attempt", ValueKind.Int64)]);
        var ack = new EventDescriptor(4002, "queue.message.acked", 1, Severity.Info, null, [new("queue", ValueKind.String), new("message_id", ValueKind.String), new("elapsed_ms", ValueKind.Double)]);
        var retry = new EventDescriptor(4003, "queue.message.retry", 1, Severity.Warn, null, [new("queue", ValueKind.String), new("message_id", ValueKind.String), new("attempt", ValueKind.Int64), new("reason", ValueKind.String)]);
        var list = new List<LogRecord>(n);
        var t = T0;
        var m = 0;
        while (list.Count < n)
        {
            var id = "msg-" + (m++).ToString("x8");
            var trace = Trace(r);
            var attempts = r.Next(20) == 0 ? r.Next(2, 4) : 1;
            for (var a = 1; a <= attempts && list.Count < n; a++)
            {
                t += r.Next(100_000, 1_000_000);
                list.Add(new LogRecord { TimestampUnixNanos = t, Severity = Severity.Info, Event = recv, Context = res, TraceId = trace, CorrelationId = id, Attributes = [new("queue", "orders"), new("message_id", id), new("attempt", (long)a)] });
                if (a < attempts && list.Count < n) list.Add(new LogRecord { TimestampUnixNanos = t + 50_000, Severity = Severity.Warn, Event = retry, Context = res, TraceId = trace, CorrelationId = id, Attributes = [new("queue", "orders"), new("message_id", id), new("attempt", (long)a), new("reason", "downstream 503")] });
            }
            if (list.Count < n) list.Add(new LogRecord { TimestampUnixNanos = t + 80_000, Severity = Severity.Info, Event = ack, Context = res, TraceId = trace, CorrelationId = id, Attributes = [new("queue", "orders"), new("message_id", id), new("elapsed_ms", Math.Round(r.NextDouble() * 50, 2))] });
        }
        return new Dataset("queue", seed, list, []);
    }

    private static Dataset Microservices(int n, int seed)
    {
        var r = new Random(seed);
        var svcs = new[] { Resource("gateway"), Resource("orders"), Resource("payments"), Resource("inventory"), Resource("notifications") };
        var span = new EventDescriptor(5001, "rpc.call", 1, Severity.Info, "{peer}.{rpc} {status} {elapsed_ms}ms", [new("peer", ValueKind.String), new("rpc", ValueKind.String), new("status", ValueKind.String), new("elapsed_ms", ValueKind.Double)]);
        var list = new List<LogRecord>(n);
        var gt = new List<string>();
        var t = T0;
        var req = 0;
        while (list.Count < n)
        {
            var trace = Trace(r);
            var fail = r.Next(500) == 0;
            var root = "req-" + req++;
            for (var hop = 0; hop < svcs.Length && list.Count < n; hop++)
            {
                t += r.Next(200_000, 3_000_000);
                var failing = fail && hop == 2;
                list.Add(new LogRecord
                {
                    TimestampUnixNanos = t, Severity = failing ? Severity.Error : Severity.Info, Event = span, Context = svcs[hop], TraceId = trace, SpanId = Span(r),
                    OperationId = hop == 0 ? root : root + "/" + hop, ParentOperationId = hop == 0 ? null : (hop == 1 ? root : root + "/" + (hop - 1)),
                    Attributes = [new("peer", hop + 1 < svcs.Length ? svcs[hop + 1].ServiceName : "smtp"), new("rpc", "Handle"), new("status", failing ? "DEADLINE_EXCEEDED" : "OK"), new("elapsed_ms", Math.Round(r.NextDouble() * 30, 3))],
                });
                if (failing) { if (gt.Count < 5) gt.Add(trace.ToString()); break; }
            }
        }
        return new Dataset("microservices", seed, list, gt);
    }

    private static Dataset HighCardinality(int n, int seed)
    {
        var r = new Random(seed);
        var res = Resource("analytics");
        var list = new List<LogRecord>(n);
        var t = T0;
        for (var i = 0; i < n; i++)
        {
            t += r.Next(1_000, 500_000);
            var rec = new LogRecord
            {
                TimestampUnixNanos = t, Severity = Severity.Info, EventName = "event.tracked", Context = res, TraceId = Trace(r),
                Attributes = [new("user_id", Value.From(new Guid(BytesOf(r, 16)))), new("session", Convert.ToHexString(BytesOf(r, 12))), new("value", r.NextDouble()), new("hash", Convert.ToBase64String(BytesOf(r, 24)))],
            };
            list.Add(rec);
        }
        return new Dataset("high-cardinality", seed, list, []);
    }

    private static Dataset Adversarial(int n, int seed)
    {
        var r = new Random(seed);
        var list = new List<LogRecord>(n);
        for (var i = 0; i < n; i++)
        {
            var rec = new LogRecord { TimestampUnixNanos = r.NextInt64(0, long.MaxValue / 2), Severity = (Severity)r.Next(0, 25), EventName = Convert.ToHexString(BytesOf(r, 6)) };
            rec.SetBody(Value.From(BytesOf(r, r.Next(16, 200))));
            rec.Attributes = [new(Convert.ToHexString(BytesOf(r, 4)), Value.From(r.NextInt64())), new("s", Convert.ToBase64String(BytesOf(r, r.Next(8, 80))))];
            list.Add(rec);
        }
        return new Dataset("adversarial-random", seed, list, []);
    }
}
