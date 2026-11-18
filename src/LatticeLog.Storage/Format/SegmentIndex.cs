using System.Text;

namespace LatticeLog.Storage.Format;

/// <summary>Bloom filter with FNV-1a double hashing (specs/llog-format-v0.md §7.2). Deterministic across languages.</summary>
public sealed class BloomFilter
{
    private readonly byte[] _bits;
    public int BitCount { get; }
    public int HashCount { get; }

    public BloomFilter(int expectedItems, double falsePositiveRate = 0.01)
    {
        expectedItems = Math.Max(1, expectedItems);
        var m = (int)Math.Ceiling(-expectedItems * Math.Log(falsePositiveRate) / (Math.Log(2) * Math.Log(2)));
        m = Math.Clamp(m, 64, 64 * 1024 * 1024);
        m = (m + 7) / 8 * 8;
        BitCount = m;
        HashCount = Math.Clamp((int)Math.Round((double)m / expectedItems * Math.Log(2)), 1, 16);
        _bits = new byte[m / 8];
    }

    private BloomFilter(int bitCount, int hashCount, byte[] bits) { BitCount = bitCount; HashCount = hashCount; _bits = bits; }

    public ReadOnlySpan<byte> Bits => _bits;

    public static BloomFilter FromBits(int bitCount, int hashCount, byte[] bits)
    {
        if (bitCount <= 0 || bitCount % 8 != 0 || bits.Length != bitCount / 8 || hashCount is < 1 or > 16) throw new LlogFormatException("Invalid bloom filter parameters.");
        return new BloomFilter(bitCount, hashCount, bits);
    }

    public void Add(string key)
    {
        var h = Fnv1a.Hash(key);
        var h1 = (uint)h;
        var h2 = (uint)(h >> 32) | 1u;
        for (uint i = 0; i < HashCount; i++)
        {
            var bit = (int)((h1 + i * h2) % (uint)BitCount);
            _bits[bit >> 3] |= (byte)(1 << (bit & 7));
        }
    }

    public bool MightContain(string key)
    {
        var h = Fnv1a.Hash(key);
        var h1 = (uint)h;
        var h2 = (uint)(h >> 32) | 1u;
        for (uint i = 0; i < HashCount; i++)
        {
            var bit = (int)((h1 + i * h2) % (uint)BitCount);
            if ((_bits[bit >> 3] & (1 << (bit & 7))) == 0) return false;
        }
        return true;
    }

    public static string TraceKey(TraceId t) => "t:" + t;
    public static string OperationKey(string op) => "o:" + op;
    public static string CorrelationKey(string c) => "c:" + c;
}

public sealed record BlockIndexEntry(long Offset, long Length, BlockType Type, int RecordCount, long MinTimestamp, long MaxTimestamp, ulong FirstSequence, ulong LastSequence);

public sealed record EventCount(uint EventId, string Name, long Count);

/// <summary>Segment summary stored in the footer block of a sealed segment (P11-T05).</summary>
public sealed class SegmentIndex
{
    public long RecordCount { get; set; }
    public long MinTimestamp { get; set; } = long.MaxValue;
    public long MaxTimestamp { get; set; } = long.MinValue;
    public ulong FirstSequence { get; set; }
    public ulong LastSequence { get; set; }
    public List<EventCount> Events { get; } = [];
    public long[] SeverityCounts { get; } = new long[SeverityExtensions.MaxNumber + 1];
    public List<string> Services { get; } = [];
    public List<BlockIndexEntry> Blocks { get; } = [];
    public BloomFilter? Keys { get; set; }
    /// <summary>Format 0.2: complete schema/context catalog of the segment (see SegmentTables.EncodeCatalogCopy). Null in 0.1 files.</summary>
    public byte[]? CatalogCopy { get; set; }

    public bool HasRecords => RecordCount > 0;

    public bool HasSeverityAtLeast(Severity min)
    {
        for (var i = (int)min; i < SeverityCounts.Length; i++) if (SeverityCounts[i] > 0) return true;
        return false;
    }

