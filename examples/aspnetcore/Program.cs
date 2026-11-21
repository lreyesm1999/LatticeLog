// LatticeLog in an ASP.NET Core minimal API.
//
// Two ways to write, side by side:
//   - ILogger<T> calls (framework and app code) go through the Microsoft.Extensions.Logging bridge;
//   - hot-path events are declared once with [LatticeEvent] and generated at compile time (typed, no reflection).
// ASP.NET Core starts an Activity per request, so every record carries the request's W3C trace id.
//
// Run:          dotnet run --project examples/aspnetcore -- [store-dir]
//               then GET http://localhost:5080/orders/7 (every 5th order id fails with a stock timeout)
// Self-check:   dotnet run --project examples/aspnetcore -- [store-dir] --smoke
//               starts on a free port, sends 20 requests to itself, stops, flushes and prints a summary.
// Read back:    latticelog errors <store-dir>    ·    latticelog query <store-dir> --where "level >= WARN"
using LatticeLog;
using LatticeLog.Core;
using LatticeLog.Extensions.Logging;

var smoke = args.Contains("--smoke");
var storeDir = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "logs/orders-api";

// One LatticeLogger per process. Disposing it (at the end of Main) drains the queue and seals the segment.
using var lattice = LatticeLogger.CreateLocal(storeDir, o =>
{
    o.Durability = DurabilityMode.Balanced;
    o.Resource.Add(new LogAttribute("service.name", "orders-api"));
    o.Resource.Add(new LogAttribute("deployment.environment", smoke ? "smoke" : "dev"));
});

var builder = WebApplication.CreateBuilder(args.Where(a => a != "--smoke" && a != storeDir).ToArray());
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddLatticeLog(lattice);                          // every ILogger call is also written to LatticeLog
builder.Services.AddSingleton<ILatticeLogger>(lattice);          // for generated typed events
builder.WebHost.UseUrls(smoke ? "http://127.0.0.1:0" : "http://localhost:5080");

var app = builder.Build();

app.MapGet("/orders/{id:int}", async (int id, ILatticeLogger log, ILogger<Program> ilogger) =>
{
    OrderEvents.Received(log, id, "web");
    if (id % 5 == 0)
    {
        // Simulated incident: the inventory service does not answer.
        await Task.Delay(10);
        var ex = new TimeoutException($"Inventory did not answer for order {id}");
        OrderEvents.Failed(log, id, "STOCK_TIMEOUT", ex);
        return Results.Problem($"order {id} failed", statusCode: 503);
    }
    AppLog.OrderServed(ilogger, id, cacheHit: id % 2 == 0);    // a plain ILogger call, via [LoggerMessage]
    return Results.Ok(new { id, status = "confirmed" });
});

app.MapGet("/", () => "LatticeLog ASP.NET Core example. Try /orders/7 and /orders/10.");

if (!smoke)
{
    app.Run();
    return 0;
}

// --smoke: exercise the endpoints in-process, then report what was written.
await app.StartAsync();
var address = app.Urls.First();
using (var http = new HttpClient { BaseAddress = new Uri(address) })
{
    for (var id = 1; id <= 20; id++)
        using (await http.GetAsync($"/orders/{id}")) { }
}
await app.StopAsync();

var ack = await lattice.FlushAsync(AckLevel.FsyncConfirmed);
var stats = lattice.GetStats();
Console.WriteLine($"smoke: 20 requests to {address}; {stats.Written} records written, ack={ack.Level}, dropped={stats.DroppedSaturation + stats.DroppedWriteError}");
Console.WriteLine($"store: {Path.GetFullPath(storeDir)}");
return stats.Written > 0 && stats.DroppedSaturation + stats.DroppedWriteError == 0 ? 0 : 1;

// Existing ILogger code keeps working unchanged; [LoggerMessage] is the framework's own generator.
static partial class AppLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Order {OrderId} served, cache hit: {CacheHit}")]
    public static partial void OrderServed(ILogger logger, int orderId, bool cacheHit);
}

// LatticeLog's typed events: the attribute types are kept in storage (orderId is an Int64, not text).
static partial class OrderEvents
{
    [LatticeEvent(Id = 3001, Name = "order.received", Level = Severity.Info, Message = "Order {orderId} received via {channel}")]
    public static partial void Received(ILatticeLogger logger, long orderId, string channel);

    [LatticeEvent(Id = 3002, Name = "order.failed", Level = Severity.Error, Message = "Order {orderId} failed: {reason}")]
    public static partial void Failed(ILatticeLogger logger, long orderId, string reason, Exception? error);
}
