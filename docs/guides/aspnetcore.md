# ASP.NET Core and Microsoft.Extensions.Logging

LatticeLog plugs into `Microsoft.Extensions.Logging` (MEL) as a logging provider, so existing `ILogger<T>` code keeps
working and its output lands in a `.llog` store next to your typed events. The runnable example is
[`examples/aspnetcore`](../../examples/aspnetcore/Program.cs).

## Run the example

```bash
dotnet build LatticeLog.sln -c Release
dotnet run -c Release --no-build --project examples/aspnetcore -- "$PWD/aspnet-store"
# then open http://localhost:5080/orders/7 and http://localhost:5080/orders/10 (every 5th id fails)
```

Pass an **absolute** store path: `dotnet run` starts web projects in their own project folder, so a relative path
lands under `examples/aspnetcore/`.

Without a browser, the example can exercise itself: `--smoke` starts it on a free port, sends 20 requests, stops,
flushes and prints a summary. CI runs it this way.

```text
$ dotnet run -c Release --no-build --project examples/aspnetcore -- "$PWD/aspnet-store" --smoke
smoke: 20 requests to http://127.0.0.1:60580; 165 records written, ack=FsyncConfirmed, dropped=0
```

## The wiring

```csharp
using var lattice = LatticeLogger.CreateLocal(storeDir, o =>
{
    o.Durability = DurabilityMode.Balanced;
    o.Resource.Add(new LogAttribute("service.name", "orders-api"));
});

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddLatticeLog(lattice);                    // ILogger → LatticeLog
builder.Services.AddSingleton<ILatticeLogger>(lattice);    // for generated typed events
```

- **One `LatticeLogger` per process.** Create it before the host and dispose it after `app.Run()` returns (the
  `using var` above): disposing drains the queue and seals the segment.
- `AddLatticeLog` adds a provider; your other providers (console, OpenTelemetry…) keep working. Normal MEL filtering
  applies, for example `"Logging": { "LogLevel": { "Microsoft.AspNetCore": "Warning" } }` in `appsettings.json` to skip
  the framework's per-request Information logs.

## Two ways to write

```csharp
// 1. Plain ILogger (here through MEL's own [LoggerMessage] generator): unchanged code.
[LoggerMessage(Level = LogLevel.Information, Message = "Order {OrderId} served, cache hit: {CacheHit}")]
public static partial void OrderServed(ILogger logger, int orderId, bool cacheHit);

// 2. LatticeLog's typed events: a stable id and name, and attribute types kept on disk.
[LatticeEvent(Id = 3002, Name = "order.failed", Level = Severity.Error, Message = "Order {orderId} failed: {reason}")]
public static partial void Failed(ILatticeLogger logger, long orderId, string reason, Exception? error);
```

What the bridge stores for an `ILogger` call:

| MEL | LatticeLog record |
|---|---|
| Message template properties (`{OrderId}`) | typed attributes (`OrderId` as an integer) |
| `{OriginalFormat}` | `message.template` attribute |
| `EventId` | `event_id` / `event_name` |
| Logger category | `logger.category` attribute |
| Scopes (`BeginScope`) | `scope.*` attributes (outermost first) |
| Exception | structured exception (type, message, stack, inner exceptions) |
| `Activity.Current` | W3C `trace_id` / `span_id` |

MEL formats the message and allocates its state object on every enabled call. On hot paths, prefer `[LatticeEvent]`
methods ([benchmark](../../reports/benchmarks-hotpath.md)).

## One request, one trace

ASP.NET Core starts an `Activity` per request, so framework logs, your `ILogger` calls and your typed events share the
request's trace id. Real output from the example for a failing request:

```text
$ latticelog query aspnet-store --where "trace_id = '5b3b5464abbb22db930015375c0dde52'"
… INFO  [orders-api] Request starting HTTP/1.1 GET http://127.0.0.1:60580/orders/5 …
… INFO  [orders-api] ExecutingEndpoint: Executing endpoint 'HTTP: GET /orders/{id:int}' …
… INFO  [orders-api] order.received: Order 5 received via web orderId=5 channel=web …
… ERROR [orders-api] order.failed: Order 5 failed: STOCK_TIMEOUT orderId=5 reason=STOCK_TIMEOUT … exception=System.TimeoutException: Inventory did not answer for order 5
… INFO  [orders-api] WritingResultAsStatusCode: Setting HTTP status code 503. StatusCode=503 …
… INFO  [orders-api] Request finished HTTP/1.1 GET http://127.0.0.1:60580/orders/5 - 503 - application/problem+json 24.6809ms …
-- 8 event(s); scanned 1/1 segments, 165 records decoded in 14.4 ms
```

(Lines shortened with `…`; the full output also lists every attribute.)

## Reference from your own project

Packages are not on NuGet yet; reference the projects from a clone:

```xml
<ItemGroup>
  <ProjectReference Include="path/to/LatticeLog/src/LatticeLog.Core/LatticeLog.Core.csproj" />
  <ProjectReference Include="path/to/LatticeLog/src/LatticeLog.Extensions.Logging/LatticeLog.Extensions.Logging.csproj" />
  <ProjectReference Include="path/to/LatticeLog/src/LatticeLog.Generator/LatticeLog.Generator.csproj"
                    OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
</ItemGroup>
```

`dotnet pack LatticeLog.sln -c Release -o ./nupkg` also builds local packages you can consume from a folder feed.