    public void Encode(ByteWriter w)
    {
        w.WriteVarUInt((ulong)RecordCount);
        w.WriteVarInt(RecordCount == 0 ? 0 : MinTimestamp);
        w.WriteVarInt(RecordCount == 0 ? 0 : MaxTimestamp);
        w.WriteVarUInt(FirstSequence);
        w.WriteVarUInt(LastSequence);
        w.WriteVarUInt((ulong)Events.Count);
        foreach (var e in Events) { w.WriteVarUInt(e.EventId); w.WriteString(e.Name); w.WriteVarUInt((ulong)e.Count); }
        var nonZero = SeverityCounts.Count(c => c > 0);
        w.WriteVarUInt((ulong)nonZero);
        for (var i = 0; i < SeverityCounts.Length; i++) if (SeverityCounts[i] > 0) { w.WriteByte((byte)i); w.WriteVarUInt((ulong)SeverityCounts[i]); }
        w.WriteVarUInt((ulong)Services.Count);
        foreach (var s in Services) w.WriteString(s);
        w.WriteVarUInt((ulong)Blocks.Count);
        foreach (var b in Blocks)
        {
            w.WriteVarUInt((ulong)b.Offset);
            w.WriteVarUInt((ulong)b.Length);
            w.WriteByte((byte)b.Type);
            w.WriteVarUInt((ulong)b.RecordCount);
            w.WriteVarInt(b.MinTimestamp);
            w.WriteVarInt(b.MaxTimestamp);
            w.WriteVarUInt(b.FirstSequence);
            w.WriteVarUInt(b.LastSequence);
        }
        if (Keys is null) w.WriteVarUInt(0);
        else
        {
            w.WriteVarUInt((ulong)Keys.BitCount);
            w.WriteByte((byte)Keys.HashCount);
            w.WriteBytes(Keys.Bits);
        }
        // Format 0.2 extension (always written by 0.2 writers): catalog section, length 0 = absent.
        // section := u8 compression (0 none, 1 raw DEFLATE) + varint decoded_length + payload
        if (CatalogCopy is not { Length: > 0 } copy) { w.WriteVarUInt(0); return; }
        var deflated = CatalogCompression.Deflate(copy);
        var useDeflate = deflated.Length < copy.Length;
        var payload = useDeflate ? deflated : copy;
        var section = new ByteWriter(payload.Length + 16);
        section.WriteByte(useDeflate ? (byte)Compression.Deflate : (byte)Compression.None);
        section.WriteVarUInt((ulong)copy.Length);
        section.WriteBytes(payload);
        w.WriteLengthPrefixed(section.WrittenSpan);
        section.Release();
    }

    public static SegmentIndex Decode(ReadOnlySpan<byte> payload)
    {
        var r = new ByteReader(payload);
        var x = new SegmentIndex
        {
            RecordCount = (long)r.ReadVarUInt(),
            MinTimestamp = r.ReadVarInt(),
            MaxTimestamp = r.ReadVarInt(),
            FirstSequence = r.ReadVarUInt(),
            LastSequence = r.ReadVarUInt(),
        };
        if (x.RecordCount < 0) throw new LlogFormatException("Negative record count.");
        var ne = r.ReadCount(3);
        for (var i = 0; i < ne; i++)
        {
            var id = r.ReadVarUInt();
            if (id > uint.MaxValue) throw new LlogFormatException("Event id out of range.");
            x.Events.Add(new EventCount((uint)id, r.ReadString(), (long)r.ReadVarUInt()));
        }
        var ns = r.ReadCount(2, SeverityExtensions.MaxNumber + 1);
        for (var i = 0; i < ns; i++)
        {
            var s = r.ReadByte();
            if (s > SeverityExtensions.MaxNumber) throw new LlogFormatException("Severity out of range.");
            x.SeverityCounts[s] = (long)r.ReadVarUInt();
        }
        var nsv = r.ReadCount(1);
        for (var i = 0; i < nsv; i++) x.Services.Add(r.ReadString());
        var nb = r.ReadCount(8);
        for (var i = 0; i < nb; i++)
        {
            var off = (long)r.ReadVarUInt();
            var len = (long)r.ReadVarUInt();
            var type = (BlockType)r.ReadByte();
            var cnt = (int)Math.Min(int.MaxValue, r.ReadVarUInt());
            x.Blocks.Add(new BlockIndexEntry(off, len, type, cnt, r.ReadVarInt(), r.ReadVarInt(), r.ReadVarUInt(), r.ReadVarUInt()));
        }
        var bits = r.ReadVarUInt();
        if (bits > 0)
        {
            if (bits > 64UL * 1024 * 1024) throw new LlogFormatException("Bloom filter too large.");
            var k = r.ReadByte();
            x.Keys = BloomFilter.FromBits((int)bits, k, r.ReadBytes((int)bits / 8).ToArray());
        }
        if (!r.End)
        {
            // Format 0.2 extension: catalog copy. Absent in 0.1 footers.
            var n = r.ReadCount(1);
            if (n > 0)
            {
                var section = new ByteReader(r.ReadBytes(n));
                var comp = (Compression)section.ReadByte();
                var decoded = section.ReadVarUInt();
                if (decoded > Llog.MaxDecodedBytes) throw new LlogFormatException("Footer catalog too large.");
                var body = section.ReadBytes(section.Remaining);
                x.CatalogCopy = comp switch
                {
                    Compression.None when decoded == (ulong)body.Length => body.ToArray(),
                    Compression.Deflate when decoded <= (ulong)body.Length * Llog.MaxExpansionRatio + 64 => CatalogCompression.Inflate(body, (int)decoded),
                    _ => throw new LlogFormatException("Invalid footer catalog section."),
                };
            }
        }
        if (!r.End) throw new LlogFormatException("Trailing bytes in footer.");
        return x;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append($"records={RecordCount} seq=[{FirstSequence},{LastSequence}] blocks={Blocks.Count} events={Events.Count}");
        return sb.ToString();
    }
}

