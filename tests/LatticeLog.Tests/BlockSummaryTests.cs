using LatticeLog.Query;
using LatticeLog.Storage;
using LatticeLog.Storage.Format;

namespace LatticeLog.Tests;

/// <summary>Format 0.3 block summaries (specs/llog-format-v0.md §7.4): footer encoding and block-level pruning.</summary>
public class BlockSummaryTests
{
    private const int BlockSize = 256;
    private static readonly Lazy<(string Root, List<LogRecord> All)> Fixture = new(Build);

    /// <summary>
    /// One segment of 20,000 records in 256-record blocks. Most records are the same INFO event; the rare events,
    /// severities and services are clustered in a few blocks, like a real incident in a long log.
    /// </summary>
    private static List<LogRecord> Records()
    {
        var api = new ContextSnapshot(ContextKind.Resource, null, [new("service.name", "api")], null);
        var worker = new ContextSnapshot(ContextKind.Resource, null, [new("service.name", "worker")], null);
        var nested = new ContextSnapshot(ContextKind.Request, "req", [new("service.name", "api-override")], api);
        var ok = new EventDescriptor(1001, "http.request.completed", 1, Severity.Info, "{status}", [new("status", ValueKind.Int64)]);
        var list = new List<LogRecord>();
        var t = 1_763_400_000_000_000_000L;
        for (var i = 0; i < 20_000; i++)
        {
            t += 1_000_000;
            var r = new LogRecord { TimestampUnixNanos = t, Severity = Severity.Info, Event = ok, Context = api, Attributes = [new("status", 200L)] };
            if (i is >= 15_000 and < 15_012) r = new LogRecord { TimestampUnixNanos = t, Severity = Severity.Error, EventName = "bridge_emit_failed", Context = api };
            else if (i % 3_000 == 1_500) r = new LogRecord { TimestampUnixNanos = t, Severity = Severity.Warn, EventName = "cache.miss", Context = api };
            else if (i is >= 8_000 and < 8_300) r = new LogRecord { TimestampUnixNanos = t, Severity = Severity.Debug, EventName = "job.tick", Context = worker };
            else if (i == 9_100) r = new LogRecord { TimestampUnixNanos = t, Severity = Severity.Warn, EventName = "job.slow", Context = worker };
            else if (i == 12_345) r = new LogRecord { TimestampUnixNanos = t, Severity = Severity.Fatal, Context = nested }; // no event name
            else if (i == 17_777) r = new LogRecord { TimestampUnixNanos = t, Severity = Severity.Error, EventName = "orphan" }; // no service
            list.Add(r);
        }
        return list;
    }

    private static (string, List<LogRecord>) Build()
    {
        var root = TestData.TempDir();
        using (var w = StoreWriter.Open(root, new StoreWriterOptions { SegmentMaxBytes = 64L << 20, Segment = new SegmentWriterOptions { MaxRecordsPerBlock = BlockSize } }))
        {
            var records = Records();
            for (var i = 0; i < records.Count; i += 1_000) w.WriteBatch(records.GetRange(i, Math.Min(1_000, records.Count - i)));
        }
        var rep = new ScanReport();
        var all = StoreReader.Open(root).ReadAll(rep).ToList();
        Assert.True(rep.IsClean);
        return (root, all);
    }

    public static IEnumerable<object[]> Queries() =>
    [
        ["level >= ERROR"],
        ["level > WARN"],
        ["level = WARN"],
        ["level >= ERROR AND event = 'bridge_emit_failed'"],
        ["event = 'cache.miss'"],
        ["event IN ('cache.miss', 'job.slow')"],
        ["event = 'job.tick' AND level >= WARN"],
        ["event = 'never.seen'"],
        ["service = 'worker'"],
        ["service = 'worker' AND level >= WARN"],
        ["service IN ('api-override', 'nobody')"],
        ["service = 'api' AND event = 'orphan'"],
        ["event = 'cache.miss' AND time >= '2025-11-17T17:20:05Z'"],
        ["level >= FATAL OR event = 'job.slow'"],
        ["NOT event = 'http.request.completed'"],
    ];

