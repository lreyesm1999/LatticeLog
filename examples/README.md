# Examples

Every example runs from a clone after `dotnet build LatticeLog.sln -c Release`. The .NET ones are exercised on every CI
run by [`scripts/check-examples.sh`](../scripts/check-examples.sh).

| Example | Shows | Run |
|---|---|---|
| [`dotnet-console`](dotnet-console) | Typed events with the source generator, operations, `Activity` trace context, redaction, an injected incident to investigate | `dotnet run -c Release --project examples/dotnet-console -- demo-store 2000` |
| [`aspnetcore`](aspnetcore) | ASP.NET Core minimal API: the `ILogger` bridge and `[LatticeEvent]` side by side, one trace per request | `dotnet run -c Release --project examples/aspnetcore -- "$PWD/aspnet-store" --smoke` |
| [`developer-flow`](developer-flow) | A Python structlog service and a migrated legacy log, asked about by name over MCP | `cd examples/developer-flow && python demo_api.py && python make_legacy_log.py && python ask_mcp.py latticelog` |

Then investigate with the CLI: [Getting started](../docs/guides/getting-started.md).
