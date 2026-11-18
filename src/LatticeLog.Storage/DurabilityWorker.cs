using System.Diagnostics;
using LatticeLog.Storage.Format;

namespace LatticeLog.Storage;

/// <summary>
/// One background thread that runs the slow durability work of a store writer in FIFO order: sealing rotated segments
/// (footer + fsync + close) and fsyncing the active segment on request. Measured motivation (reports/latency-v1.md §3):
/// fsync and seal took 170–330 ms on the reference machine and, on the writer thread, stalled producers once the
/// queue filled. Work is FIFO, so when an item finishes, every earlier item has finished too and
/// <see cref="DurableSequence"/> can only grow. After a failure it never grows again (sticky): the next synchronous
/// flush reports the error instead of claiming durability that was not reached.
/// </summary>
internal sealed class DurabilityWorker : IDisposable
{
    private abstract class Work(ulong upToSequence)
    {
        public ulong UpToSequence = upToSequence; // mutable for fsync coalescing (guarded by _gate)
    }
    private sealed class SealWork(SegmentWriter segment, Action<SegmentWriter, SegmentIndex> onSealed, ulong upToSequence) : Work(upToSequence)
    {
        public readonly SegmentWriter Segment = segment;
        public readonly Action<SegmentWriter, SegmentIndex> OnSealed = onSealed;
    }
    private sealed class FsyncWork(SegmentWriter segment, ulong upToSequence) : Work(upToSequence)
    {
        public readonly SegmentWriter Segment = segment;
    }

    private readonly object _gate = new();
    private readonly Queue<Work> _queue = new();
    private readonly Thread _thread;
    private readonly Action<string, long> _slow;
    private bool _busy;
    private bool _stopping;
    private ulong _durable;
    private ulong _fsyncRequestedUpTo;
    private FsyncWork? _queuedFsync; // queued, not started yet: later requests for the same segment extend it
    private string? _failure;

    /// <summary>Test-only fault injection: runs on the durability thread before each work item (sleep = slow disk, throw = failing disk).</summary>
    internal Action<string>? BeforeWork { get; set; }

    public DurabilityWorker(ulong durableAtStart, Action<string, long> slowOperation)
    {
        _durable = durableAtStart;
        _fsyncRequestedUpTo = durableAtStart;
        _slow = slowOperation;
        _thread = new Thread(Loop) { IsBackground = true, Name = "LatticeLog.Durability" };
        _thread.Start();
    }

    /// <summary>Every record with a sequence &lt;= this value is on stable storage (fsync returned).</summary>
    public ulong DurableSequence { get { lock (_gate) return _durable; } }

    public int Pending { get { lock (_gate) return _queue.Count + (_busy ? 1 : 0); } }

    /// <summary>First background failure, if any. Once set, durability is no longer advanced in the background.</summary>
    public string? Failure { get { lock (_gate) return _failure; } }

    public void EnqueueSeal(SegmentWriter segment, ulong upToSequence, Action<SegmentWriter, SegmentIndex> onSealed)
    {
        lock (_gate)
        {
            _queuedFsync = null; // FIFO: a later fsync must not be merged into one queued before this seal
            _queue.Enqueue(new SealWork(segment, onSealed, upToSequence));
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// Queues an fsync of <paramref name="segment"/> covering <paramref name="upToSequence"/>. Requests coalesce: if an
    /// fsync of the same segment is queued and has not started, it is extended instead, so a slow disk never builds a
    /// backlog of periodic fsyncs. (An fsync started later covers every byte written before it started.)
    /// </summary>
    public void EnqueueFsync(SegmentWriter segment, ulong upToSequence)
    {
        lock (_gate)
        {
            if (upToSequence <= _fsyncRequestedUpTo) return;
            _fsyncRequestedUpTo = upToSequence;
            if (_queuedFsync is { } q && ReferenceEquals(q.Segment, segment))
            {
                q.UpToSequence = upToSequence;
                return;
            }
            _queuedFsync = new FsyncWork(segment, upToSequence);
            _queue.Enqueue(_queuedFsync);
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Blocks until all queued work has finished.</summary>
    public void WaitIdle()
    {
        lock (_gate)
        {
            while (_queue.Count > 0 || _busy) Monitor.Wait(_gate);
        }
    }

    private void Loop()
    {
        while (true)
        {
            Work work;
            lock (_gate)
            {
                while (_queue.Count == 0 && !_stopping) Monitor.Wait(_gate);
                if (_queue.Count == 0) return;
                work = _queue.Dequeue();
                if (ReferenceEquals(work, _queuedFsync)) _queuedFsync = null; // started: no longer extensible
                _busy = true;
            }
            string? error = null;
            try
            {
                BeforeWork?.Invoke(work is SealWork ? "seal" : "fsync");
                switch (work)
                {
                    case SealWork s:
                        {
                            var t0 = Stopwatch.GetTimestamp();
                            var index = s.Segment.Seal();
                            s.Segment.Dispose();
                            _slow($"background seal of segment {s.Segment.SegmentId} (footer + fsync + close)", t0);
                            s.OnSealed(s.Segment, index);
                            break;
                        }
                    case FsyncWork f:
                        {
                            var t0 = Stopwatch.GetTimestamp();
                            try { f.Segment.Fsync(); }
                            catch (ObjectDisposedException) when (f.Segment.IsSealed)
                            {
                                // Sealed (and therefore fsynced) by the writer meanwhile: the records are durable.
                            }
                            _slow("background fsync", t0);
                            break;
                        }
                }
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
            }
            lock (_gate)
            {
                _busy = false;
                if (error is not null) _failure ??= error;
                else if (_failure is null && work.UpToSequence > _durable) _durable = work.UpToSequence; // read under the lock: final value
                Monitor.PulseAll(_gate);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stopping = true;
            Monitor.PulseAll(_gate);
        }
        _thread.Join();
    }
}