    [Theory]
    [MemberData(nameof(Queries))]
    public void Block_pruned_query_equals_full_scan_oracle(string where)
    {
        var (root, all) = Fixture.Value;
        var engine = QueryEngine.Open(root);
        var linq = all.Where(Evaluator.Compile(QueryParser.Parse(where))).Select(r => r.Sequence).ToList();
        foreach (var order in new[] { SortOrder.Ascending, SortOrder.Descending })
        {
            var indexed = engine.Execute(new QueryRequest { Where = where, Limit = QueryRequest.MaxLimit, Order = order });
            var oracle = engine.Execute(new QueryRequest { Where = where, Limit = QueryRequest.MaxLimit, Order = order, UseIndexes = false });
            var expected = order == SortOrder.Ascending ? linq : Enumerable.Reverse(linq).ToList();
            Assert.False(indexed.Partial);
            Assert.Equal(expected, oracle.Hits.Select(h => h.Record.Sequence));
            Assert.Equal(expected, indexed.Hits.Select(h => h.Record.Sequence));
        }
    }

    [Fact]
    public void Store_has_one_segment_with_a_summary_for_every_records_block()
    {
        var (root, _) = Fixture.Value;
        var store = StoreReader.Open(root);
        var idx = Assert.Single(store.Indexes.Values);
        Assert.NotNull(idx); // sealed
        var records = idx.Blocks.Where(b => b.Type == BlockType.Records).ToList();
        Assert.True(records.Count >= 20_000 / BlockSize);
        Assert.All(records, b => Assert.NotNull(b.Summary));
    }

    [Fact]
    public void Event_filter_skips_blocks_inside_a_segment()
    {
        var (root, all) = Fixture.Value;
        var engine = QueryEngine.Open(root);
        const string where = "level >= ERROR AND event = 'bridge_emit_failed'";
        var first = engine.Execute(new QueryRequest { Where = where, Limit = 1 });
        var oracle = engine.Execute(new QueryRequest { Where = where, Limit = 1, UseIndexes = false });
        Assert.Equal(oracle.Hits.Select(h => h.Record.Sequence), first.Hits.Select(h => h.Record.Sequence));
        Assert.True(first.Stats.BlocksPruned > 0, first.Plan);
        // The 12 matches sit in at most two blocks: only those are decoded, against ~15,000 records for the oracle.
        Assert.True(first.Stats.RecordsDecoded <= 2 * BlockSize, $"decoded {first.Stats.RecordsDecoded}");
        Assert.True(oracle.Stats.RecordsDecoded >= 15_000);

        var every = engine.Execute(new QueryRequest { Where = where, Limit = QueryRequest.MaxLimit });
        Assert.Equal(12, every.Hits.Count);
        Assert.True(every.Stats.RecordsDecoded <= 2 * BlockSize, $"decoded {every.Stats.RecordsDecoded}");
        Assert.Contains("blocks of segments written in format 0.3", every.Plan);

        var none = engine.Execute(new QueryRequest { Where = "service = 'worker' AND level >= FATAL" });
        Assert.Empty(none.Hits);
        Assert.Equal(0, none.Stats.RecordsDecoded);
        Assert.True(all.Count > 0);
    }

