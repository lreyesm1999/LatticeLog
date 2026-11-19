// LatticeLog example: an "inventory/orders" workload with typed events, operations and one injected incident.
// Usage: dotnet run --project examples/dotnet-console -- <store-dir> [orders=2000] [durability=Balanced]
using System.Diagnostics;
using LatticeLog;
using LatticeLog.Core;

var dir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "latticelog-example");
var orders = args.Length > 1 ? int.Parse(args[1]) : 2000;
var mode = args.Length > 2 ? Enum.Parse<DurabilityMode>(args[2], ignoreCase: true) : DurabilityMode.Balanced;

using var logger = LatticeLogger.CreateLocal(dir, o =>
{
    o.Durability = mode;
    o.MinimumLevel = Severity.Debug;
    o.Resource.Add(new LogAttribute("service.name", "orders-api"));
    o.Resource.Add(new LogAttribute("service.version", "1.4.2"));
    o.Resource.Add(new LogAttribute("deployment.environment", "demo"));
    o.InternalDiagnostics = Console.Error.WriteLine;
});

using var source = new ActivitySource("orders");
using var listener = new ActivityListener
{
    ShouldListenTo = _ => true,
    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
};
ActivitySource.AddActivityListener(listener);

var rnd = new Random(42);
var sw = Stopwatch.StartNew();
string? incidentTrace = null;
for (var i = 0; i < orders; i++)
{
    using var act = source.StartActivity("POST /orders");
    var orderId = 10_000 + i;
    using (logger.BeginOperation("PlaceOrder", $"order-{orderId}", new LogAttribute("order.id", (long)orderId)))
    {
        var product = rnd.Next(1, 200);
        var qty = rnd.Next(1, 5);
        AppEvents.OrderReceived(logger, orderId, product, qty, "user-" + rnd.Next(1, 500));
        using (logger.BeginOperation("ReserveStock", $"reserve-{orderId}"))
        {
            var injectIncident = i == orders * 3 / 4;
            if (injectIncident || rnd.Next(400) == 0)
            {
                var elapsed = injectIncident ? 12_000 : rnd.Next(5_000, 9_000);
                AppEvents.StockQueryTimeout(logger, product, elapsed, "warehouse-eu-1");
                var ex = new TimeoutException($"Reservation for product {product} timed out", new IOException("socket closed by peer"));
                AppEvents.OrderFailed(logger, orderId, "STOCK_TIMEOUT", ex);
                if (injectIncident) incidentTrace = act?.TraceId.ToHexString();
                continue;
            }
            if (rnd.Next(50) == 0) AppEvents.StockLow(logger, product, rnd.Next(0, 3));
            AppEvents.StockReserved(logger, product, qty, Guid.NewGuid(), TimeSpan.FromMinutes(15));
        }
        AppEvents.PaymentAuthorized(logger, orderId, Math.Round((decimal)(rnd.NextDouble() * 200), 2), "EUR", "4111-xxxx", "tok_" + Guid.NewGuid().ToString("N")[..12]);
        AppEvents.OrderCompleted(logger, orderId, rnd.Next(20, 400));
    }
}
var flush = await logger.FlushAsync(AckLevel.FsyncConfirmed);
var stats = logger.GetStats();
Console.WriteLine($"wrote {stats.Written} records in {sw.Elapsed.TotalMilliseconds:0} ms ({stats.Written / sw.Elapsed.TotalSeconds:0} rec/s), ack={flush.Level}, dropped={stats.DroppedSaturation + stats.DroppedWriteError}, bytes={stats.BytesWritten}");
Console.WriteLine($"store: {dir}");
Console.WriteLine($"incident trace: {incidentTrace}");

static partial class AppEvents
{
    [LatticeEvent(Id = 1001, Name = "order.received", Level = Severity.Info, Message = "Order {orderId}: {quantity} x product {productId} for {customer}")]
    public static partial void OrderReceived(ILatticeLogger logger, long orderId, int productId, int quantity, [Sensitive(Sensitivity.Personal)] string customer);

    [LatticeEvent(Id = 1002, Name = "inventory.stock.reserved", Level = Severity.Debug, Message = "Reserved {quantity} of {productId}")]
    public static partial void StockReserved(ILatticeLogger logger, int productId, int quantity, Guid reservationId, TimeSpan holdFor);

    [LatticeEvent(Id = 1003, Name = "inventory.stock.low", Level = Severity.Warn, Message = "Stock low for {productId}: {remaining} left")]
    public static partial void StockLow(ILatticeLogger logger, int productId, int remaining);

    [LatticeEvent(Id = 2101, Name = "inventory.stock.timeout", Level = Severity.Error, Message = "Stock query for {productId} timed out after {elapsedMs} ms on {warehouse}")]
    public static partial void StockQueryTimeout(ILatticeLogger logger, int productId, int elapsedMs, string warehouse);

    [LatticeEvent(Id = 1004, Name = "payment.authorized", Level = Severity.Info, Message = "Payment of {amount} {currency} authorized")]
    public static partial void PaymentAuthorized(ILatticeLogger logger, long orderId, decimal amount, string currency, string maskedCard, [Sensitive] string paymentToken);

    [LatticeEvent(Id = 1005, Name = "order.completed", Level = Severity.Info, Message = "Order {orderId} completed in {elapsedMs} ms")]
    public static partial void OrderCompleted(ILatticeLogger logger, long orderId, int elapsedMs);

    [LatticeEvent(Id = 2102, Name = "order.failed", Level = Severity.Error, Message = "Order {orderId} failed: {reason}")]
    public static partial void OrderFailed(ILatticeLogger logger, long orderId, string reason, Exception? error);
}
