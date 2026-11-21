# Getting started: investigate an incident in five minutes

This tutorial runs a simulated order service that has an incident, then investigates it with the CLI the way you (or a
coding agent) would on a real one. Every command and every output below was run on 2026-11-21, and the same commands
run in CI through [`scripts/check-examples.sh`](../../scripts/check-examples.sh). Ids, timestamps and timings differ
on each run.

**You need:** the [.NET 10 SDK](https://dotnet.microsoft.com/download) (exact version in
[`global.json`](../../global.json)) and Git. Nothing else: no server, no container, no account.

## 1. Build

```bash
git clone https://github.com/lreyesm1999/LatticeLog.git
cd LatticeLog
dotnet build LatticeLog.sln -c Release
```

Make the CLI available as `latticelog` for this terminal session:

```bash
alias latticelog="dotnet run -c Release --no-build --project src/LatticeLog.Cli --"
```

```powershell
function latticelog { dotnet run -c Release --no-build --project src/LatticeLog.Cli -- @args }
```

(Or install it once as a single executable: see [Install](install.md).)

## 2. Produce some logs, with an incident in them

```bash
dotnet run -c Release --no-build --project examples/dotnet-console -- demo-store 2000
```

```text
wrote 8037 records in 74 ms (108823 rec/s), ack=FsyncConfirmed, dropped=0, bytes=758247
store: demo-store
incident trace: 3c47eca248878d65cd2e531ab8585b86
```

[`examples/dotnet-console`](../../examples/dotnet-console/Program.cs) places 2,000 orders. Each order is an operation
with typed events (`order.received`, `inventory.stock.reserved`, `payment.authorized`…), and a few stock queries time
out. `ack=FsyncConfirmed` means the data reached the disk (fsync) before the program said so; `dropped=0` means no
record was lost to back-pressure. `demo-store` is a folder with one `.llog` segment (762 KB).

## 3. What failed, and how often?

```bash
latticelog errors demo-store --top 3
```

The answer (excerpt of `--format markdown`):

```text
| coverage | value |
|---|---|
| matched_events | 16 |
| returned_events | 2 |
| omitted_events | 14 |
| truncated | False |
| partial_results | False |
| sampling | none |
| exhaustive | yes |

## Error groups (deterministic fingerprints, not root causes)

- `4971c8d6ee1067be` ×8 ERROR `order.failed` exception=`System.TimeoutException`, first llog://c17423fc7296431a89fd8881fb45b0cc/e/196
- `a85a07c80de55c35` ×8 ERROR `inventory.stock.timeout`, first llog://c17423fc7296431a89fd8881fb45b0cc/e/195
```

Two things to notice:

- **Coverage.** 16 error events matched, 2 are shown as group representatives, and the other 14 are *counted*, not
  silently dropped. `exhaustive: yes` means every relevant segment was read. If a segment were damaged, the answer would
  say `partial_results: True` and why.
- **Groups are fingerprints, not diagnoses.** LatticeLog groups events by event name, exception type chain and message shape.
  It does not guess causes; that is the reader's job.

## 4. Look at one failure

```bash
latticelog event demo-store 196 --format markdown
```

Excerpt of the event in that answer (time and the inner exception left out):

```json
{
  "ref": "llog://c17423fc7296431a89fd8881fb45b0cc/e/196",
  "severity": "ERROR",
  "event": "order.failed",
  "event_id": 2102,
  "service": "orders-api",
  "message": "Order 10048 failed: STOCK_TIMEOUT",
  "attributes": { "orderId": 10048, "reason": "STOCK_TIMEOUT" },
  "trace_id": "20463e869b10c5a84c90b21e256d312b",
  "operation_id": "reserve-10048",
  "parent_operation_id": "order-10048",
  "exception": { "type": "System.TimeoutException", "message": "Reservation for product 33 timed out", "inner": [ … ] }
}
```

`orderId` is stored as a 64-bit integer, not as text, so `attr.orderId = 10048` is an exact typed comparison. The
`ref` works anywhere: in the CLI, in the MCP server, in the HTTP API.

## 5. Follow the trace

```bash
latticelog trace demo-store --trace-id 3c47eca248878d65cd2e531ab8585b86 --format markdown
```

```text
| matched_events | 3 |
| exhaustive | yes |
- **by_event**: order.received=1, inventory.stock.timeout=1, order.failed=1
- **ordering_note**: Timeline is ordered by producer wall-clock time then sequence. Order across processes is not causal.

## Timeline
- `+0ms` **INFO** `order.received` svc=`orders-api` llog://…/e/6035
- `+0ms` **ERROR** `inventory.stock.timeout` svc=`orders-api` llog://…/e/6036
- `+0ms` **ERROR** `order.failed` svc=`orders-api` llog://…/e/6037
```

The trace id came from .NET's `Activity`: LatticeLog records W3C trace context automatically. Note the
`ordering_note`: the tool states what its ordering does *not* prove.

## 6. Ask precise questions

```bash
latticelog query demo-store --where "level >= ERROR" --limit 3 --explain
```

```text
… 3 events …
-- 3 event(s); scanned 1/1 segments, 1024 records decoded in 12.8 ms; more results: --cursor eyJWIjoxLCJTIjoi…
filter: level >= 'ERROR'
pruning: severity>=ERROR
```

`--explain` shows which indexes pruned the scan. Other questions:

```bash
latticelog query demo-store --where "event = 'inventory.stock.timeout' AND attr.elapsedMs > 8000"
latticelog stats demo-store --by event          # answered from footer indexes: no block decoded
latticelog context demo-store --where "level >= WARN" --budget-bytes 16000 --format json   # a budgeted evidence pack
latticelog verify demo-store                     # CRC check of every block; exit code 3 on damage
```

The query language is in [`specs/query-language.md`](../../specs/query-language.md).

## 7. Hand it to your coding agent

Everything above is also available to Claude Code, Cursor, Claude Desktop and VS Code through the MCP server, which
finds logs **by service name** instead of by path. See [MCP setup](mcp.md) and
[Investigating an incident with an agent](investigate-with-an-agent.md).

## 8. Your own app

- **New code:** reference `LatticeLog.Core` and the generator, declare `[LatticeEvent]` methods. See the
  [console example](../../examples/dotnet-console/Program.cs).
- **Existing `ILogger` code:** add the bridge, nothing else changes. See [ASP.NET Core](aspnetcore.md).
- **Python services:** `latticelog.open("logs/orders-api", service="orders-api")`. See the
  [Python SDK](../../sdks/python).
- **Old log files:** convert them on request; the originals are never touched. See
  [Migrating from text logs](migrating-from-text-logs.md).

Something did not work? [Troubleshooting](troubleshooting.md).
