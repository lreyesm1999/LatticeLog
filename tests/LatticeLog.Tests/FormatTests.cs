using LatticeLog.Storage;
using LatticeLog.Storage.Codecs;
using LatticeLog.Storage.Format;

namespace LatticeLog.Tests;

public class FormatTests
{
    public static IEnumerable<object[]> CodecMatrix()
    {
        foreach (var c in new[] { CodecId.Raw, CodecId.Structural, CodecId.Contextual })
            foreach (var z in new[] { Compression.None, Compression.Deflate })
                yield return [c, z];
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(63L)]
    [InlineData(-64L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void Varint_and_zigzag_round_trip(long v)
    {
        var w = new ByteWriter();
        w.WriteVarInt(v);
        w.WriteVarUInt(unchecked((ulong)v));
        var r = new ByteReader(w.WrittenSpan);
        Assert.Equal(v, r.ReadVarInt());
        Assert.Equal(unchecked((ulong)v), r.ReadVarUInt());
        Assert.True(r.End);
    }

    [Fact]
    public void Varint_rejects_overlong_and_truncated_input()
    {
        Assert.Throws<LlogFormatException>(() => { var r = new ByteReader(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F }); r.ReadVarUInt(); });
        Assert.Throws<LlogFormatException>(() => { var r = new ByteReader(new byte[] { 0x80, 0x80 }); r.ReadVarUInt(); });
        Assert.Throws<LlogFormatException>(() => { var r = new ByteReader(new byte[] { 0x05, (byte)'a' }); r.ReadString(); });
    }

    [Fact]
    public void Property_value_round_trip_all_kinds()
    {
        var td = new TestData(42);
        for (var i = 0; i < 5000; i++)
        {
            var v = td.AnyValue();
            var w = new ByteWriter();
            ValueCodec.Write(w, v);
            var r = new ByteReader(w.WrittenSpan);
            var back = ValueCodec.Read(ref r);
            Assert.True(r.End);
            Assert.True(v.Equals(back), $"iteration {i}: {v} != {back}");
        }
    }

    [Fact]
    public void Doubles_preserve_exact_bits()
    {
        foreach (var bits in new long[] { 0x7FF8_0000_0000_0000, 0x7FF0_0000_0000_0001, unchecked((long)0x8000_0000_0000_0000), 0x7FF0_0000_0000_0000 })
        {
            var v = Value.From(BitConverter.Int64BitsToDouble(bits));
            var w = new ByteWriter();
            ValueCodec.Write(w, v);
            var r = new ByteReader(w.WrittenSpan);
            Assert.Equal(bits, ValueCodec.Read(ref r).AsDoubleBits());
        }
    }

    [Fact]
    public void Lone_surrogates_in_values_are_preserved_exactly()
    {
        var s = "a\uD800b\uDFFFc";
        var w = new ByteWriter();
        ValueCodec.Write(w, Value.From(s));
        var r = new ByteReader(w.WrittenSpan);
        Assert.Equal(s, ValueCodec.Read(ref r).AsString());
    }

    [Theory]
    [MemberData(nameof(CodecMatrix))]
    public void Property_segment_round_trip(CodecId codec, Compression compression)
    {
        for (var seed = 0; seed < 6; seed++)
        {
            var td = new TestData(seed * 31 + (int)codec * 7 + (int)compression);
            var records = td.Records(1500);
            TestData.AssignSequences(records, 10);
            var expected = records.Select(r => r.ShallowClone()).ToList();
            var dir = TestData.TempDir();
            var path = Path.Combine(dir, "a.llog");
            using (var w = new SegmentWriter(path, 1, new SegmentWriterOptions { Codec = codec, Compression = compression, MaxRecordsPerBlock = 257 }))
            {
                w.Append(records, 0, 700);
                w.Append(records, 700, 800);
                w.Seal();
            }
            using var reader = SegmentReader.Open(path);
            Assert.True(reader.IsSealed);
            Assert.Equal(1500, reader.Index!.RecordCount);
            var report = new ScanReport();
            var decoded = reader.ReadBlocks(report).SelectMany(b => b.Records).ToList();
            Assert.True(report.IsClean, string.Join("\n", report.Issues));
            Assert.Equal(expected.Count, decoded.Count);
            for (var i = 0; i < expected.Count; i++)
                Assert.True(expected[i].Equals(decoded[i]), $"seed {seed} record {i}: {TestData.Describe(expected[i], decoded[i])}");
            // Random access through the footer directory gives identical records.
            var recordBlocks = reader.Index.Blocks.Where(b => b.Type == BlockType.Records).ToList();
            var last = reader.ReadBlockAt(recordBlocks[^1]);
            Assert.Equal(expected[^1], last.Records[^1]);
            var first = reader.ReadBlockAt(recordBlocks[0]);
            Assert.Equal(expected[0], first.Records[0]);
        }
    }

    [Fact]
    public void Index_summarizes_segment()
    {
        var td = new TestData(7);
        var records = td.Records(2000);
        TestData.AssignSequences(records, 1);
        var path = Path.Combine(TestData.TempDir(), "i.llog");
        SegmentIndex idx;
        using (var w = new SegmentWriter(path, 3))
        {
            w.Append(records, 0, records.Count);
            idx = w.Seal();
        }
        Assert.Equal(2000, idx.RecordCount);
        Assert.Equal(records.Min(r => r.TimestampUnixNanos), idx.MinTimestamp);
        Assert.Equal(records.Max(r => r.TimestampUnixNanos), idx.MaxTimestamp);
        Assert.Equal(1UL, idx.FirstSequence);
        Assert.Equal(2000UL, idx.LastSequence);
        Assert.Equal(2000, idx.SeverityCounts.Sum());
        Assert.Equal(2000, idx.Events.Sum(e => e.Count));
        foreach (var t in records.Where(r => !r.TraceId.IsEmpty).Select(r => r.TraceId).Distinct())
            Assert.True(idx.Keys!.MightContain(BloomFilter.TraceKey(t)));
        using var reader = SegmentReader.Open(path);
        Assert.Equal(idx.RecordCount, reader.Index!.RecordCount);
        Assert.Equal(idx.Blocks.Count, reader.Index.Blocks.Count);
    }

    [Fact]
    public void Bloom_false_positive_rate_is_bounded()
    {
        var b = new BloomFilter(10_000, 0.01);
        for (var i = 0; i < 10_000; i++) b.Add("t:" + i);
        for (var i = 0; i < 10_000; i++) Assert.True(b.MightContain("t:" + i));
        var fp = Enumerable.Range(0, 20_000).Count(i => b.MightContain("x:" + i));
        Assert.True(fp < 20_000 * 0.03, $"false positives {fp}");
    }

    [Fact]
    public void Header_rejects_unknown_major_and_required_features()
    {
        var h = new byte[Llog.FileHeaderSize];
        new FileHeader(1, 0, 0, 0, 1, 0).Write(h);
        Assert.Throws<LlogFormatException>(() => FileHeader.Read(h));
        new FileHeader(0, 1, 0x10, 0, 1, 0).Write(h);
        Assert.Throws<LlogFormatException>(() => FileHeader.Read(h));
        new FileHeader(0, 9, 0, 0xFFFF, 1, 0).Write(h); // newer minor + unknown optional features are accepted
        Assert.Equal(9, FileHeader.Read(h).Minor);
    }

    [Fact]
    public void Frame_validation_rejects_bombs_and_inconsistencies()
    {
        Assert.NotNull(new FrameHeader(BlockType.Records, CodecId.Raw, Compression.Deflate, 0, 10, 10_000_000, 1, 0).Validate());
        Assert.NotNull(new FrameHeader(BlockType.Records, CodecId.Raw, Compression.None, 0, 10, 11, 1, 0).Validate());
        Assert.NotNull(new FrameHeader((BlockType)9, CodecId.Raw, Compression.None, 0, 10, 10, 1, 0).Validate());
        Assert.NotNull(new FrameHeader(BlockType.Records, (CodecId)9, Compression.None, 0, 10, 10, 1, 0).Validate());
        Assert.Null(new FrameHeader(BlockType.Records, CodecId.Structural, Compression.Deflate, 0, 10, 100, 1, 0).Validate());
    }

    [Fact]
    public void Structural_codecs_are_smaller_than_raw_on_repetitive_events()
    {
        var td = new TestData(3);
        var desc = new EventDescriptor(1, "http.request", 1, Severity.Info, null,
            [new("method", ValueKind.String), new("path", ValueKind.String), new("status", ValueKind.Int64), new("elapsed_ms", ValueKind.Double)]);
        var root = new ContextSnapshot(ContextKind.Resource, null, [new("service.name", "api")], null);
        var records = Enumerable.Range(0, 4000).Select(i => new LogRecord
        {
            TimestampUnixNanos = 1_700_000_000_000_000_000 + i * 1_000_000L,
            Severity = Severity.Info,
            Event = desc,
            Attributes = [new("method", "GET"), new("path", "/api/items/" + (i % 50)), new("status", 200L), new("elapsed_ms", 1.5 + i % 7)],
            Context = root,
        }).ToList();
        TestData.AssignSequences(records);
        long Size(CodecId c)
        {
            var t = new SegmentTables();
            var (s, ctx) = t.CollectNew(records, 0, records.Count);
            var w = new ByteWriter();
            t.EncodeSchemaBlock(s, w);
            t.EncodeContextBlock(ctx, w);
            RecordCodec.Encode(c, records, 0, records.Count, t, w);
            return w.Length;
        }
        var raw = Size(CodecId.Raw);
        var st = Size(CodecId.Structural);
        Assert.True(st < raw * 0.6, $"structural {st} vs raw {raw}");
    }
}
