// LatticeLog reproducible lab (P02). Usage:
//   dotnet run -c Release --project research/LatticeLog.Lab -- baseline [--events 200000] [--reps 5] [--workloads a,b] [--out research/results]
//   dotnet run -c Release --project research/LatticeLog.Lab -- generate --workload http-api --events 100000 --out file.llog.jsonl
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using K4os.Compression.LZ4;
using LatticeLog;
using LatticeLog.Lab;
using LatticeLog.Query;
using LatticeLog.Storage;
using LatticeLog.Storage.Format;
using LatticeLog.Storage.Json;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
var argv = args.ToList();
string Opt(string name, string def) { var i = argv.IndexOf("--" + name); return i >= 0 && i + 1 < argv.Count ? argv[i + 1] : def; }
var cmd = argv.Count > 0 ? argv[0] : "baseline";

if (cmd == "generate")
{
    var ds = Workloads.Generate(Opt("workload", "http-api"), int.Parse(Opt("events", "100000")), int.Parse(Opt("seed", "1")));
    using var fs = File.Create(Opt("out", ds.Name + ".llog.jsonl"));
    using var w = new JsonlExchangeWriter(fs, null);
    foreach (var r in ds.Records) w.Write(r);
    Console.WriteLine($"{ds.Records.Count} records written; ground truth: {string.Join(",", ds.GroundTruth)}");
    return 0;
}

var events = int.Parse(Opt("events", "200000"));
var reps = int.Parse(Opt("reps", "5"));
var outDir = Opt("out", Path.Combine("research", "results"));
var workloads = Opt("workloads", string.Join(",", Workloads.Names)).Split(',', StringSplitOptions.RemoveEmptyEntries);
var work = Path.Combine(Path.GetTempPath(), "latticelog-lab-" + Environment.ProcessId);
Directory.CreateDirectory(work);
Directory.CreateDirectory(outDir);

var hw = Hardware.Describe();
Console.WriteLine(hw);
var rows = new List<Row>();
var queryRows = new List<QueryRow>();
var entropyRows = new List<EntropyRow>();