/// <summary>Raw DEFLATE helpers for the footer catalog section.</summary>
public static class CatalogCompression
{
    public static byte[] Deflate(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream();
        using (var ds = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true)) ds.Write(data);
        return ms.ToArray();
    }

    public static byte[] Inflate(ReadOnlySpan<byte> data, int decodedLength)
    {
        var output = new byte[decodedLength];
        using var ds = new System.IO.Compression.DeflateStream(new MemoryStream(data.ToArray()), System.IO.Compression.CompressionMode.Decompress);
        var total = 0;
        while (total < output.Length)
        {
            var k = ds.Read(output, total, output.Length - total);
            if (k == 0) break;
            total += k;
        }
        Span<byte> one = stackalloc byte[1];
        if (total != output.Length || ds.Read(one) != 0) throw new LlogFormatException("Footer catalog decompressed size mismatch.");
        return output;
    }
}

/// <summary>Accumulates statistics while a segment is written.</summary>
public sealed class SegmentIndexBuilder
{
    private readonly SegmentIndex _index = new();
    private readonly Dictionary<(uint, string), long> _events = [];
    private readonly HashSet<string> _services = new(StringComparer.Ordinal);
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    public SegmentIndex Current => _index;

    public void AddRecord(LogRecord r)
    {
        var x = _index;
        if (x.RecordCount == 0) x.FirstSequence = r.Sequence;
        x.LastSequence = r.Sequence;
        x.RecordCount++;
        if (r.TimestampUnixNanos < x.MinTimestamp) x.MinTimestamp = r.TimestampUnixNanos;
        if (r.TimestampUnixNanos > x.MaxTimestamp) x.MaxTimestamp = r.TimestampUnixNanos;
        var key = (r.EffectiveEventId, r.EffectiveEventName ?? "");
        _events[key] = _events.GetValueOrDefault(key) + 1;
        x.SeverityCounts[Math.Min((int)r.Severity, SeverityExtensions.MaxNumber)]++;
        if (r.ServiceName is { } svc) _services.Add(svc);
        if (!r.TraceId.IsEmpty) _keys.Add(BloomFilter.TraceKey(r.TraceId));
        if (r.OperationId is not null) _keys.Add(BloomFilter.OperationKey(r.OperationId));
        if (r.CorrelationId is not null) _keys.Add(BloomFilter.CorrelationKey(r.CorrelationId));
    }

    public void AddBlock(BlockIndexEntry e) => _index.Blocks.Add(e);

    public SegmentIndex Build()
    {
        _index.Events.Clear();
        foreach (var kv in _events.OrderBy(k => k.Key.Item1).ThenBy(k => k.Key.Item2, StringComparer.Ordinal))
            _index.Events.Add(new EventCount(kv.Key.Item1, kv.Key.Item2, kv.Value));
        _index.Services.Clear();
        _index.Services.AddRange(_services.OrderBy(s => s, StringComparer.Ordinal));
        if (_keys.Count > 0)
        {
            var bloom = new BloomFilter(_keys.Count);
            foreach (var k in _keys) bloom.Add(k);
            _index.Keys = bloom;
        }
        return _index;
    }
}
