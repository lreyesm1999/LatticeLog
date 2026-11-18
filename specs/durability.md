# Durability, acknowledgement and loss contract (P00-T07, P05, spec §3.1)

| mode | writer behaviour | what `FlushAsync(level)` can confirm | allowed loss |
|---|---|---|---|
| `Fast` | batches every ≤100 ms or 1024 records; bytes handed to the OS per batch; fsync only at seal/flush | `WrittenToOs` (or `FsyncConfirmed` if explicitly requested) | queue contents on process crash; OS-buffered data on power loss |
| `Balanced` (default) | batches every ≤10 ms; periodic fsync every 1 s (optionally also every `BalancedFsyncBytes`) | `WrittenToOs`, `FsyncConfirmed` on request | ≤ ~1 s of OS-buffered data on power loss; queue on crash |
| `Durable` | group commit: each batch is fsync'ed before its records are acknowledged | `FsyncConfirmed` | only what was never acknowledged; storage hardware lying about fsync is out of scope |

Rules implemented and tested (`tests/LatticeLog.Tests/LoggerTests.cs`, `DurabilityTests.cs`, `CrashTests.cs`):
- `Write()` returning `true` means *accepted in memory* — never "persisted".
- `FlushResult.Level` is the level actually reached for all records accepted before the call; write/fsync errors in
  that window are reported in `FlushResult.Error` and counted in `DroppedWriteError`.
- Every loss is counted: `FilteredByLevel`, `SampledOut`, `DroppedSaturation`, `DroppedAfterDispose`, `DroppedWriteError`.
- Saturation policies: `Block` (bounded by `BlockTimeout`, then dropped and counted), `DropNewest`, `Throw`.
- Disk full / quota: writes fail explicitly (`StoreQuotaExceededException`); data is never deleted automatically.
- I/O error during append: the segment is cut back to its last complete block, sealed, and a new segment is opened.
- Crash: the next open recovers unsealed segments — the torn tail is copied to `recovered/` and never decoded.
- Real process crashes (`CrashTests`, since 2026-11-18): a child process (`tests/LatticeLog.CrashHarness`) writes
  records with a contiguous counter and prints every acknowledgement; the test kills it (`Process.Kill`, no shutdown
  hooks, no seal) at a random moment, 3 times per mode, with segment rotation in between. After each kill: recovery is
  clean, every acknowledged record is present, and the store holds a gap-free prefix without duplicates. This covers
  process crashes (the OS keeps its page cache), not power loss or OS crashes, which need hardware/VM fault injection.
- A torn tail cannot orphan records from their catalog: schema/context blocks are written before the records that
  reference them, so every cut position keeps each complete records block decodable (`DurabilityTests`). The residual
  catalog risk of an open segment is media corruption (bit rot) of a catalog block before the segment is sealed; once
  sealed, the footer holds a second copy (format 0.2). Writing the catalog blocks twice while the segment is open was
  not adopted: it adds bytes in proportion to the catalog share (largest in context-heavy workloads such as
  `inventory`; not measured) for a risk limited to the open segment (≤ 64 MiB / 1 h by default).
- Shutdown: `Dispose` drains, fsyncs and seals within `ShutdownTimeout`; `ProcessExit`/unhandled exceptions trigger a
  bounded flush. Records that race with shutdown are counted as `DroppedAfterDispose`.
- Multi-process: one writer per directory, enforced by an exclusive OS lock (`StoreLockedException`). Readers are
  unlimited and lock-free.

Known limitations (open issues):
- On Linux the directory entry of a newly created segment is not fsync'ed (no portable .NET API); a power loss right
  after rotation could lose the new file entry. Planned mitigation: `fsync(dirfd)` via P/Invoke on Unix.
- Background durability (default, `StoreWriterOptions.BackgroundDurability`): sealing rotated segments and the
  periodic `Balanced` fsync run on the store's durability thread, in FIFO order. `DurableSequence` only grows when an
  fsync or seal has returned, and stops growing after a failure (sticky); the next synchronous flush then throws
  instead of claiming durability. Explicit `FlushAsync(FsyncConfirmed)` and `Durable` mode still wait for the fsync
  (and for pending seals).
- Storage stalls (measured, `reports/latency-v1.md` §4): on the reference machine, fsync/seal took up to 1.8–5.3 s
  (SSD 98 % full; device-level, GC excluded), and on Windows/NTFS an append waits while the same file is being
  flushed. A stall longer than `QueueCapacity / rate` (131 ms for 65,536 slots at 500k/s) then blocks producers under
  `Block` (up to `BlockTimeout`, then counted drops) or becomes counted drops under `DropNewest`. Moving fsync/seal off
  the writer thread removed the seal stalls and most fsync stalls with one producer; it cannot remove the NTFS
  same-file wait.
- Records waiting in the in-memory queue are not redacted yet (redaction happens on the writer thread, before
  persistence); a memory dump of the process can contain them.
- The `Remote` mode (server ACK) belongs to P16 and is not implemented.
