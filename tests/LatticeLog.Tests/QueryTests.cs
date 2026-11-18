using LatticeLog.Query;
using LatticeLog.Storage;
using LatticeLog.Storage.Json;

namespace LatticeLog.Tests;

public class QueryTests
{
    private static readonly Lazy<(string Root, List<LogRecord> All)> Fixture = new(Build);

    private static (string, List<LogRecord>) Build()
    {
        var root = TestData.TempDir();
        var td = new TestData(2024);
        using (var w = StoreWriter.Open(root, new StoreWriterOptions { SegmentMaxBytes = 60_000, Segment = new SegmentWriterOptions { MaxRecordsPerBlock = 128 } }))
        {
            for (var i = 0; i < 12; i++) w.WriteBatch(td.Records(500, startTs: 1_763_400_000_000_000_000 + i * 3_000_000_000_000L));
        }
        var rep = new ScanReport();
        var all = StoreReader.Open(root).ReadAll(rep).ToList();
        Assert.True(rep.IsClean);
        return (root, all);
    }

    public static IEnumerable<object[]> Queries() =>
    [
        ["level >= ERROR"],
        ["level = WARN"],
        ["service = 'svc-1'"],
        ["service IN ('svc-0', 'svc-2') AND level >= INFO"],
        ["event STARTSWITH 'event.'"],
        ["event = 'free.alpha'"],
        ["NOT EXISTS(trace_id)"],
        ["operation_id = 'op-3' OR correlation_id = 'corr-1'"],
        ["text CONTAINS 'timeout'"],
        ["exception.type CONTAINS 'retry'"],
        ["alpha > 10 AND alpha < 500000"],
        ["ctx.host.name = 'h1' AND level < DEBUG"],
        ["time >= '2025-11-17T17:40:00Z' AND time < '2025-11-17T18:30:00Z'"],
        ["body IS NULL AND level >= 9"],
        ["seq > 1000 AND seq <= 1200"],
    ];

    [Theory]
    [MemberData(nameof(Queries))]
    public void Indexed_query_equals_full_scan_oracle(string where)
    {
        var (root, all) = Fixture.Value;
        var engine = QueryEngine.Open(root);
        var indexed = engine.Execute(new QueryRequest { Where = where, Limit = QueryRequest.MaxLimit });
        var oracle = engine.Execute(new QueryRequest { Where = where, Limit = QueryRequest.MaxLimit, UseIndexes = false });
        var linq = all.Where(Evaluator.Compile(QueryParser.Parse(where))).Select(r => r.Sequence).ToList();
        Assert.False(indexed.Partial);
        Assert.True(linq.Count > 0, "query should match the fixture: " + where);
        Assert.Equal(linq, oracle.Hits.Select(h => h.Record.Sequence));
        Assert.Equal(linq, indexed.Hits.Select(h => h.Record.Sequence));
    }

    [Fact]
    public void Trace_lookup_prunes_segments_with_bloom_filter()
    {
        var (root, all) = Fixture.Value;
        var engine = QueryEngine.Open(root);
        var trace = all.First(r => !r.TraceId.IsEmpty).TraceId;
        var res = engine.Trace(trace);
        Assert.Equal(all.Count(r => r.TraceId == trace), res.Hits.Count);
        var unknown = engine.Trace(new TraceId(0xdead, 0xbeef));
        Assert.Empty(unknown.Hits);
        Assert.True(unknown.Stats.SegmentsPruned.GetValueOrDefault("bloom") > 0, unknown.Plan);
        Assert.Contains("bloom", unknown.Plan);
    }

