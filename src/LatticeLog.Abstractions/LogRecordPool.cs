using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LatticeLog;

/// <summary>
/// Pool of <see cref="LogRecord"/> objects with an attribute array of a fixed length (P14-T03). Used by generated typed
/// events: the record is rented by the producer and returned by the logger only after a sink that declared
/// <see cref="IRecordSink.RetainsRecords"/> = false has encoded it. Records handed to any other logger or sink are
/// simply left to the GC, so correctness never depends on a return happening.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public static class LogRecordPool
{
    /// <summary>Records with more attributes than this are not pooled.</summary>
    public const int MaxPooledAttributes = 16;

    /// <summary>Records kept per attribute count. Bounds retained memory (~1.5 MB per bucket in use for 4 attributes).</summary>
    public const int RecordsPerBucket = 4096;

    private static readonly MpmcRing<LogRecord>?[] Buckets = new MpmcRing<LogRecord>?[MaxPooledAttributes + 1];

    /// <summary>
    /// Returns a cleared record whose <see cref="LogRecord.Attributes"/> is an array of exactly
    /// <paramref name="attributeCount"/> default entries that the caller fills in.
    /// </summary>
    public static LogRecord Rent(int attributeCount)
    {
        if ((uint)attributeCount > MaxPooledAttributes)
            return new LogRecord { Attributes = new LogAttribute[attributeCount] };
        var bucket = Volatile.Read(ref Buckets[attributeCount]);
        if (bucket is not null && bucket.TryDequeue(out var r))
        {
            r.InPool = 0;
            if (r.Attributes.Length != attributeCount) r.Attributes = attributeCount == 0 ? LogRecord.NoAttributes : new LogAttribute[attributeCount];
            return r;
        }
        return new LogRecord
        {
            Attributes = attributeCount == 0 ? LogRecord.NoAttributes : new LogAttribute[attributeCount],
            PoolBucket = attributeCount,
        };
    }

    /// <summary>
    /// Gives a rented record back. Only the logger calls this, once the record is no longer referenced anywhere.
    /// Records that were not rented from the pool (or were cloned) are ignored.
    /// </summary>
    public static void Return(LogRecord record)
    {
        var b = record.PoolBucket;
        if (b < 0 || Interlocked.Exchange(ref record.InPool, 1) == 1) return;
        record.ResetForPool();
        var bucket = Volatile.Read(ref Buckets[b]) ?? CreateBucket(b);
        bucket.TryEnqueue(record); // full: drop it, the GC takes it
    }

    private static MpmcRing<LogRecord> CreateBucket(int index)
    {
        var created = new MpmcRing<LogRecord>(RecordsPerBucket);
        return Interlocked.CompareExchange(ref Buckets[index], created, null) ?? created;
    }
}

/// <summary>A long on its own cache line (explicit layout is not allowed inside generic types).</summary>
[StructLayout(LayoutKind.Explicit, Size = 128)]
internal struct Padded
{
    [FieldOffset(64)] public long Value;
}

/// <summary>Bounded lock-free multi-producer / multi-consumer queue (Vyukov). Never allocates after construction.</summary>
internal sealed class MpmcRing<T> where T : class
{
    private readonly T?[] _items;
    private readonly long[] _sequence;
    private readonly long _mask;
    private Padded _tail;
    private Padded _head;

    public MpmcRing(int capacity)
    {
        if (capacity < 2 || (capacity & (capacity - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _items = new T?[capacity];
        _sequence = new long[capacity];
        for (var i = 0; i < capacity; i++) _sequence[i] = i;
        _mask = capacity - 1;
    }

    public bool TryEnqueue(T item)
    {
        var pos = Volatile.Read(ref _tail.Value);
        while (true)
        {
            var idx = (int)(pos & _mask);
            var diff = Volatile.Read(ref _sequence[idx]) - pos;
            if (diff == 0)
            {
                var observed = Interlocked.CompareExchange(ref _tail.Value, pos + 1, pos);
                if (observed == pos)
                {
                    _items[idx] = item;
                    Volatile.Write(ref _sequence[idx], pos + 1);
                    return true;
                }
                pos = observed;
            }
            else if (diff < 0) return false;
            else pos = Volatile.Read(ref _tail.Value);
        }
    }

    public bool TryDequeue(out T item)
    {
        var pos = Volatile.Read(ref _head.Value);
        while (true)
        {
            var idx = (int)(pos & _mask);
            var diff = Volatile.Read(ref _sequence[idx]) - (pos + 1);
            if (diff == 0)
            {
                var observed = Interlocked.CompareExchange(ref _head.Value, pos + 1, pos);
                if (observed == pos)
                {
                    item = _items[idx]!;
                    _items[idx] = null;
                    Volatile.Write(ref _sequence[idx], pos + _items.Length);
                    return true;
                }
                pos = observed;
            }
            else if (diff < 0)
            {
                item = null!;
                return false;
            }
            else pos = Volatile.Read(ref _head.Value);
        }
    }
}
