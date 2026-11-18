# Durability, acknowledgement and loss contract (P00-T07, P05, spec §3.1)

| mode | writer behaviour | what `FlushAsync(level)` can confirm | allowed loss |
|---|---|---|---|
| `Fast` | batches every ≤100 ms or 1024 records; bytes handed to the OS per batch; fsync only at seal/flush | `WrittenToOs` (or `FsyncConfirmed` if explicitly requested) | queue contents on process crash; OS-buffered data on power loss |
| `Balanced` (default) | batches every ≤10 ms; periodic fsync every 1 s | `WrittenToOs`, `FsyncConfirmed` on request | ≤ ~1 s of OS-buffered data on power loss; queue on crash |
| `Durable` | group commit: each batch is fsync'ed before its records are acknowledged | `FsyncConfirmed` | only what was never acknowledged; storage hardware lying about fsync is out of scope |

Rules implemented and tested (`tests/LatticeLog.Tests/LoggerTests.cs`, `DurabilityTests.cs`):
- `Write()` returning `true` means *accepted in memory* — never "persisted".
- `FlushResult.Level` is the level actually reached for all records accepted before the call; write/fsync errors in
  that window are reported in `FlushResult.Error` and counted in `DroppedWriteError`.
- Every loss is counted: `FilteredByLevel`, `SampledOut`, `DroppedSaturation`, `DroppedAfterDispose`, `DroppedWriteError`.
- Saturation policies: `Block` (bounded by `BlockTimeout`, then dropped and counted), `DropNewest`, `Throw`.
- Disk full / quota: writes fail explicitly (`StoreQuotaExceededException`); data is never deleted automatically.
- I/O error during append: the segment is cut back to its last complete block, sealed, and a new segment is opened.
- Crash: the next open recovers unsealed segments — the torn tail is copied to `recovered/` and never decoded.
- Shutdown: `Dispose` drains, fsyncs and seals within `ShutdownTimeout`; `ProcessExit`/unhandled exceptions trigger a
  bounded flush. Records that race with shutdown are counted as `DroppedAfterDispose`.
- Multi-process: one writer per directory, enforced by an exclusive OS lock (`StoreLockedException`). Readers are
  unlimited and lock-free.

Known limitations (open issues):
- On Linux the directory entry of a newly created segment is not fsync'ed (no portable .NET API); a power loss right
  after rotation could lose the new file entry. Planned mitigation: `fsync(dirfd)` via P/Invoke on Unix.
- Records waiting in the in-memory queue are not redacted yet (redaction happens on the writer thread, before
  persistence); a memory dump of the process can contain them.
- The `Remote` mode (server ACK) belongs to P16 and is not implemented.
