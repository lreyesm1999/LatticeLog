using System.Text;
using LatticeLog.Storage;
using LatticeLog.Storage.Format;
using LatticeLog.Storage.Json;

namespace LatticeLog.Tests;

/// <summary>
/// Frozen compatibility vectors (P04-T14/T16, P06-T10). Regenerate only for a new format version:
/// set LATTICELOG_REGENERATE_GOLDEN=1 and run this test class. Committed files must keep decoding forever.
/// </summary>
public class GoldenFileTests
{
    public static string GoldenDir
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "LatticeLog.sln"))) d = d.Parent;
            return Path.Combine(d!.FullName, "tests", "GoldenFiles", "v0.1");
        }
    }

    public static readonly (string Name, CodecId Codec, Compression Compression)[] Variants =
    [
        ("raw", CodecId.Raw, Compression.None),
        ("raw-deflate", CodecId.Raw, Compression.Deflate),
        ("structural", CodecId.Structural, Compression.None),
        ("structural-deflate", CodecId.Structural, Compression.Deflate),
        ("contextual", CodecId.Contextual, Compression.None),
        ("contextual-deflate", CodecId.Contextual, Compression.Deflate),
    ];

    /// <summary>Hand-written records covering every value kind and every record field.</summary>
    public static List<LogRecord> GoldenRecords()
    {
        var res = new ContextSnapshot(ContextKind.Resource, null, [new("service.name", "golden"), new("service.version", "0.1.0")], null);
        var op = new ContextSnapshot(ContextKind.Operation, "PlaceOrder", [new("operation.name", "PlaceOrder"), new("order.id", 991L)], res);
        var req = new ContextSnapshot(ContextKind.Request, null, [new("http.route", "/orders"), new("service.name", "golden-override")], op);
        var d1 = new EventDescriptor(2101, "inventory.stock.timeout", 1, Severity.Error, "Stock query for {productId} timed out after {elapsedMs} ms",
            [new("productId", ValueKind.Int64), new("elapsedMs", ValueKind.Int64), new("warehouse", ValueKind.String, Sensitivity.None, "warehouse code")], "Stock service did not answer");
        var d2 = new EventDescriptor(1001, "order.received", 2, Severity.Info, null,
            [new("orderId", ValueKind.Int64), new("amount", ValueKind.Decimal), new("customer", ValueKind.String, Sensitivity.Personal), new("id", ValueKind.Uuid), new("at", ValueKind.Timestamp), new("ok", ValueKind.Bool)]);
        var trace = TraceId.Parse("4bf92f3577b34da6a3ce929d0e0e4736");
        var t = 1_763_400_000_000_000_000L;
        var list = new List<LogRecord>();
        LogRecord R(long dt, Severity s) => new() { TimestampUnixNanos = t + dt, Severity = s };

        var r1 = R(0, Severity.Info);
        r1.SetBody(Value.From("plain text body"));
        list.Add(r1);

        var r2 = R(1_000, Severity.Debug);
        r2.EventName = "all.kinds";
        r2.EventId = 7;
        r2.Attributes =
        [
            new("null", Value.Null), new("true", true), new("false", false), new("i64min", long.MinValue), new("i64max", long.MaxValue), new("neg", -5L),
            new("u64max", Value.From(ulong.MaxValue)), new("f64", 3.141592653589793), new("negzero", -0.0), new("nan", double.NaN),
            new("snan", Value.From(BitConverter.Int64BitsToDouble(0x7FF0_0000_0000_0001))), new("inf", double.PositiveInfinity),
            new("str", "héllo 世界 🎉"), new("empty", ""), new("lone", "a\uD800b"), new("bytes", Value.From(new byte[] { 0, 1, 2, 254, 255 })),
            new("arr", Value.From([Value.From(1), Value.From("two"), Value.Null, Value.From([Value.From(true)])])),
            new("map", Value.From([new LogAttribute("k", 1L), new LogAttribute("k", 2L), new LogAttribute("nested", Value.From([new LogAttribute("x", "y")]))])),
            new("ts", Value.FromTimestamp(-1_234_567_890_123)), new("dur", Value.FromDuration(1_500_000_000)),
            new("uuid", Value.From(Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301"))), new("dec", Value.From(-123.4500m)), new("decmax", Value.From(decimal.MaxValue)),
            new("dup", "first"), new("dup", "second"),
        ];
        list.Add(r2);

        var r3 = R(2_000, Severity.Error);
        r3.Event = d1;
        r3.Attributes = [new("productId", 42L), new("elapsedMs", 12_000L), new("warehouse", "wh-eu-1")];
        r3.TraceId = trace; r3.SpanId = SpanId.Parse("00f067aa0ba902b7"); r3.TraceFlags = 1;
        r3.OperationId = "op-inv"; r3.ParentOperationId = "op-root"; r3.CorrelationId = "corr-1";
        r3.Context = req;
        r3.ObservedUnixNanos = t + 2_500; r3.MonotonicNanos = 987_654_321;
        list.Add(r3);

        var r4 = R(3_000, Severity.Error); // same context tuple as r3 (context inheritance), schema kind mismatch
        r4.Event = d1;
        r4.Attributes = [new("productId", "not-a-number"), new("elapsedMs", Value.Null), new("warehouse", "wh-eu-1")];
        r4.TraceId = r3.TraceId; r4.SpanId = r3.SpanId; r4.TraceFlags = 1; r4.OperationId = "op-inv"; r4.ParentOperationId = "op-root"; r4.CorrelationId = "corr-1"; r4.Context = req;
        list.Add(r4);

        var r5 = R(4_000, Severity.Fatal);
        r5.EventName = "order.failed";
        r5.Exception = new ExceptionInfo("System.AggregateException", "One or more errors occurred.", "   at Orders.Place()\n   at Api.Post()",
            [new ExceptionInfo("System.TimeoutException", "late", null, []), new ExceptionInfo("System.IO.IOException", null, "   at Socket.Read()", [new ExceptionInfo("Inner.Most", "x", null, [])])]);
        r5.TraceId = trace; r5.Context = op; r5.Flags = RecordFlags.Redacted;
        r5.Attributes = [new("authorization", "[REDACTED]")];
        list.Add(r5);

        var r6 = R(-10_000, Severity.Unspecified); // out of order timestamp
        r6.Event = d2;
        r6.Attributes = [new("orderId", 991L), new("amount", Value.From(10.50m)), new("customer", "alice"), new("id", Value.From(Guid.Parse("00000000-0000-0000-0000-000000000001"))), new("at", Value.FromTimestamp(t)), new("ok", true)];
        r6.Context = res;
        list.Add(r6);

        var r7 = R(5_000, Severity.Warn);
        r7.SetBody(Value.From(Encoding.Latin1.GetBytes("raw café bytes\r")));
        r7.Flags = RecordFlags.RawImport;
        r7.Context = res;
        list.Add(r7);

        for (var i = 0; i < 20; i++) // repetition for dictionaries/trace table
        {
            var r = R(6_000 + i * 100, Severity.Info);
            r.Event = d2;
            r.Attributes = [new("orderId", 1000L + i), new("amount", Value.From(1.25m * i)), new("customer", i % 2 == 0 ? "bob" : "carol"), new("id", Value.From(new Guid(i, 0, 0, new byte[8]))), new("at", Value.FromTimestamp(t + i)), new("ok", i % 3 != 0)];
            r.TraceId = i % 4 == 0 ? trace : TraceId.Parse("0af7651916cd43dd8448eb211c80319c");
            r.Context = op;
            r.OperationId = "op-" + (i % 3);
            list.Add(r);
        }
        TestData.AssignSequences(list, 100);
        return list;
    }

    private static void WriteGolden(string path, CodecId codec, Compression compression, List<LogRecord> records)
    {
        using var w = new SegmentWriter(path, 1, new SegmentWriterOptions { Codec = codec, Compression = compression, MaxRecordsPerBlock = 8, CreatedUnixNanos = 0 });
        w.Append(records, 0, 12);
        w.Append(records, 12, records.Count - 12);
        w.Seal();
    }

    [Fact]
    public void Golden_files_decode_to_expected_records()
    {
        var dir = GoldenDir;
        var expectedRecords = GoldenRecords();
        if (Environment.GetEnvironmentVariable("LATTICELOG_REGENERATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(dir);
            foreach (var (name, codec, comp) in Variants)
            {
                var target = Path.Combine(dir, name + ".llog");
                File.Delete(target); // regeneration only: explicit golden file path
                WriteGolden(target, codec, comp, expectedRecords);
            }
            using (var fs = File.Create(Path.Combine(dir, "expected.llog.jsonl")))
            using (var jw = new JsonlExchangeWriter(fs, null)) foreach (var r in expectedRecords) jw.Write(r);
            var bytes = File.ReadAllBytes(Path.Combine(dir, "structural.llog"));
            long footerOffset;
            using (var sr = SegmentReader.Open(Path.Combine(dir, "structural.llog"))) footerOffset = sr.FooterOffset;
            File.WriteAllBytes(Path.Combine(dir, "corrupt-truncated.llog"), bytes[..(int)(footerOffset - 10)]); // torn last records block
            var flipped = (byte[])bytes.Clone();
            flipped[300] ^= 0x40;
            File.WriteAllBytes(Path.Combine(dir, "corrupt-bitflip.llog"), flipped);
        }
        Assert.True(File.Exists(Path.Combine(dir, "expected.llog.jsonl")), "golden files missing: run with LATTICELOG_REGENERATE_GOLDEN=1");
        var expected = new JsonlExchangeReader().Read(new StreamReader(Path.Combine(dir, "expected.llog.jsonl"))).ToList();
        Assert.Equal(expectedRecords.Count, expected.Count);
        for (var i = 0; i < expected.Count; i++) Assert.True(expectedRecords[i].Equals(expected[i]), $"jsonl record {i}: {TestData.Describe(expectedRecords[i], expected[i])}");
        foreach (var (name, codec, comp) in Variants)
        {
            using var reader = SegmentReader.Open(Path.Combine(dir, name + ".llog"));
            Assert.True(reader.IsSealed, name);
            var report = new ScanReport();
            var got = reader.ReadBlocks(report).SelectMany(b => b.Records).ToList();
            Assert.True(report.IsClean, name + ": " + string.Join("; ", report.Issues));
            Assert.Equal(expected.Count, got.Count);
            for (var i = 0; i < got.Count; i++) Assert.True(expected[i].Equals(got[i]), $"{name} record {i}: {TestData.Describe(expected[i], got[i])}");
            if (comp == Compression.None)
            {
                // The encoder is deterministic for uncompressed variants (DEFLATE output may differ across zlib versions).
                var tmp = Path.Combine(TestData.TempDir(), name + ".llog");
                WriteGolden(tmp, codec, comp, GoldenRecords());
                Assert.Equal(File.ReadAllBytes(Path.Combine(dir, name + ".llog")), File.ReadAllBytes(tmp));
            }
        }
    }

    [Fact]
    public void Corrupt_golden_files_are_reported_never_silent()
    {
        var dir = GoldenDir;
        using (var r = SegmentReader.Open(Path.Combine(dir, "corrupt-truncated.llog")))
        {
            Assert.False(r.IsSealed);
            var rep = new ScanReport();
            var n = r.ReadBlocks(rep).Sum(b => b.Records.Count);
            Assert.Contains(rep.Issues, i => i.Kind == IssueKind.TruncatedTail);
            Assert.True(n < GoldenRecords().Count);
        }
        using (var r = SegmentReader.Open(Path.Combine(dir, "corrupt-bitflip.llog")))
        {
            var rep = new ScanReport();
            var n = r.ReadBlocks(rep).Sum(b => b.Records.Count);
            Assert.False(rep.IsClean);
            Assert.True(n < GoldenRecords().Count);
        }
    }
}