foreach (var wl in workloads)
{
    var ds = Workloads.Generate(wl, events, seed: 1);
    var records = ds.Records;
    for (var i = 0; i < records.Count; i++) records[i].Sequence = (ulong)i + 1;
    var jsonl = Encode.Jsonl(records);
    var reference = Hash(jsonl);
    Console.WriteLine($"\n== {wl}: {records.Count} records, canonical JSONL {jsonl.Length:N0} bytes, sha256 {reference[..16]}");

    void Measure(string name, string fidelity, Func<byte[]> enc, Func<byte[], byte[]?> dec, long? indexBytes = null)
    {
        var encTimes = new List<double>(); var decTimes = new List<double>();
        byte[] blob = [];
        string? check = null;
        for (var rep = 0; rep < reps + 1; rep++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers();
            var sw = Stopwatch.StartNew();
            blob = enc();
            var e = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            var back = dec(blob);
            var d = sw.Elapsed.TotalMilliseconds;
            if (rep == 0) { check = back is null ? "n/a" : Hash(back) == reference ? "ok" : "MISMATCH"; continue; } // warm-up
            encTimes.Add(e); decTimes.Add(d);
        }
        var row = new Row(wl, name, fidelity, records.Count, jsonl.Length, blob.Length, indexBytes, Stats.Median(encTimes), Stats.Min(encTimes), Stats.Max(encTimes), Stats.Median(decTimes), check!);
        rows.Add(row);
        Console.WriteLine($"  {name,-26} {blob.Length,12:N0} B  ratio {row.Ratio,6:0.00}x  enc {row.EncMs,8:0.0} ms  dec {row.DecMs,8:0.0} ms  roundtrip {check}");
    }

    Measure("text-lines (lossy)", "lossy", () => Encode.TextLines(records), _ => null);
    Measure("jsonl", "lossless", () => jsonl, b => b);
    Measure("jsonl+gzip-optimal", "lossless", () => Encode.Gzip(jsonl, CompressionLevel.Optimal), b => Encode.Gunzip(b));
    Measure("jsonl+zstd-3", "lossless", () => Encode.Zstd(jsonl, 3), b => Encode.Unzstd(b));
    Measure("jsonl+zstd-19", "lossless", () => Encode.Zstd(jsonl, 19), b => Encode.Unzstd(b));
    Measure("jsonl+lz4", "lossless", () => Encode.Lz4(jsonl), b => Encode.Unlz4(b));
    foreach (var codec in new[] { CodecId.Raw, CodecId.Structural, CodecId.Contextual })
        foreach (var comp in new[] { Compression.None, Compression.Deflate })
        {
            var name = $"llog-{codec.ToString().ToLowerInvariant()}{(comp == Compression.Deflate ? "+deflate" : "")}";
            long footer = 0;
            Measure(name, "lossless", () =>
            {
                var (bytes, f) = Encode.Llog(records, codec, comp, work);
                footer = f;
                return bytes;
            }, b => Encode.LlogToJsonl(b, work), footer == 0 ? null : footer);
            // re-run once to capture footer size (Measure closure captured before first encode)
            rows[^1] = rows[^1] with { IndexBytes = footer };
        }

    // ---- selective queries (P02-T14): indexed llog vs full scan llog vs JSONL+zstd scan
    var storeDir = Path.Combine(work, "store-" + wl);
    if (Directory.Exists(storeDir)) Directory.Delete(storeDir, true);
    using (var sw = StoreWriter.Open(storeDir, new StoreWriterOptions { SegmentMaxBytes = 2L * 1024 * 1024, Segment = new SegmentWriterOptions { Codec = CodecId.Structural, Compression = Compression.Deflate } }))
    {
        var copies = records.Select(r => r.ShallowClone()).ToList();
        for (var i = 0; i < copies.Count; i += 4096) sw.WriteBatch(copies.GetRange(i, Math.Min(4096, copies.Count - i)));
    }
    var zstdBlob = Encode.Zstd(jsonl, 3);
    var anyTrace = ds.GroundTruth.FirstOrDefault() ?? records.FirstOrDefault(r => !r.TraceId.IsEmpty)?.TraceId.ToString();
    var mid = records[records.Count / 2].TimestampUnixNanos;
    var span = (records.Max(r => r.TimestampUnixNanos) - records.Min(r => r.TimestampUnixNanos)) / 100;
    var firstEvent = records[0].EffectiveEventName ?? "";
    var queries = new List<(string Name, string Where)>
    {
        ("errors", "level >= ERROR"),
        ("event", $"event = '{firstEvent.Replace("'", "''")}'"),
        ("time-1pct", $"time >= {mid} AND time < {mid + span}"),
    };
    if (anyTrace is not null) queries.Insert(0, ("trace", $"trace_id = '{anyTrace}'"));
    var engine = QueryEngine.Open(storeDir);
    foreach (var (qn, where) in queries)
    {
        var match = Evaluator.Compile(QueryParser.Parse(where));
        double Time(Func<long> f, out long count)
        {
            var ts = new List<double>(); long c = 0;
            for (var i = 0; i < reps + 1; i++) { var s = Stopwatch.StartNew(); c = f(); if (i > 0) ts.Add(s.Elapsed.TotalMilliseconds); }
            count = c;
            return Stats.Median(ts);
        }
        QueryResult? last = null;
        var idxMs = Time(() => { last = engine.Execute(new QueryRequest { Where = where, Limit = QueryRequest.MaxLimit }); return last.Hits.Count; }, out var c1);
        var idxStats = last!.Stats;
        var scanMs = Time(() => engine.Execute(new QueryRequest { Where = where, Limit = QueryRequest.MaxLimit, UseIndexes = false }).Hits.Count, out var c2);
        var jsonMs = Time(() => Encode.ScanJsonlZstd(zstdBlob, match), out var c3);
        var consistent = c1 == c2 && (c1 == c3 || c1 == QueryRequest.MaxLimit);
        queryRows.Add(new QueryRow(wl, qn, where, c1, idxMs, idxStats.SegmentsScanned, idxStats.SegmentsTotal, idxStats.BlocksScanned, scanMs, jsonMs, consistent));
        Console.WriteLine($"  query {qn,-10} hits {c1,7}  indexed {idxMs,8:0.00} ms (segments {idxStats.SegmentsScanned}/{idxStats.SegmentsTotal}, blocks {idxStats.BlocksScanned})  llog-fullscan {scanMs,8:0.00} ms  jsonl+zstd-scan {jsonMs,8:0.00} ms  {(consistent ? "consistent" : "INCONSISTENT")}");
    }

    // ---- P09 pre-study: entropy upper bounds of what event-transition / timestamp prediction could save
    entropyRows.Add(Entropy.Analyze(wl, records, rows.Last(r => r.Workload == wl && r.Encoding == "llog-structural+deflate").Bytes));
    var e = entropyRows[^1];
    Console.WriteLine($"  P-study (adaptive bits/record): event o0={e.HEvent:0.000} o1={e.HEventGivenPrev:0.000}, dt={e.HDelta:0.000} ddt={e.HDeltaOfDelta:0.000} -> optimistic saving {e.UpperBoundBytes:N0} B = {e.UpperBoundPct:0.00}% of S+deflate");
}

var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
var csv = Path.Combine(outDir, $"baseline-{stamp}.csv");
File.WriteAllLines(csv, new[] { Row.Header }.Concat(rows.Select(r => r.Csv())));
File.WriteAllLines(Path.Combine(outDir, $"queries-{stamp}.csv"), new[] { QueryRow.Header }.Concat(queryRows.Select(q => q.Csv())));
var md = Report.Markdown(hw, events, reps, rows, queryRows, entropyRows);
File.WriteAllText(Path.Combine(outDir, $"baseline-{stamp}.md"), md);
Console.WriteLine($"\nResults: {csv}");
try { Directory.Delete(work, true); } catch (IOException) { }
return rows.Any(r => r.RoundTrip == "MISMATCH") || queryRows.Any(q => !q.Consistent) ? 1 : 0;

static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b));

