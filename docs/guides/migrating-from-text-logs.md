# Migrating from traditional logging without losing information

There are two separate things to migrate: **the code that writes logs**, and **the log files you already have**.
You can do either one without the other, and nothing forces a big-bang switch.

## Step 1: write to LatticeLog alongside what you have

Add LatticeLog as an additional destination first. Your current output stays the source of truth until you trust the
new one.

| You use | Add | Your existing code |
|---|---|---|
| `ILogger` / MEL (with Serilog or NLog as provider, or not) | `builder.Logging.AddLatticeLog(logger)` ([guide](aspnetcore.md)) | unchanged |
| Python `logging` | `logging.getLogger().addHandler(latticelog.LatticeLogHandler(log))` | unchanged |
| Python structlog | `latticelog.structlog_processor(log)` in the processor chain | unchanged |
| PHP | `new LatticeLog\Logger($spool, [...])` (server shipping) | new calls |

The MEL bridge keeps message-template properties as typed attributes and the template itself as
`message.template`, so nothing that was structured becomes text.

## Step 2: move hot or important events to typed events

For events you query often or that sit on hot paths, declare a `[LatticeEvent]`:

```csharp
// before
logger.LogError(ex, "Order {OrderId} failed: {Reason}", orderId, reason);

// after
[LatticeEvent(Id = 2102, Name = "order.failed", Level = Severity.Error, Message = "Order {orderId} failed: {reason}")]
public static partial void OrderFailed(ILatticeLogger logger, long orderId, string reason, Exception? error);
```

What you gain: a stable event id and name to query (`event = 'order.failed'`), attribute types on disk, generator
diagnostics at compile time (LL0001–LL0007, for example a placeholder with no parameter), and no formatting or
reflection on the hot path. Sensitive parameters can be marked `[Sensitive]` so they are redacted before they are
persisted.

## Step 3: convert existing files, only when you want to

```bash
latticelog allow add D:/old-service/logs                         # folders migration may read
latticelog migrate D:/old-service/logs/app.log --preview          # what would be kept; writes nothing
latticelog migrate "D:/old-service/logs/app.log*" --name legacy-app [--follow]
```

- **The originals are never modified, moved or deleted.** The result is a new source in LatticeLog's data folder.
- **Re-running imports only new lines** (`--follow` keeps doing it from the background service).
- **The preview says what cannot be kept.** For structured JSON (structlog and similar), number formatting such as
  `1.50` vs `1.5` and key order inside nested objects are normalized; everything else maps to typed fields.
- **Lines that are not JSON are kept as text**, not dropped, and counted in the report.
- Plain text is imported **byte-exact** (`import --format text --preserve-raw`): you can query it and export the exact
  original bytes. It is not smaller than compressed text: in our measurement it was +83 % vs zstd-3
  ([report](../../reports/loghub2-v1.md)), so do it for queryability, not for disk space.

A real example from this repository's development machine: a 378 MB structlog file became a 7.9 MB source with
1,762,702 records. Your ratio depends entirely on how repetitive your logs are.

## Step 4: check, then switch

```bash
latticelog verify <store>                                   # CRC of every block
latticelog export <store> --format llog-jsonl --output check.jsonl   # lossless, human-readable
latticelog stats <store> --by level                         # compare counts with your old tooling
```

Keep both outputs for a while. When the counts and the queries you rely on match, remove the old provider. You can
always leave LatticeLog again: `export --format llog-jsonl` is lossless, and `--format otlp-json` produces OTLP/JSON
for any OpenTelemetry backend.
