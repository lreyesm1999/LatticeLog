using System.Runtime.InteropServices;

namespace LatticeLog.Core;

/// <summary>A long on its own cache line: producers hammer the tail, the consumer the head.</summary>
[StructLayout(LayoutKind.Explicit, Size = 128)]
internal struct PaddedLong
{
    [FieldOffset(64)] public long Value;
}

/// <summary>
/// Bounded multi-producer / single-consumer ring (Vyukov-style sequence numbers). All slots are allocated up front, so
/// enqueue and dequeue never allocate (P14-T03). Positions are dense and monotonically increasing: the position of a
/// record doubles as its acknowledgement ticket.
/// </summary>
internal sealed class BoundedRing<T> where T : class
{
    private readonly T?[] _items;
    private readonly long[] _sequence;
    private readonly long _mask;
    private PaddedLong _tail;
    private PaddedLong _head;

    public BoundedRing(int capacity)
    {
        if (capacity < 2 || (capacity & (capacity - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(capacity), "capacity must be a power of two >= 2");
        _items = new T?[capacity];
        _sequence = new long[capacity];
        for (var i = 0; i < capacity; i++) _sequence[i] = i;
        _mask = capacity - 1;
    }

    public int Capacity => _items.Length;

    /// <summary>Positions claimed so far (= records ever accepted).</summary>
    public long Tail => Volatile.Read(ref _tail.Value);

    /// <summary>Positions consumed so far.</summary>
    public long Head => Volatile.Read(ref _head.Value);

    public long Count => Math.Max(0, Tail - Head);

    /// <summary>Thread safe. Returns false when the ring is full.</summary>
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
                    Volatile.Write(ref _sequence[idx], pos + 1); // publish
                    return true;
                }
                pos = observed;
            }
            else if (diff < 0)
            {
                return false; // the slot still holds an unconsumed item from one lap ago: full
            }
            else
            {
                pos = Volatile.Read(ref _tail.Value);
            }
        }
    }

    /// <summary>
    /// Single consumer only. Returns false when empty or when the next position was claimed but is not published yet
    /// (a producer between its claim and its write); the caller retries later.
    /// </summary>
    public bool TryDequeue(out T item)
    {
        var pos = _head.Value;
        var idx = (int)(pos & _mask);
        if (Volatile.Read(ref _sequence[idx]) != pos + 1)
        {
            item = null!;
            return false;
        }
        item = _items[idx]!;
        _items[idx] = null; // do not keep records alive after they were consumed
        Volatile.Write(ref _sequence[idx], pos + _items.Length);
        Volatile.Write(ref _head.Value, pos + 1);
        return true;
    }

    /// <summary>Single consumer only: true when the next position was claimed by a producer that has not published it yet.</summary>
    public bool NextIsClaimedButUnpublished()
    {
        var pos = _head.Value;
        return Volatile.Read(ref _sequence[(int)(pos & _mask)]) == pos && Volatile.Read(ref _tail.Value) > pos;
    }

    public static int RoundUpToPowerOfTwo(int value)
    {
        var v = 2;
        while (v < value) v <<= 1;
        return v;
    }
}