namespace LatticeLog.Lab
{
    public sealed record Row(string Workload, string Encoding, string Fidelity, int Records, long JsonlBytes, long Bytes, long? IndexBytes, double EncMs, double EncMinMs, double EncMaxMs, double DecMs, string RoundTrip)
    {
        public double Ratio => (double)JsonlBytes / Bytes;
        public double BytesPerRecord => (double)Bytes / Records;
        public const string Header = "workload,encoding,fidelity,records,jsonl_bytes,total_stored_bytes,index_bytes,ratio_vs_jsonl,bytes_per_record,encode_ms_median,encode_ms_min,encode_ms_max,decode_ms_median,roundtrip";
        public string Csv() => string.Join(",", Workload, Encoding, Fidelity, Records, JsonlBytes, Bytes, IndexBytes?.ToString() ?? "", Ratio.ToString("0.000"), BytesPerRecord.ToString("0.00"), EncMs.ToString("0.00"), EncMinMs.ToString("0.00"), EncMaxMs.ToString("0.00"), DecMs.ToString("0.00"), RoundTrip);
    }

    public sealed record QueryRow(string Workload, string Query, string Where, long Hits, double IndexedMs, int SegmentsScanned, int SegmentsTotal, long BlocksScanned, double FullScanMs, double JsonlZstdScanMs, bool Consistent)
    {
        public const string Header = "workload,query,hits,indexed_ms,segments_scanned,segments_total,blocks_scanned,llog_fullscan_ms,jsonl_zstd_scan_ms,consistent";
        public string Csv() => string.Join(",", Workload, Query, Hits, IndexedMs.ToString("0.000"), SegmentsScanned, SegmentsTotal, BlocksScanned, FullScanMs.ToString("0.000"), JsonlZstdScanMs.ToString("0.000"), Consistent);
    }

    public sealed record EntropyRow(string Workload, double HEvent, double HEventGivenPrev, double HDelta, double HDeltaOfDelta, long UpperBoundBytes, double UpperBoundPct);

    public static class Stats
    {
        public static double Median(List<double> v) { if (v.Count == 0) return 0; var s = v.OrderBy(x => x).ToList(); return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2; }
        public static double Min(List<double> v) => v.Count == 0 ? 0 : v.Min();
        public static double Max(List<double> v) => v.Count == 0 ? 0 : v.Max();
    }

