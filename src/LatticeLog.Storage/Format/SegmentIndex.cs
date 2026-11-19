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

public sealed record BlockIndexEntry(long Offset, long Length, BlockType Type, int RecordCount, long MinTimestamp, long MaxTimestamp, ulong FirstSequence, ulong LastSequence)
{
    /// <summary>Format 0.3: what this Records block contains (spec §7.4). Null for other block types, 0.1/0.2 footers and unsealed segments.</summary>
    public BlockSummary? Summary { get; init; }
}

/// <summary>
/// Exact content summary of one Records block (format 0.3, spec §7.4): the severities present and the events and
/// services present, as indexes into the segment footer's events and services lists.
/// </summary>
public sealed class BlockSummary(uint severityMask, int[] eventIndexes, int[] serviceIndexes) : IEquatable<BlockSummary>
{
    /// <summary>Bit s set when some record of the block has severity s (0..24).</summary>
    public uint SeverityMask { get; } = severityMask;
    /// <summary>Strictly ascending indexes into <see cref="SegmentIndex.Events"/>.</summary>
    public int[] EventIndexes { get; } = eventIndexes;
    /// <summary>Strictly ascending indexes into <see cref="SegmentIndex.Services"/>.</summary>
    public int[] ServiceIndexes { get; } = serviceIndexes;

    public bool HasSeverityAtLeast(Severity min) => (SeverityMask >> (int)min) != 0;

    public bool Equals(BlockSummary? other) => other is not null && SeverityMask == other.SeverityMask
        && EventIndexes.AsSpan().SequenceEqual(other.EventIndexes) && ServiceIndexes.AsSpan().SequenceEqual(other.ServiceIndexes);

    public override bool Equals(object? obj) => Equals(obj as BlockSummary);

