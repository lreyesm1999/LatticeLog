# ASP.NET Core example

A minimal API that logs through `ILogger` (via the LatticeLog provider) and through generated `[LatticeEvent]` methods.
Every 5th order id fails with a simulated inventory timeout.

```bash
dotnet build LatticeLog.sln -c Release
dotnet run -c Release --no-build --project examples/aspnetcore -- "$PWD/aspnet-store"          # http://localhost:5080/orders/7
dotnet run -c Release --no-build --project examples/aspnetcore -- "$PWD/aspnet-store" --smoke  # 20 requests to itself, then exits
latticelog errors aspnet-store
```

Use an absolute store path: `dotnet run` starts web projects in their project folder. Walkthrough:
[docs/guides/aspnetcore.md](../../docs/guides/aspnetcore.md).
