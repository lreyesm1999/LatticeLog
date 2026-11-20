using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LatticeLog;
using LatticeLog.Evidence;
using LatticeLog.OpenTelemetry;
using LatticeLog.Query;
using LatticeLog.Storage;
using LatticeLog.Storage.Format;
using LatticeLog.Storage.Json;

namespace LatticeLog.Cli;

/// <summary>Exit codes are part of the CLI contract (P15-T03).</summary>
public static class ExitCodes
{
    public const int Ok = 0;
    public const int Usage = 1;
    public const int NotFound = 2;
    public const int IntegrityIssues = 3;
    public const int PartialResults = 4;
    public const int DataError = 5;
    public const int Locked = 6;
    public const int Internal = 10;
}

public sealed class CliException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

public sealed class Args
{
    private readonly Dictionary<string, string?> _opts = new(StringComparer.Ordinal);
    public List<string> Positional { get; } = [];

    public Args(IEnumerable<string> argv)
    {
        var list = argv.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                var eq = a.IndexOf('=');
                if (eq > 0) { _opts[a[2..eq]] = a[(eq + 1)..]; continue; }
                var name = a[2..];
                if (i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal) && !Flags.Contains(name)) _opts[name] = list[++i];
                else _opts[name] = null;
            }
            else Positional.Add(a);
        }
    }

    private static readonly HashSet<string> Flags = ["follow", "no-index", "force", "stdout", "pretty", "help", "preserve-raw", "no-redact", "desc", "explain", "preview", "yes", "dry-run", "no-refresh",
        "no-service", "no-start", "no-project", "no-path", "open", "verbose", "uninstall", "remove-data", "clear"];

    public bool Has(string name) => _opts.ContainsKey(name);
    public string? Get(string name) => _opts.TryGetValue(name, out var v) ? v : null;
    public string Require(string name) => Get(name) ?? throw new CliException(ExitCodes.Usage, $"Missing --{name}.");
    public int Int(string name, int def)
    {
        var v = Get(name);
        if (v is null) return def;
        return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : throw new CliException(ExitCodes.Usage, $"--{name} must be an integer.");
    }
    public string Format => (Get("format") ?? "text").ToLowerInvariant();
    public IEnumerable<string> Keys => _opts.Keys;
}

public static class Program
{
    public const string Version = "0.1.0-alpha.1";

    private static readonly Dictionary<string, string> Help = new()
    {
        ["init"] = "init <dir>                         Create an empty store.",
        ["inspect"] = "inspect <dir|file.llog>            Show segments, codecs, sizes and index summaries.",
        ["query"] = "query <dir> [--where Q] [--since 1h|RFC3339] [--until ..] [--limit N] [--cursor C] [--desc] [--explain] [--format text|json|jsonl]",
        ["trace"] = "trace <dir> --trace-id HEX [--budget-bytes N] [--format json|markdown]",
        ["context"] = "context <dir> (--trace-id HEX | --where Q) [--since ..] [--budget-bytes N] [--format json|markdown]",
        ["event"] = "event <dir> <llog://store/e/SEQ | SEQ> [--format json|markdown]",
        ["related"] = "related <dir> <ref|SEQ> [--limit N]",
        ["errors"] = "errors <dir> [--where Q] [--since ..] [--top N] [--format json|markdown]",
        ["overview"] = "overview <dir> [--since ..] [--format json|markdown]",
        ["schema"] = "schema <dir> [--format json|markdown]",
        ["stats"] = "stats <dir> --by event|level|service|<attr> [--where Q] [--bucket 1m] [--top N]",
        ["verify"] = "verify <dir|file.llog> [--format json]      Exit 3 when integrity issues are found.",
        ["repair"] = "repair <dir>                       Recover unsealed/torn segments (copies torn tails to recovered/).",
        ["compact"] = "compact <dir> [--policy fast|balanced|compact] [--target-mb N]",
        ["backup"] = "backup <dir> <destination>         Hot backup (safe while a writer runs), verified by reading it back.",
        ["retention"] = "retention <dir> (--max-age 30d | --max-mb N) [--dry-run] [--format json]   Delete whole old sealed segments (journaled).",
        ["export"] = "export <dir> --format llog-jsonl|otlp-json|raw (--output FILE | --stdout) [--where Q]",
        ["import"] = "import <dir> <file> --format llog-jsonl|otlp-json|jsonl|text [--service NAME] [--preserve-raw]",
        ["tail"] = "tail <dir> [--lines N] [--where Q] [--follow]",
        ["roots"] = "roots add|remove|list [<folder>]   Per-user folders searched for sources (service folders, .NET stores).",
        ["allow"] = "allow add|remove|list [<folder>]   Extra folders whose log files may be previewed/migrated.",
        ["sources"] = "sources [--project DIR] [--no-refresh] [--format json]   List sources by name (indexes Python folders first).",
        ["migrate"] = "migrate <path|glob> --name <source> [--follow] | --preview   Convert traditional logs into a source; originals untouched.",
        ["sources retention"] = "sources retention [<name>|*] [--max-age 30d] [--max-mb N] [--clear]   Retention the service applies to indexes and migrations.",
        ["mcp"] = "mcp [<dir>] [--project DIR] [--no-service]   MCP over stdio via the background service (auto-started; in-process if it cannot start).",
        ["mcp install"] = "mcp install --client claude-desktop|claude-code|cursor|vscode [--scope user|project] [--yes] [--dry-run]",
        ["mcp uninstall"] = "mcp uninstall --client claude-desktop|claude-code|cursor|vscode [--scope user|project]",
        ["service"] = "service status|start|stop|restart|install|uninstall|ui [--open]|run [--port N] [--verbose]   Per-user background service on 127.0.0.1.",
        ["setup"] = "setup [--clients a,b|none] [--install-dir DIR] [--no-service] [--no-path] [--method task|run-key] | setup --uninstall [--remove-data]",
    };