    [Theory]
    [InlineData(SortOrder.Ascending)]
    [InlineData(SortOrder.Descending)]
    public void Cursor_pagination_over_pruned_blocks_returns_every_match_once(SortOrder order)
    {
        var (root, all) = Fixture.Value;
        var engine = QueryEngine.Open(root);
        const string where = "event IN ('cache.miss', 'bridge_emit_failed')";
        var expected = all.Where(Evaluator.Compile(QueryParser.Parse(where))).Select(r => r.Sequence).ToList();
        if (order == SortOrder.Descending) expected.Reverse();
        var got = new List<ulong>();
        string? cursor = null;
        do
        {
            var page = engine.Execute(new QueryRequest { Where = where, Limit = 5, Cursor = cursor, Order = order });
            got.AddRange(page.Hits.Select(h => h.Record.Sequence));
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(expected, got);
    }

    [Fact]
    public void Aggregate_with_an_event_filter_decodes_only_matching_blocks()
    {
        var (root, all) = Fixture.Value;
        var engine = QueryEngine.Open(root);
        var agg = engine.Aggregate(new QueryRequest { Where = "event = 'job.tick'" }, "service");
        Assert.Equal(all.Count(r => r.EffectiveEventName == "job.tick"), agg.Total);
        Assert.True(agg.Stats.BlocksPruned > 0);
        Assert.True(agg.Stats.RecordsDecoded < 4 * BlockSize, $"decoded {agg.Stats.RecordsDecoded}");
    }

    // ------------------------------------------------------------------ footer encoding

    private static (string Path, SegmentIndex Index) WriteSegment(int maxRecordsPerBlock = 64)
    {
        var path = Path.Combine(TestData.TempDir(), "seg.llog");
        var all = Records();
        var records = all.Take(2_000).Concat(all.Skip(14_990).Take(30)).Concat(all.Skip(12_340).Take(10)).ToList();
        TestData.AssignSequences(records);
        using var w = new SegmentWriter(path, 1, new SegmentWriterOptions { MaxRecordsPerBlock = maxRecordsPerBlock, CreatedUnixNanos = 0 });
        w.Append(records, 0, records.Count);
        return (path, w.Seal());
    }

    private static byte[] Encode(SegmentIndex idx)
    {
        var w = new ByteWriter();
        idx.Encode(w);
        var bytes = w.WrittenSpan.ToArray();
        w.Release();
        return bytes;
    }

    [Fact]
    public void Summaries_round_trip_and_match_the_records_of_each_block()
    {
        var (path, written) = WriteSegment();
        using var reader = SegmentReader.Open(path);
        var idx = reader.Index!;
        Assert.Equal(Llog.OptionalFooterCatalog | Llog.OptionalBlockSummaries, reader.Header.OptionalFeatures);
        Assert.Equal(3, reader.Header.Minor);
        Assert.Equal(written.Blocks, idx.Blocks);
        foreach (var b in idx.Blocks.Where(b => b.Type == BlockType.Records))
        {
            var recs = reader.ReadBlockAt(b).Records;
            var s = b.Summary!;
            Assert.Equal(recs.Aggregate(0u, (m, r) => m | 1u << (int)r.Severity), s.SeverityMask);
            Assert.Equal(recs.Select(r => r.EffectiveEventName ?? "").Distinct().Order(StringComparer.Ordinal),
                s.EventIndexes.Select(i => idx.Events[i].Name).Order(StringComparer.Ordinal));
            Assert.Equal(recs.Select(r => r.ServiceName).OfType<string>().Distinct().Order(StringComparer.Ordinal),
                s.ServiceIndexes.Select(i => idx.Services[i]));
        }
        // Recovery rebuilds the same summaries from the blocks.
        var rebuilt = reader.RebuildIndex(new ScanReport());
        Assert.Equal(idx.Blocks, rebuilt.Blocks);
        Assert.Equal(Encode(idx), Encode(rebuilt));
    }

    [Fact]
    public void Unknown_footer_extensions_are_skipped_and_tags_must_ascend()
    {
        var (_, idx) = WriteSegment();
        var bytes = Encode(idx);
        var w = new ByteWriter();
        w.WriteBytes(bytes);
        w.WriteVarUInt(9); // a tag from a later minor version
        w.WriteLengthPrefixed([1, 2, 3]);
        var decoded = SegmentIndex.Decode(w.WrittenSpan);
        Assert.Equal(idx.Blocks, decoded.Blocks);

        w.WriteVarUInt(9); // the same tag again: not ascending
        w.WriteLengthPrefixed([]);
        Assert.Throws<LlogFormatException>(() => SegmentIndex.Decode(w.WrittenSpan));
        w.Release();
    }

    [Fact]
    public void Malformed_summaries_make_the_footer_invalid()
    {
        var (_, idx) = WriteSegment();
        var pos = idx.Blocks.FindIndex(b => b.Type == BlockType.Records);
        var good = idx.Blocks[pos];

        idx.Blocks[pos] = good with { Summary = new BlockSummary(good.Summary!.SeverityMask, [idx.Events.Count], []) };
        Assert.Throws<LlogFormatException>(() => SegmentIndex.Decode(Encode(idx))); // index out of range

        idx.Blocks[pos] = good with { Summary = new BlockSummary(1u << 25, good.Summary!.EventIndexes, good.Summary.ServiceIndexes) };
        Assert.Throws<LlogFormatException>(() => SegmentIndex.Decode(Encode(idx))); // severity above 24

        // A hand-written section: valid with one entry per Records block, invalid with one more.
        idx.Blocks[pos] = good;
        var recordsBlocks = idx.Blocks.Count(b => b.Type == BlockType.Records);
        for (var i = 0; i < idx.Blocks.Count; i++) idx.Blocks[i] = idx.Blocks[i] with { Summary = null };
        var bare = Encode(idx); // no extension area
        byte[] WithSection(int n)
        {
            var section = new ByteWriter();
            section.WriteVarUInt((ulong)n);
            for (var i = 0; i < n; i++) { section.WriteVarUInt(1); section.WriteVarUInt(0); section.WriteVarUInt(0); } // TRACE, no events, no services
            var w = new ByteWriter();
            w.WriteBytes(bare);
            w.WriteVarUInt(SegmentIndex.ExtensionBlockSummaries);
            w.WriteLengthPrefixed(section.WrittenSpan);
            var bytes = w.WrittenSpan.ToArray();
            section.Release();
            w.Release();
            return bytes;
        }
        var decoded = SegmentIndex.Decode(WithSection(recordsBlocks));
        Assert.All(decoded.Blocks.Where(b => b.Type == BlockType.Records), b => Assert.Equal(1u, b.Summary!.SeverityMask));
        Assert.Throws<LlogFormatException>(() => SegmentIndex.Decode(WithSection(recordsBlocks + 1)));
        Assert.Throws<LlogFormatException>(() => SegmentIndex.Decode(WithSection(recordsBlocks - 1)));
    }

    [Fact]
    public void Footers_without_summaries_prune_blocks_by_time_only()
    {
        var (_, idx) = WriteSegment();
        for (var i = 0; i < idx.Blocks.Count; i++) idx.Blocks[i] = idx.Blocks[i] with { Summary = null };
        var decoded = SegmentIndex.Decode(Encode(idx)); // a 0.2-shaped footer
        Assert.All(decoded.Blocks, b => Assert.Null(b.Summary));
        var c = PruningConstraints.From(QueryParser.Parse("event = 'never.seen' AND level >= FATAL"));
        Assert.All(decoded.Blocks, b => Assert.Null(c.ExcludeBlock(b, decoded)));
    }

    [Fact]
    public void Exclude_block_reports_the_reason()
    {
        var (_, idx) = WriteSegment();
        var first = idx.Blocks.First(b => b.Type == BlockType.Records); // INFO http.request.completed from api
        Assert.Equal("severity", PruningConstraints.From(QueryParser.Parse("level >= ERROR")).ExcludeBlock(first, idx));
        Assert.Equal("event", PruningConstraints.From(QueryParser.Parse("event = 'cache.miss'")).ExcludeBlock(first, idx));
        Assert.Equal("service", PruningConstraints.From(QueryParser.Parse("service = 'worker'")).ExcludeBlock(first, idx));
        Assert.Null(PruningConstraints.From(QueryParser.Parse("event = 'http.request.completed' AND service = 'api'")).ExcludeBlock(first, idx));
        Assert.Null(PruningConstraints.From(QueryParser.Parse("level >= ERROR")).ExcludeBlock(first)); // no footer given
    }
}