    [Theory]
    [InlineData(SortOrder.Ascending)]
    [InlineData(SortOrder.Descending)]
    public void Cursor_pagination_returns_every_match_exactly_once(SortOrder order)
    {
        var (root, all) = Fixture.Value;
        var engine = QueryEngine.Open(root);
        const string where = "level >= INFO";
        var expected = all.Where(Evaluator.Compile(QueryParser.Parse(where))).Select(r => r.Sequence).ToList();
        if (order == SortOrder.Descending) expected.Reverse();
        var got = new List<ulong>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = engine.Execute(new QueryRequest { Where = where, Limit = 333, Cursor = cursor, Order = order });
            got.AddRange(page.Hits.Select(h => h.Record.Sequence));
            cursor = page.NextCursor;
            pages++;
        } while (cursor is not null && pages < 1000);
        Assert.Equal(expected, got);
        Assert.Throws<ArgumentException>(() => engine.Execute(new QueryRequest { Where = "level >= ERROR", Cursor = Cursor.Encode(engine.Store.Info.StoreId, "other", 5, order) }));
    }

    [Fact]
    public void Index_only_aggregation_matches_scan()
    {
        var (root, all) = Fixture.Value;
        var engine = QueryEngine.Open(root);
        var byIndex = engine.Aggregate(new QueryRequest(), "event", top: 1000);
        Assert.True(byIndex.UsedIndexOnly);
        Assert.Equal(all.Count, byIndex.Total);
        var expected = all.GroupBy(r => r.EffectiveEventName ?? "").ToDictionary(g => g.Key.Length == 0 ? "(none)" : g.Key, g => (long)g.Count());
        Assert.Equal(expected.OrderBy(k => k.Key), byIndex.Groups.ToDictionary(g => g.Key, g => g.Count).OrderBy(k => k.Key));
        var filtered = engine.Aggregate(new QueryRequest { Where = "level >= ERROR" }, "service");
        Assert.False(filtered.UsedIndexOnly);
        Assert.Equal(all.Count(r => r.Severity >= Severity.Error), filtered.Total);
        var hist = engine.Aggregate(new QueryRequest(), "time", TimeSpan.FromHours(1));
        Assert.Equal(all.Count, hist.Total);
    }

    [Theory]
    [InlineData("level >=")]
    [InlineData("(level = ERROR")]
    [InlineData("service = 'unterminated")]
    [InlineData("a = 1 b = 2")]
    [InlineData("level ! 3")]
    public void Syntax_errors_are_reported_with_position(string q)
    {
        var ex = Assert.Throws<QuerySyntaxException>(() => QueryParser.Parse(q));
        Assert.True(ex.Position >= 0);
    }

    [Fact]
    public void Parser_limits_depth_and_length()
    {
        Assert.Throws<QuerySyntaxException>(() => QueryParser.Parse(new string('(', 200) + "a=1" + new string(')', 200)));
        Assert.Throws<QuerySyntaxException>(() => QueryParser.Parse("a = '" + new string('x', 9000) + "'"));
    }

    [Fact]
    public void References_are_stable_across_compaction()
    {
        var root = TestData.TempDir();
        var td = new TestData(77);
        StoreWriter w = StoreWriter.Open(root, new StoreWriterOptions { SegmentMaxBytes = 30_000 });
        for (var i = 0; i < 8; i++) w.WriteBatch(td.Records(300));
        w.Dispose();
        var before = StoreReader.Open(root);
        var rep = new ScanReport();
        var all = before.ReadAll(rep).ToList();
        var segmentsBefore = before.SegmentIds.Count;
        var sample = all[1234];

        using (var w2 = StoreWriter.Open(root))
        {
            var results = Compactor.CompactAll(root, CompactionPolicy.Compact, w2.AllocateSegmentId);
            Assert.NotEmpty(results);
            Assert.All(results, r => Assert.True(r.OutputBytes <= r.InputBytes * 1.05, $"{r.OutputBytes} vs {r.InputBytes}"));
        }
        var after = StoreReader.Open(root);
        Assert.True(after.SegmentIds.Count < segmentsBefore);
        var rep2 = new ScanReport();
        var all2 = after.ReadAll(rep2).ToList();
        Assert.True(rep2.IsClean);
        Assert.Equal(all.Count, all2.Count);
        for (var i = 0; i < all.Count; i++) Assert.True(all[i].Equals(all2[i]), $"record {i}");
        var hit = new QueryEngine(after).GetBySequence(sample.Sequence);
        Assert.NotNull(hit);
        Assert.Equal(sample, hit!.Record);
        var reference = CanonicalJson.ReferenceFor(after.Info.StoreId, sample.Sequence);
        Assert.True(CanonicalJson.TryParseReference(reference, out var sid, out var seq) && sid == after.Info.StoreId && seq == sample.Sequence);
    }

    [Fact]
    public void Interrupted_compaction_is_rolled_back_on_open()
    {
        var root = TestData.TempDir();
        var td = new TestData(78);
        using (var w = StoreWriter.Open(root, new StoreWriterOptions { SegmentMaxBytes = 20_000 }))
            for (var i = 0; i < 4; i++) w.WriteBatch(td.Records(200));
        var reader = StoreReader.Open(root);
        var count = reader.ReadAll(new ScanReport()).Count();
        // Simulate a crash after "started" and after the output file appeared but before "committed".
        Manifest.Append(root, new System.Text.Json.Nodes.JsonObject
        {
            ["op"] = "compaction_started",
            ["inputs"] = new System.Text.Json.Nodes.JsonArray(reader.SegmentIds.Take(2).Select(i => (System.Text.Json.Nodes.JsonNode)i).ToArray()),
            ["output"] = 999UL,
        });
        File.Copy(reader.SegmentPath(reader.SegmentIds[0]), StoreLayout.SegmentPath(root, 999));
        // Readers must ignore the uncommitted output (no duplicates)...
        Assert.Equal(count, StoreReader.Open(root).ReadAll(new ScanReport()).Count());
        // ...and opening the writer rolls it back.
        using (StoreWriter.Open(root)) { }
        Assert.False(File.Exists(StoreLayout.SegmentPath(root, 999)));
        Assert.Equal(count, StoreReader.Open(root).ReadAll(new ScanReport()).Count());
    }

    [Fact]
    public void Lossless_jsonl_exchange_round_trips()
    {
        var td = new TestData(9);
        var records = td.Records(2000);
        TestData.AssignSequences(records);
        var ms = new MemoryStream();
        using (var w = new JsonlExchangeWriter(ms, "store-x", leaveOpen: true)) foreach (var r in records) w.Write(r);
        ms.Position = 0;
        var reader = new JsonlExchangeReader();
        var back = reader.Read(new StreamReader(ms)).ToList();
        Assert.Equal("store-x", reader.StoreId);
        Assert.Equal(records.Count, back.Count);
        for (var i = 0; i < records.Count; i++) Assert.True(records[i].Equals(back[i]), $"{i}: {TestData.Describe(records[i], back[i])}");
    }
}