    public override int GetHashCode() => HashCode.Combine(SeverityMask, EventIndexes.Length, ServiceIndexes.Length);
}

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
        if (CatalogCopy is not { Length: > 0 } copy) w.WriteVarUInt(0);
        else
        {
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
        // Format 0.3 extension area (§7.4): (varint tag; varint length; bytes)* up to the end of the footer,
        // tags strictly ascending; readers skip tags they do not know.
        EncodeBlockSummaries(w);
    }

    /// <summary>Footer extension tag 1 (format 0.3): per Records block summaries.</summary>
    public const ulong ExtensionBlockSummaries = 1;

    private void EncodeBlockSummaries(ByteWriter w)
    {
        var records = Blocks.Where(b => b.Type == BlockType.Records).ToList();
        // All or nothing: a directory with a Records block lacking a summary gets no section (pruning by time only).
        if (records.Count == 0 || records.Any(b => b.Summary is null)) return;
        var section = new ByteWriter(records.Count * 8 + 16);
        section.WriteVarUInt((ulong)records.Count);
        foreach (var b in records)
        {
            var s = b.Summary!;
            section.WriteVarUInt(s.SeverityMask);
            WriteIndexSet(section, s.EventIndexes, Events.Count);
            WriteIndexSet(section, s.ServiceIndexes, Services.Count);
        }
        w.WriteVarUInt(ExtensionBlockSummaries);
        w.WriteLengthPrefixed(section.WrittenSpan);
        section.Release();
    }

    /// <summary>
    /// set := varint h; h even: h/2 ascending indexes, the first absolute and each next as (gap - 1);
    /// h == 1: bitmap of ceil(universe/8) bytes, bit i = byte[i >> 3] &amp; (1 &lt;&lt; (i &amp; 7)), padding bits 0.
    /// The writer picks the shorter form (the list on a tie).
    /// </summary>
    private static void WriteIndexSet(ByteWriter w, int[] sorted, int universe)
    {
        var listBytes = VarUIntSize((ulong)sorted.Length << 1);
        for (var i = 0; i < sorted.Length; i++) listBytes += VarUIntSize((ulong)(i == 0 ? sorted[0] : sorted[i] - sorted[i - 1] - 1));
        var bitmapBytes = 1 + (universe + 7) / 8;
        if (bitmapBytes < listBytes)
        {
            w.WriteVarUInt(1);
            var bits = new byte[(universe + 7) / 8];
            foreach (var i in sorted) bits[i >> 3] |= (byte)(1 << (i & 7));
            w.WriteBytes(bits);
            return;
        }
        w.WriteVarUInt((ulong)sorted.Length << 1);
        for (var i = 0; i < sorted.Length; i++) w.WriteVarUInt((ulong)(i == 0 ? sorted[0] : sorted[i] - sorted[i - 1] - 1));
    }

    private static int VarUIntSize(ulong v)
    {
        var n = 1;
        while (v >= 0x80) { v >>= 7; n++; }
        return n;
    }

    private static int[] ReadIndexSet(ref ByteReader r, int universe)
    {
        var h = r.ReadVarUInt();
        if (h == 1)
        {
            var bits = r.ReadBytes((universe + 7) / 8);
            var list = new List<int>();
            for (var i = 0; i < bits.Length * 8; i++)
            {
                if ((bits[i >> 3] & (1 << (i & 7))) == 0) continue;
                if (i >= universe) throw new LlogFormatException("Block summary bitmap has padding bits set.");
                list.Add(i);
            }
            return [.. list];
        }
        if ((h & 1) != 0) throw new LlogFormatException("Invalid block summary set encoding.");
        var k = h >> 1;
        if (k > (ulong)universe || k > (ulong)r.Remaining) throw new LlogFormatException("Block summary set larger than its list.");
        var result = new int[(int)k];
        long prev = -1;
        for (var i = 0; i < result.Length; i++)
        {
            var d = r.ReadVarUInt();
            if (d >= (ulong)universe) throw new LlogFormatException("Block summary index out of range.");
            var next = prev + 1 + (long)d;
            if (next >= universe) throw new LlogFormatException("Block summary index out of range.");
            result[i] = (int)next;
            prev = next;
        }
        return result;
    }

    private static void DecodeBlockSummaries(SegmentIndex x, ReadOnlySpan<byte> body)
    {
        var r = new ByteReader(body);
        var n = r.ReadCount(3);
        var positions = new List<int>();
        for (var i = 0; i < x.Blocks.Count; i++) if (x.Blocks[i].Type == BlockType.Records) positions.Add(i);
        if (n != positions.Count) throw new LlogFormatException($"Block summaries cover {n} blocks, the directory has {positions.Count} Records blocks.");
        foreach (var p in positions)
        {
            var mask = r.ReadVarUInt();
            if (mask == 0 || mask >> (SeverityExtensions.MaxNumber + 1) != 0) throw new LlogFormatException("Invalid block severity mask.");
            var events = ReadIndexSet(ref r, x.Events.Count);
            var services = ReadIndexSet(ref r, x.Services.Count);
            x.Blocks[p] = x.Blocks[p] with { Summary = new BlockSummary((uint)mask, events, services) };
        }
        if (!r.End) throw new LlogFormatException("Trailing bytes in block summaries.");
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
        // Format 0.3 extension area: absent in 0.1/0.2 footers.
        ulong? lastTag = null;
        while (!r.End)
        {
            var tag = r.ReadVarUInt();
            if (lastTag is { } lt && tag <= lt) throw new LlogFormatException("Footer extension tags must be strictly ascending.");
            lastTag = tag;
            var body = r.ReadBytes(r.ReadCount(1));
            if (tag == ExtensionBlockSummaries) DecodeBlockSummaries(x, body);
            // Unknown tags are skipped: later minor versions may add extensions without breaking this reader.
        }
        return x;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append($"records={RecordCount} seq=[{FirstSequence},{LastSequence}] blocks={Blocks.Count} events={Events.Count}");        return sb.ToString();
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
    // Events and services get a provisional id in order of first appearance; Build() maps them to their
    // position in the sorted footer lists, which is what block summaries reference.
    private readonly Dictionary<(uint, string), int> _eventIds = [];
    private readonly List<(uint Id, string Name)> _eventKeys = [];
    private readonly List<long> _eventCounts = [];
    private readonly Dictionary<string, int> _serviceIds = new(StringComparer.Ordinal);
    private readonly List<string> _serviceNames = [];
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    // Summary of the Records block being accumulated (records are added before their AddBlock call).
    private uint _blockSeverities;
    private readonly HashSet<int> _blockEvents = [];
    private readonly HashSet<int> _blockServices = [];
    private readonly List<(int Position, uint Severities, int[] Events, int[] Services)> _summaries = [];

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
        if (!_eventIds.TryGetValue(key, out var ev))
        {
            ev = _eventKeys.Count;
            _eventIds.Add(key, ev);
            _eventKeys.Add(key);
            _eventCounts.Add(0);
        }
        _eventCounts[ev]++;
        _blockEvents.Add(ev);
        var sev = Math.Min((int)r.Severity, SeverityExtensions.MaxNumber);
        x.SeverityCounts[sev]++;
        _blockSeverities |= 1u << sev;
        if (r.ServiceName is { } svc)
        {
            if (!_serviceIds.TryGetValue(svc, out var sv))
            {
                sv = _serviceNames.Count;
                _serviceIds.Add(svc, sv);
                _serviceNames.Add(svc);
            }
            _blockServices.Add(sv);
        }
        if (!r.TraceId.IsEmpty) _keys.Add(BloomFilter.TraceKey(r.TraceId));
        if (r.OperationId is not null) _keys.Add(BloomFilter.OperationKey(r.OperationId));
        if (r.CorrelationId is not null) _keys.Add(BloomFilter.CorrelationKey(r.CorrelationId));
    }

    /// <summary>Adds a directory entry. A Records entry takes the summary of the records added since the previous Records entry.</summary>
    public void AddBlock(BlockIndexEntry e)
    {
        if (e.Type == BlockType.Records)
        {
            if (e.RecordCount > 0 && _blockSeverities != 0)
                _summaries.Add((_index.Blocks.Count, _blockSeverities, [.. _blockEvents], [.. _blockServices]));
            _blockSeverities = 0;
            _blockEvents.Clear();
            _blockServices.Clear();
        }
        _index.Blocks.Add(e);
    }

    public SegmentIndex Build()
    {
        _index.Events.Clear();
        var eventOrder = Enumerable.Range(0, _eventKeys.Count)
            .OrderBy(i => _eventKeys[i].Id).ThenBy(i => _eventKeys[i].Name, StringComparer.Ordinal).ToArray();
        var eventPosition = new int[eventOrder.Length];
        for (var p = 0; p < eventOrder.Length; p++)
        {
            var i = eventOrder[p];
            eventPosition[i] = p;
            _index.Events.Add(new EventCount(_eventKeys[i].Id, _eventKeys[i].Name, _eventCounts[i]));
        }
        _index.Services.Clear();
        var serviceOrder = Enumerable.Range(0, _serviceNames.Count).OrderBy(i => _serviceNames[i], StringComparer.Ordinal).ToArray();
        var servicePosition = new int[serviceOrder.Length];
        for (var p = 0; p < serviceOrder.Length; p++)
        {
            servicePosition[serviceOrder[p]] = p;
            _index.Services.Add(_serviceNames[serviceOrder[p]]);
        }
        foreach (var (pos, sev, evs, svs) in _summaries)
        {
            var events = evs.Select(i => eventPosition[i]).Order().ToArray();
            var services = svs.Select(i => servicePosition[i]).Order().ToArray();
            _index.Blocks[pos] = _index.Blocks[pos] with { Summary = new BlockSummary(sev, events, services) };
        }
        if (_keys.Count > 0)
        {
            var bloom = new BloomFilter(_keys.Count);
            foreach (var k in _keys) bloom.Add(k);
            _index.Keys = bloom;
        }
        return _index;
    }
}