    public static int Main(string[] argv)
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); }
        catch (IOException) { } // no console (the service started as latticelogw.exe)
        if (argv.Length == 0 || argv[0] is "-h" or "--help" or "help") { PrintHelp(); return argv.Length == 0 ? ExitCodes.Usage : ExitCodes.Ok; }
        if (argv[0] is "--version" or "version") { Console.WriteLine("latticelog " + Version); return ExitCodes.Ok; }
        var cmd = argv[0];
        var args = new Args(argv.Skip(1));
        try
        {
            if (args.Has("help")) { Console.WriteLine("latticelog " + (Help.TryGetValue(cmd, out var h) ? h : cmd)); return ExitCodes.Ok; }
            return cmd switch
            {
                "init" => Init(args),
                "inspect" => Inspect(args),
                "query" => QueryCmd(args),
                "trace" => TraceCmd(args),
                "context" => ContextCmd(args),
                "event" => EventCmd(args),
                "related" => RelatedCmd(args),
                "errors" => Emit(args, Evidence(args).ErrorGroups(Scope(args), args.Int("top", 20))),
                "overview" => Emit(args, Evidence(args).Overview(Scope(args), args.Int("top", 10))),
                "schema" => Emit(args, Evidence(args).Schema()),
                "stats" => StatsCmd(args),
                "verify" => Verify(args),
                "repair" => Repair(args),
                "compact" => Compact(args),
                "retention" => RetentionCmd(args),
                "backup" => BackupCmd(args),
                "export" => Export(args),
                "import" => Import(args),
                "tail" => Tail(args),
                "roots" => SourceCommands.FolderList(args, "roots"),
                "allow" => SourceCommands.FolderList(args, "allow"),
                "sources" => SourceCommands.Sources(args),
                "migrate" => SourceCommands.Migrate(args),
                "mcp" => SourceCommands.Mcp(args),
                "service" => ServiceCommands.Service(args),
                "setup" => ServiceCommands.Setup(args),
                _ => throw new CliException(ExitCodes.Usage, $"Unknown command '{cmd}'. Run 'latticelog help'."),
            };
        }
        catch (CliException ex) { return Fail(args, ex.Code, ex.Message); }
        catch (QuerySyntaxException ex) { return Fail(args, ExitCodes.Usage, "Query error: " + ex.Message); }
        catch (StoreLockedException ex) { return Fail(args, ExitCodes.Locked, ex.Message); }
        catch (DirectoryNotFoundException ex) { return Fail(args, ExitCodes.NotFound, ex.Message); }
        catch (FileNotFoundException ex) { return Fail(args, ExitCodes.NotFound, ex.Message); }
        catch (Exception ex) when (ex is LlogFormatException or FormatException or InvalidDataException) { return Fail(args, ExitCodes.DataError, ex.Message); }
        catch (ArgumentException ex) { return Fail(args, ExitCodes.Usage, ex.Message); }
        catch (Exception ex) { return Fail(args, ExitCodes.Internal, $"{ex.GetType().Name}: {ex.Message}"); }
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"latticelog {Version} — local log store with typed events, selective queries and verifiable evidence (no AI inside).");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        foreach (var h in Help.Values) Console.WriteLine("  " + h);
        Console.WriteLine();
        Console.WriteLine("Exit codes: 0 ok, 1 usage, 2 not found, 3 integrity issues, 4 partial results, 5 data error, 6 store locked, 10 internal.");
    }

    private static int Fail(Args args, int code, string message)
    {
        if (args.Format is "json" or "jsonl")
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, CanonicalJson.WriterOptions))
            {
                w.WriteStartObject();
                w.WriteStartObject("error");
                w.WriteNumber("code", code);
                w.WriteString("message", message);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            Console.Error.WriteLine(Encoding.UTF8.GetString(ms.ToArray()));
        }
        else Console.Error.WriteLine("error: " + message);
        return code;
    }

    private static string Store(Args a) => a.Positional.Count > 0 ? a.Positional[0] : throw new CliException(ExitCodes.Usage, "Missing store directory.");

    private static QueryEngine Engine(Args a) => QueryEngine.Open(Store(a));

    private static EvidenceService Evidence(Args a) => new(Engine(a), new EvidenceOptions { Redaction = a.Has("no-redact") ? RedactionPolicy.None : RedactionPolicy.Default });

    public static long? ParseTimeArg(string? s, long now)
    {
        if (s is null) return null;
        if (UnixTime.TryParseRfc3339(s, out var t)) return t;
        if (s.Length >= 2 && double.TryParse(s[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
        {
            var unit = s[^1] switch { 's' => 1e9, 'm' => 60e9, 'h' => 3600e9, 'd' => 86400e9, _ => -1 };
            if (unit > 0) return now - (long)(n * unit);
        }
        throw new CliException(ExitCodes.Usage, $"Invalid time '{s}': use RFC 3339 or a relative duration like 15m, 2h, 7d.");
    }

    private static QueryRequest Scope(Args a)
    {
        var now = UnixTime.NowUnixNanos();
        return new QueryRequest
        {
            Where = a.Get("where"),
            SinceUnixNanos = ParseTimeArg(a.Get("since"), now),
            UntilUnixNanos = ParseTimeArg(a.Get("until"), now),
            Limit = Math.Clamp(a.Int("limit", 100), 1, QueryRequest.MaxLimit),
            Cursor = a.Get("cursor"),
            Order = a.Has("desc") ? SortOrder.Descending : SortOrder.Ascending,
            UseIndexes = !a.Has("no-index"),
        };
    }

    // ------------------------------------------------------------------ commands

    // JsonNode output only: no reflection-based serialization, so the tool stays trimming/AOT-safe.
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static int Init(Args a)
    {
        var dir = Store(a);
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, StoreLayout.SegmentsDir));
        var info = StoreWriter.EnsureStoreFile(dir);
        Console.WriteLine(a.Format == "json" ? new JsonObject { ["store_id"] = info.StoreId, ["path"] = Path.GetFullPath(dir) }.ToJsonString() : $"Initialized store {info.StoreId} at {Path.GetFullPath(dir)}");
        return ExitCodes.Ok;
    }

    private static int Inspect(Args a)
    {
        var target = Store(a);
        var files = File.Exists(target) ? [target] : StoreReader.Open(target).SegmentIds.Select(id => StoreLayout.SegmentPath(target, id)).ToList();
        var rows = new List<JsonObject>();
        foreach (var f in files)
        {
            using var r = SegmentReader.Open(f);
            var row = new JsonObject
            {
                ["file"] = Path.GetFileName(f),
                ["segment_id"] = r.Header.SegmentId,
                ["format"] = $"{r.Header.Major}.{r.Header.Minor}",
                ["bytes"] = r.Length,
                ["sealed"] = r.IsSealed,
            };
            if (r.Index is { } x)
            {
                row["records"] = x.RecordCount;
                row["first_seq"] = x.FirstSequence;
                row["last_seq"] = x.LastSequence;
                row["min_time"] = x.HasRecords ? UnixTime.FormatRfc3339(x.MinTimestamp) : null;
                row["max_time"] = x.HasRecords ? UnixTime.FormatRfc3339(x.MaxTimestamp) : null;
                row["blocks"] = x.Blocks.Count;
                row["events"] = x.Events.Count;
                row["services"] = new JsonArray(x.Services.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
                row["bytes_per_record"] = x.HasRecords ? Math.Round((double)r.Length / x.RecordCount, 2) : null;
            }
            rows.Add(row);
        }
        if (a.Format == "json") Console.WriteLine(new JsonArray(rows.Select(r => (JsonNode?)r).ToArray()).ToJsonString(Indented));
        else
        {
            foreach (var row in rows)
                Console.WriteLine(string.Join("  ", row.Where(kv => kv.Value is not null && kv.Key != "services").Select(kv => $"{kv.Key}={kv.Value}")));
            Console.WriteLine($"{rows.Count} segment(s), {rows.Sum(r => (long)r["bytes"]!)} bytes, {rows.Sum(r => r.TryGetPropertyValue("records", out var n) && n is not null ? (long)n : 0)} records in sealed segments");
        }
        return ExitCodes.Ok;
    }

    private static int QueryCmd(Args a)
    {
        var engine = Engine(a);
        var req = Scope(a);
        var res = engine.Execute(req);
        var fo = new FriendlyOptions { Redaction = a.Has("no-redact") ? RedactionPolicy.None : RedactionPolicy.Default };
        switch (a.Format)
        {
            case "jsonl":
                foreach (var h in res.Hits) Console.WriteLine(CanonicalJson.ToFriendlyJson(h.Record, res.StoreId, fo));
                if (res.NextCursor is not null || res.Partial)
                    Console.Error.WriteLine(new JsonObject { ["next_cursor"] = res.NextCursor, ["truncated"] = res.Truncated, ["partial"] = res.Partial, ["partial_reason"] = res.PartialReason }.ToJsonString());
                break;
            case "json":
                {
                    using var ms = new MemoryStream();
                    using (var w = new Utf8JsonWriter(ms, CanonicalJson.WriterOptions with { Indented = a.Has("pretty") }))
                    {
                        w.WriteStartObject();
                        w.WriteString("format", "latticelog.query.v1");
                        w.WriteString("store_id", res.StoreId);
                        w.WriteString("filter", res.Query);
                        w.WriteStartObject("coverage");
                        w.WriteNumber("returned", res.Hits.Count);
                        w.WriteBoolean("truncated", res.Truncated);
                        w.WriteBoolean("partial_results", res.Partial);
                        if (res.PartialReason is not null) w.WriteString("partial_reason", res.PartialReason);
                        w.WriteBoolean("includes_unsealed_segments", res.IncludesUnsealedSegments);
                        if (res.NextCursor is not null) w.WriteString("next_cursor", res.NextCursor);
                        w.WriteEndObject();
                        w.WriteStartObject("stats");
                        w.WriteNumber("segments_total", res.Stats.SegmentsTotal);
                        w.WriteNumber("segments_scanned", res.Stats.SegmentsScanned);
                        w.WriteNumber("blocks_scanned", res.Stats.BlocksScanned);
                        w.WriteNumber("blocks_pruned", res.Stats.BlocksPruned);
                        w.WriteNumber("records_decoded", res.Stats.RecordsDecoded);
                        w.WriteNumber("elapsed_ms", Math.Round(res.Stats.ElapsedMs, 3));
                        w.WriteEndObject();
                        if (a.Has("explain")) w.WriteString("plan", res.Plan);
                        w.WriteStartArray("events");
                        foreach (var h in res.Hits) CanonicalJson.WriteFriendlyRecord(w, h.Record, res.StoreId, fo);
                        w.WriteEndArray();
                        w.WriteEndObject();
                    }
                    Console.WriteLine(Encoding.UTF8.GetString(ms.ToArray()));
                    break;
                }
            default:
                foreach (var h in res.Hits) Console.WriteLine(TextLine(h.Record, fo));
                Console.Error.WriteLine($"-- {res.Hits.Count} event(s); scanned {res.Stats.SegmentsScanned}/{res.Stats.SegmentsTotal} segments, {res.Stats.RecordsDecoded} records decoded in {res.Stats.ElapsedMs:0.#} ms"
                    + (res.Truncated ? $"; more results: --cursor {res.NextCursor}" : "") + (res.Partial ? $"; PARTIAL: {res.PartialReason}" : ""));
                if (a.Has("explain")) Console.Error.Write(res.Plan);
                break;
        }
        return res.Partial ? ExitCodes.PartialResults : ExitCodes.Ok;
    }

    private static string TextLine(LogRecord r, FriendlyOptions fo)
    {
        var sb = new StringBuilder();
        sb.Append(UnixTime.FormatRfc3339(r.TimestampUnixNanos)).Append(' ').Append(r.Severity.ToText().PadRight(5)).Append(' ');
        if (r.ServiceName is { } s) sb.Append('[').Append(s).Append("] ");
        if (r.EffectiveEventName is { } e) sb.Append(e).Append(": ");
        if (r.Event?.MessageTemplate is { } t) sb.Append(CanonicalJson.RenderTemplate(t, r.Attributes, fo));
        else if (r.HasBody) sb.Append(r.Body.Kind == ValueKind.Bytes ? Encoding.UTF8.GetString(r.Body.AsBytes()) : fo.Redact(r, "body", r.Body).ToString());
        foreach (var a in r.Attributes) sb.Append(' ').Append(a.Key).Append('=').Append(fo.Redact(r, a.Key, a.Value));
        if (!r.TraceId.IsEmpty) sb.Append(" trace=").Append(r.TraceId);
        if (r.Exception is { } ex) sb.Append(" exception=").Append(ex.Type).Append(": ").Append(ex.Message);
        sb.Append(" #").Append(r.Sequence);
        return sb.ToString().Replace('\n', ' ').Replace('\r', ' ');
    }

    private static int Emit(Args a, EvidenceDocument doc)
    {
        Console.WriteLine(a.Format == "markdown" ? EvidenceService.ToMarkdown(doc) : doc.Json);
        return doc.Partial ? ExitCodes.PartialResults : ExitCodes.Ok;
    }

    private static int TraceCmd(Args a)
    {
        var id = a.Require("trace-id");
        if (!TraceId.TryParse(id.ToLowerInvariant(), out var t)) throw new CliException(ExitCodes.Usage, "--trace-id must be 32 hex characters.");
        return Emit(a, Evidence(a).GetTrace(t, a.Int("budget-bytes", 64_000)));
    }

    private static int ContextCmd(Args a)
    {
        var scope = Scope(a);
        if (a.Get("trace-id") is { } tid)
        {
            if (!TraceId.TryParse(tid.ToLowerInvariant(), out var t)) throw new CliException(ExitCodes.Usage, "--trace-id must be 32 hex characters.");
            scope = scope with { Where = string.IsNullOrWhiteSpace(scope.Where) ? $"trace_id = '{t}'" : $"({scope.Where}) AND trace_id = '{t}'" };
        }
        return Emit(a, Evidence(a).ContextPack(scope, a.Int("budget-bytes", a.Int("max-bytes", 32_000))));
    }

    private static ulong ParseRef(string s)
    {
        if (CanonicalJson.TryParseReference(s, out _, out var seq)) return seq;
        if (ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out seq) && seq > 0) return seq;
        throw new CliException(ExitCodes.Usage, $"'{s}' is not an event reference (llog://<store>/e/<seq>) or sequence number.");
    }

    private static int EventCmd(Args a)
    {
        if (a.Positional.Count < 2) throw new CliException(ExitCodes.Usage, "Usage: " + Help["event"]);
        var ev = Evidence(a);
        if (CanonicalJson.TryParseReference(a.Positional[1], out var sid, out _) && sid != ev.Engine.Store.Info.StoreId)
            throw new CliException(ExitCodes.NotFound, $"Reference belongs to store {sid}, not {ev.Engine.Store.Info.StoreId}.");
        var doc = ev.GetEvent(ParseRef(a.Positional[1]));
        Emit(a, doc);
        return doc.ReturnedEvents == 0 ? ExitCodes.NotFound : ExitCodes.Ok;
    }

    private static int RelatedCmd(Args a)
    {
        if (a.Positional.Count < 2) throw new CliException(ExitCodes.Usage, "Usage: " + Help["related"]);
        return Emit(a, Evidence(a).GetRelated(ParseRef(a.Positional[1]), a.Int("limit", 200)));
    }

    private static int StatsCmd(Args a)
    {
        TimeSpan? bucket = null;
        if (a.Get("bucket") is { } b)
        {
            var now = UnixTime.NowUnixNanos();
            bucket = TimeSpan.FromTicks((now - ParseTimeArg(b, now)!.Value) / 100);
        }
        var by = a.Get("by") ?? (bucket is null ? "event" : "time");
        var res = Engine(a).Aggregate(Scope(a), by, bucket, a.Int("top", 50));
        if (a.Format == "json")
            Console.WriteLine(new JsonObject
            {
                ["group_by"] = res.GroupBy,
                ["total"] = res.Total,
                ["from_index_only"] = res.UsedIndexOnly,
                ["partial"] = res.Partial,
                ["groups"] = new JsonArray(res.Groups.Select(g => (JsonNode?)new JsonObject { ["key"] = g.Key, ["count"] = g.Count }).ToArray()),
            }.ToJsonString());
        else
        {
            var width = res.Groups.Count == 0 ? 0 : res.Groups.Max(g => g.Key.Length);
            foreach (var (k, c) in res.Groups) Console.WriteLine($"{k.PadRight(width)}  {c}");
            Console.Error.WriteLine($"-- total {res.Total}{(res.UsedIndexOnly ? " (from footer indexes, no block decoded)" : "")}");
        }
        return res.Partial ? ExitCodes.PartialResults : ExitCodes.Ok;
    }

    private static int Verify(Args a)
    {
        var target = Store(a);
        var sw = Stopwatch.StartNew();
        var report = new ScanReport();
        long records = 0, segments = 0, footerMismatch = 0;
        var manifestProblems = new List<string>();
        IEnumerable<string> files;
        if (File.Exists(target)) files = [target];
        else
        {
            var store = StoreReader.Open(target);
            manifestProblems.AddRange(store.ManifestProblems);
            files = store.SegmentIds.Select(store.SegmentPath);
        }
        foreach (var f in files)
        {
            SegmentReader r;
            try { r = SegmentReader.Open(f); }
            catch (LlogFormatException ex) { report.Add(new ScanIssue(IssueKind.UnsupportedFile, f, 0, 0, ex.Message)); continue; }
            using (r)
            {
                segments++;
                if (!r.IsSealed) report.Add(new ScanIssue(IssueKind.MissingFooter, f, r.Length, 0, r.FooterError ?? "Segment is not sealed (active, or needs 'repair')."));
                var n = r.ReadBlocks(report).Sum(b => (long)b.Records.Count);
                records += n;
                if (r.Index is { } x && x.RecordCount != n)
                {
                    footerMismatch++;
                    report.Add(new ScanIssue(IssueKind.FooterMismatch, f, r.FooterOffset, 0, $"Footer says {x.RecordCount} records, decoded {n}."));
                }
            }
        }
        var ok = report.IsClean && manifestProblems.Count == 0;
        if (a.Format == "json")
            Console.WriteLine(new JsonObject
            {
                ["ok"] = ok,
                ["segments"] = segments,
                ["records"] = records,
                ["bytes_read"] = report.BytesRead,
                ["elapsed_ms"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1),
                ["issues"] = new JsonArray(report.Issues.Select(i => (JsonNode?)new JsonObject
                {
                    ["kind"] = i.Kind.ToString(),
                    ["file"] = Path.GetFileName(i.File),
                    ["offset"] = i.Offset,
                    ["length"] = i.Length,
                    ["message"] = i.Message,
                    ["lost_records"] = i.LostRecords,
                }).ToArray()),
                ["manifest_problems"] = new JsonArray(manifestProblems.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
            }.ToJsonString(Indented));
        else
        {
            foreach (var i in report.Issues) Console.WriteLine($"{i.Kind} {Path.GetFileName(i.File)} @{i.Offset} len={i.Length}{(i.LostRecords is { } l ? $" lost_records={l}" : "")}: {i.Message}");
            foreach (var p in manifestProblems) Console.WriteLine("MANIFEST " + p);
            Console.WriteLine($"{(ok ? "OK" : "ISSUES FOUND")}: {segments} segment(s), {records} record(s) verified in {sw.Elapsed.TotalMilliseconds:0} ms");
        }
        return ok ? ExitCodes.Ok : ExitCodes.IntegrityIssues;
    }

    private static int Repair(Args a)
    {
        using var w = StoreWriter.Open(Store(a));
        foreach (var r in w.Recovery)
            Console.WriteLine($"segment {r.SegmentId}: truncated {r.TruncatedBytes} byte(s){(r.TailCopy is null ? "" : $", tail copied to {r.TailCopy}")}, {r.Issues.Count} issue(s)");
        Console.WriteLine(w.Recovery.Count == 0 ? "Nothing to repair." : $"Recovered {w.Recovery.Count} segment(s).");
        return ExitCodes.Ok;
    }

    private static int Compact(Args a)
    {
        var policy = (a.Get("policy") ?? "balanced").ToLowerInvariant() switch
        {
            "fast" => CompactionPolicy.Fast,
            "balanced" => CompactionPolicy.Balanced,
            "compact" => CompactionPolicy.Compact,
            var p => throw new CliException(ExitCodes.Usage, $"Unknown policy '{p}'."),
        };
        var root = Store(a);
        using var w = StoreWriter.Open(root); // holds the lock: no concurrent writer
        var results = Compactor.CompactAll(root, policy, w.AllocateSegmentId, a.Int("target-mb", 256) * 1024L * 1024);
        foreach (var r in results)
            Console.WriteLine($"[{string.Join(",", r.Inputs)}] -> {r.Output}: {r.Records} records, {r.InputBytes} -> {r.OutputBytes} bytes ({(r.InputBytes == 0 ? 0 : 100.0 * r.OutputBytes / r.InputBytes):0.#}%), codec {r.Choice.Codec}+{r.Choice.Compression}, {r.Elapsed.TotalMilliseconds:0} ms");
        Console.WriteLine($"{results.Count} compaction(s) committed.");
        return ExitCodes.Ok;
    }

    private static int BackupCmd(Args a)
    {
        var dest = a.Positional.ElementAtOrDefault(1) ?? throw new CliException(ExitCodes.Usage, "backup requires a destination directory.");
        var r = StoreBackup.Create(Store(a), dest);
        Console.WriteLine($"{r.Segments} segment(s), {r.Records} record(s), {r.Bytes} bytes copied to {Path.GetFullPath(r.Destination)}; verified: {(r.Verified ? "OK" : "ISSUES")}");
        foreach (var i in r.Issues) Console.WriteLine("  " + i);
        return r.Verified ? ExitCodes.Ok : ExitCodes.IntegrityIssues;
    }

    private static int RetentionCmd(Args a)
    {
        TimeSpan? maxAge = null;
        if (a.Get("max-age") is { } age)
        {
            var now = UnixTime.NowUnixNanos();
            maxAge = TimeSpan.FromTicks((now - ParseTimeArg(age, now)!.Value) / 100);
            if (maxAge <= TimeSpan.Zero) throw new CliException(ExitCodes.Usage, "--max-age must be a positive duration like 30d.");
        }
        long? maxBytes = a.Get("max-mb") is null ? null : a.Int("max-mb", 0) * 1024L * 1024;
        var policy = new RetentionPolicy { MaxAge = maxAge, MaxBytes = maxBytes };
        if (policy.IsEmpty) throw new CliException(ExitCodes.Usage, "retention requires --max-age and/or --max-mb.");
        var root = Store(a);
        using var w = StoreWriter.Open(root); // holds the lock: no concurrent writer
        var result = w.ApplyRetention(policy, dryRun: a.Has("dry-run"));
        if (a.Format == "json")
            Console.WriteLine(new JsonObject
            {
                ["dry_run"] = result.DryRun,
                ["segments"] = result.Deleted.Count,
                ["records"] = result.Records,
                ["bytes"] = result.Bytes,
                ["deleted"] = new JsonArray(result.Deleted.Select(d => (JsonNode?)new JsonObject
                {
                    ["segment_id"] = d.SegmentId,
                    ["reason"] = d.Reason,
                    ["records"] = d.Records,
                    ["bytes"] = d.Bytes,
                    ["first_seq"] = d.FirstSequence,
                    ["last_seq"] = d.LastSequence,
                    ["max_time"] = UnixTime.FormatRfc3339(d.MaxTimestampUnixNanos),
                }).ToArray()),
            }.ToJsonString(Indented));
        else
        {
            foreach (var d in result.Deleted)
                Console.WriteLine($"{(result.DryRun ? "would delete" : "deleted")} segment {d.SegmentId}: {d.Records} records (seq {d.FirstSequence}-{d.LastSequence}, newest {UnixTime.FormatRfc3339(d.MaxTimestampUnixNanos)}), {d.Bytes} bytes — {d.Reason}");
            Console.WriteLine($"{result.Deleted.Count} segment(s), {result.Records} record(s), {result.Bytes} bytes {(result.DryRun ? "would be deleted (dry run)" : "deleted")}.");
        }
        return ExitCodes.Ok;
    }

    private static int Export(Args a)
    {
        var fmt = a.Get("format") ?? throw new CliException(ExitCodes.Usage, "export requires --format llog-jsonl|otlp-json|raw.");
        var output = a.Get("output");
        if (output is null && !a.Has("stdout")) throw new CliException(ExitCodes.Usage, "export requires --output FILE (or --stdout to write to standard output).");
        var engine = Engine(a);
        var report = new ScanReport();
        var match = Evaluator.Compile(engine.BuildExpression(Scope(a) with { Cursor = null }));
        var records = engine.Store.ReadAll(report).Where(match);
        using var stream = output is null ? Console.OpenStandardOutput() : new FileStream(output, a.Has("force") ? FileMode.Create : FileMode.CreateNew, FileAccess.Write);
        long count = 0;
        var loss = new LossReport();
        switch (fmt)
        {
            case "llog-jsonl":
                using (var w = new JsonlExchangeWriter(stream, engine.Store.Info.StoreId, leaveOpen: true))
                    foreach (var r in records) { w.Write(r); count++; }
                break;
            case "otlp-json":
                {
                    var list = records.ToList();
                    count = list.Count;
                    OtlpJson.Write(stream, list, loss);
                    break;
                }
            case "raw":
                {
                    var list = records.ToList();
                    count = list.Count;
                    RawTextImporter.Export(list, stream);
                    break;
                }
            default: throw new CliException(ExitCodes.Usage, $"Unknown export format '{fmt}'.");
        }
        Console.Error.WriteLine($"exported {count} record(s) as {fmt}; fidelity: {(fmt == "otlp-json" ? loss.ToString() : "lossless")}" + (report.IsClean ? "" : $"; {report.Issues.Count} integrity issue(s): affected data skipped"));
        return report.IsClean ? ExitCodes.Ok : ExitCodes.PartialResults;
    }

    private static int Import(Args a)
    {
        if (a.Positional.Count < 2) throw new CliException(ExitCodes.Usage, "Usage: " + Help["import"]);
        var root = Store(a);
        var file = a.Positional[1];
        var fmt = a.Get("format") ?? throw new CliException(ExitCodes.Usage, "import requires --format.");
        var loss = new LossReport();
        var now = UnixTime.NowUnixNanos();
        using var input = File.OpenRead(file);
        IEnumerable<LogRecord> records = fmt switch
        {
            "llog-jsonl" => new JsonlExchangeReader().Read(new StreamReader(input, Encoding.UTF8)),
            "otlp-json" => OtlpJson.Read(input, loss),
            "jsonl" => StructuredJsonlImporter.Read(input, new StructuredJsonlImporter.Options(a.Has("preserve-raw"), a.Get("service")), loss, now),
            "text" => RawTextImporter.Read(input, Path.GetFileName(file), now,
                a.Get("service") is { } svc ? new ContextSnapshot(ContextKind.Resource, null, [new("service.name", svc)], null) : null),
            _ => throw new CliException(ExitCodes.Usage, $"Unknown import format '{fmt}'."),
        };
        using var w = StoreWriter.Open(root);
        var batch = new List<LogRecord>(4096);
        long total = 0;
        foreach (var r in records)
        {
            r.Sequence = 0;
            batch.Add(r);
            if (batch.Count == 4096) { w.WriteBatch(batch); total += batch.Count; batch.Clear(); }
        }
        if (batch.Count > 0) { w.WriteBatch(batch); total += batch.Count; }
        w.Flush(AckLevel.FsyncConfirmed);
        Console.WriteLine($"imported {total} record(s) from {file} ({fmt}); last sequence {w.LastSequence}; mapping: {loss}");
        return ExitCodes.Ok;
    }

    private static int Tail(Args a)
    {
        var root = Store(a);
        var lines = a.Int("lines", 20);
        var fo = new FriendlyOptions();
        var engine = QueryEngine.Open(root);
        var last = engine.Execute(new QueryRequest { Where = a.Get("where"), Limit = lines, Order = SortOrder.Descending });
        foreach (var h in Enumerable.Reverse(last.Hits)) Console.WriteLine(a.Format == "jsonl" ? CanonicalJson.ToFriendlyJson(h.Record, last.StoreId, fo) : TextLine(h.Record, fo));
        if (!a.Has("follow")) return ExitCodes.Ok;
        var seq = last.Hits.Count > 0 ? last.Hits.Max(h => h.Record.Sequence) : 0UL;
        while (true)
        {
            Thread.Sleep(500);
            // Re-open to see new and rotated segments; the active segment is read up to its last complete block.
            var e = QueryEngine.Open(root);
            var where = string.IsNullOrWhiteSpace(a.Get("where")) ? $"seq > {seq}" : $"({a.Get("where")}) AND seq > {seq}";
            var res = e.Execute(new QueryRequest { Where = where, Limit = 10_000 });
            foreach (var h in res.Hits)
            {
                Console.WriteLine(a.Format == "jsonl" ? CanonicalJson.ToFriendlyJson(h.Record, res.StoreId, fo) : TextLine(h.Record, fo));
                seq = Math.Max(seq, h.Record.Sequence);
            }
        }
    }
}