    public static class Hardware
    {
        public static string Describe()
            => $"CPU: {Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? RuntimeInformation.ProcessArchitecture.ToString()} | logical cores: {Environment.ProcessorCount} | " +
               $"RAM: {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024 * 1024.0):0.0} GiB | OS: {RuntimeInformation.OSDescription} | runtime: {RuntimeInformation.FrameworkDescription} | " +
               $"server GC: {System.Runtime.GCSettings.IsServerGC} | build: {(IsDebug() ? "DEBUG (numbers not valid)" : "Release")}";
        private static bool IsDebug()
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }
    }

    public static class Encode
    {
        public static byte[] Jsonl(List<LogRecord> records)
        {
            using var ms = new MemoryStream();
            using (var w = new JsonlExchangeWriter(ms, null, leaveOpen: true)) foreach (var r in records) w.Write(r);
            return ms.ToArray();
        }

        public static byte[] TextLines(List<LogRecord> records)
        {
            var sb = new StringBuilder();
            foreach (var r in records)
            {
                sb.Append(UnixTime.FormatRfc3339(r.TimestampUnixNanos)).Append(' ').Append(r.Severity.ToText()).Append(' ').Append(r.ServiceName).Append(' ').Append(r.EffectiveEventName).Append(' ');
                if (r.Event?.MessageTemplate is { } t) sb.Append(CanonicalJson.RenderTemplate(t, r.Attributes));
                else if (r.HasBody) sb.Append(r.Body.ToString());
                foreach (var a in r.Attributes) sb.Append(' ').Append(a.Key).Append('=').Append(a.Value.ToString());
                if (!r.TraceId.IsEmpty) sb.Append(" trace=").Append(r.TraceId);
                sb.Append('\n');
            }
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        public static byte[] Gzip(byte[] d, CompressionLevel l) { using var ms = new MemoryStream(); using (var g = new GZipStream(ms, l)) g.Write(d); return ms.ToArray(); }
        public static byte[] Gunzip(byte[] d) { using var ms = new MemoryStream(); using (var g = new GZipStream(new MemoryStream(d), CompressionMode.Decompress)) g.CopyTo(ms); return ms.ToArray(); }
        public static byte[] Zstd(byte[] d, int level) { using var c = new ZstdSharp.Compressor(level); return c.Wrap(d).ToArray(); }
        public static byte[] Unzstd(byte[] d) { using var c = new ZstdSharp.Decompressor(); return c.Unwrap(d).ToArray(); }
        public static byte[] Lz4(byte[] d) => LZ4Pickler.Pickle(d, LZ4Level.L00_FAST);
        public static byte[] Unlz4(byte[] d) => LZ4Pickler.Unpickle(d);

        /// <summary>Real segment file (header + schema/context blocks + records + footer index + trailer). Returns bytes and footer size.</summary>
        public static (byte[] Bytes, long FooterBytes) Llog(List<LogRecord> records, CodecId codec, Compression comp, string dir)
        {
            var path = Path.Combine(dir, $"bench-{Guid.NewGuid():N}.llog");
            SegmentIndex idx;
            long footerStart;
            using (var w = new SegmentWriter(path, 1, new SegmentWriterOptions { Codec = codec, Compression = comp, MaxRecordsPerBlock = 4096 }))
            {
                for (var i = 0; i < records.Count; i += 4096) w.Append(records, i, Math.Min(4096, records.Count - i));
                footerStart = w.Length;
                idx = w.Seal();
            }
            var bytes = File.ReadAllBytes(path);
            File.Delete(path);
            _ = idx;
            return (bytes, bytes.Length - footerStart);
        }

        public static byte[] LlogToJsonl(byte[] file, string dir)
        {
            var path = Path.Combine(dir, $"read-{Guid.NewGuid():N}.llog");
            File.WriteAllBytes(path, file);
            var list = new List<LogRecord>();
            using (var r = SegmentReader.Open(path))
            {
                var rep = new ScanReport();
                foreach (var b in r.ReadBlocks(rep)) list.AddRange(b.Records);
                if (!rep.IsClean) throw new InvalidDataException("integrity issue: " + rep.Issues[0].Message);
            }
            File.Delete(path);
            return Jsonl(list);
        }

        public static long ScanJsonlZstd(byte[] blob, Func<LogRecord, bool> match)
        {
            using var ds = new ZstdSharp.DecompressionStream(new MemoryStream(blob));
            using var tr = new StreamReader(ds, Encoding.UTF8);
            long n = 0;
            foreach (var r in new JsonlExchangeReader().Read(tr)) if (match(r)) n++;
            return n;
        }
    }

    /// <summary>
    /// P09 pre-study with adaptive (sequential) code lengths, which include the cost of learning the model:
    /// order-0 vs order-1 (previous event as context, escaping to order-0) for event identity, and order-0 coding of
    /// timestamp deltas vs delta-of-deltas (µs buckets). Novel symbols pay the same escape in both models.
    /// The difference is an optimistic bound of what Lattice-P could save over coding these fields independently;
    /// it is compared against the measured Lattice-S+deflate size (which already exploits part of this redundancy).
    /// </summary>
    public static class Entropy
    {
        private sealed class Adaptive<T> where T : notnull
        {
            private readonly Dictionary<T, long> _c = new();
            private long _n;
            /// <summary>Bits to code x, or null (escape) when unseen; updates the model.</summary>
            public double? Code(T x, out double escapeBits)
            {
                var distinct = _c.Count;
                _c.TryGetValue(x, out var cnt);
                double? bits = cnt > 0 ? -Math.Log2((double)cnt / (_n + distinct + 1)) : null;
                escapeBits = -Math.Log2((distinct + 1.0) / (_n + distinct + 1));
                _c[x] = cnt + 1; _n++;
                return bits;
            }
        }

        private const double LiteralBits = 32; // flat cost for a never-seen symbol, identical in both models

        private static double Order0<T>(IEnumerable<T> xs) where T : notnull
        {
            var m = new Adaptive<T>(); double total = 0;
            foreach (var x in xs) total += m.Code(x, out var esc) ?? esc + LiteralBits;
            return total;
        }

        private static double Order1(List<string> xs)
        {
            var o0 = new Adaptive<string>();
            var ctx = new Dictionary<string, Adaptive<string>>();
            double total = 0; var prev = "";
            foreach (var x in xs)
            {
                if (!ctx.TryGetValue(prev, out var m)) ctx[prev] = m = new Adaptive<string>();
                var b1 = m.Code(x, out var esc1);
                var b0 = o0.Code(x, out var esc0);
                total += b1 ?? esc1 + (b0 ?? esc0 + LiteralBits);
                prev = x;
            }
            return total;
        }

        public static EntropyRow Analyze(string wl, List<LogRecord> recs, long structuralDeflateBytes)
        {
            var n = Math.Max(1, recs.Count);
            var ev = recs.Select(r => r.EffectiveEventName ?? "").ToList();
            var l0 = Order0(ev); var l1 = Order1(ev);
            var d = recs.Skip(1).Zip(recs, (c, p) => (c.TimestampUnixNanos - p.TimestampUnixNanos) / 1000).ToList();
            var dd = d.Skip(1).Zip(d, (c, p) => c - p).ToList();
            var ld = Order0(d); var ldd = Order0(dd);
            var bits = Math.Max(0, l0 - l1) + Math.Max(0, ld - ldd);
            var bytes = (long)(bits / 8);
            return new EntropyRow(wl, l0 / n, l1 / n, ld / n, ldd / n, bytes, 100.0 * bytes / structuralDeflateBytes);
        }
    }

    public static class Report
    {
        public static string Markdown(string hw, int events, int reps, List<Row> rows, List<QueryRow> q, List<EntropyRow> e)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# LatticeLog baseline results (generated)");
            sb.AppendLine();
            sb.AppendLine($"- Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC by `research/LatticeLog.Lab` (`baseline --events {events} --reps {reps}`)");
            sb.AppendLine($"- Hardware/runtime: {hw}");
            sb.AppendLine("- Sizes are total stored bytes (llog: whole file incl. header, schema/context blocks, footer index and trailer). Times are medians of the repetitions after one warm-up, single thread, warm page cache.");
            sb.AppendLine("- `roundtrip ok` = decoded data re-serialized to canonical JSONL has the same SHA-256 as the input.");
            sb.AppendLine();
            foreach (var g in rows.GroupBy(r => r.Workload))
            {
                sb.AppendLine($"## {g.Key}");
                sb.AppendLine();
                sb.AppendLine("| encoding | fidelity | bytes | B/record | ratio vs JSONL | index B | enc ms | dec ms | round trip |");
                sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---|");
                foreach (var r in g) sb.AppendLine($"| {r.Encoding} | {r.Fidelity} | {r.Bytes:N0} | {r.BytesPerRecord:0.0} | {r.Ratio:0.00}x | {(r.IndexBytes is { } ib ? ib.ToString("N0") : "-")} | {r.EncMs:0.0} | {r.DecMs:0.0} | {r.RoundTrip} |");
                sb.AppendLine();
                var qs = q.Where(x => x.Workload == g.Key).ToList();
                if (qs.Count > 0)
                {
                    sb.AppendLine("| query | hits | llog indexed ms | segments read | llog full scan ms | JSONL+zstd scan ms | consistent |");
                    sb.AppendLine("|---|---:|---:|---:|---:|---:|---|");
                    foreach (var x in qs) sb.AppendLine($"| {x.Query} | {x.Hits} | {x.IndexedMs:0.00} | {x.SegmentsScanned}/{x.SegmentsTotal} | {x.FullScanMs:0.00} | {x.JsonlZstdScanMs:0.00} | {x.Consistent} |");
                    sb.AppendLine();
                }
            }
            sb.AppendLine("## P09 pre-study: entropy upper bound for predictive coding");
            sb.AppendLine();
            sb.AppendLine("| workload | event bits/rec order-0 | event bits/rec order-1 | dt bits/rec | ddt bits/rec | optimistic saving | % of S+deflate |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
            foreach (var x in e) sb.AppendLine($"| {x.Workload} | {x.HEvent:0.000} | {x.HEventGivenPrev:0.000} | {x.HDelta:0.000} | {x.HDeltaOfDelta:0.000} | {x.UpperBoundBytes:N0} B | {x.UpperBoundPct:0.00}% |");
            return sb.ToString();
        }
    }
}
