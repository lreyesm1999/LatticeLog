using System.Text;
using System.Text.Json;
using LatticeLog.OpenTelemetry;
using LatticeLog.Storage.Json;

namespace LatticeLog.Sources;

public enum LogFormat
{
    LatticeJsonl,
    OtlpJsonLines,
    OtlpJsonDocument,
    Structlog,
    SerilogClef,
    Pino,
    GenericJson,
    Text,
}

public sealed record FormatDetection(LogFormat Format, int SampledLines, int JsonObjectLines, string Reason)
{
    public string Name => Format switch
    {
        LogFormat.LatticeJsonl => "latticelog.jsonl",
        LogFormat.OtlpJsonLines => "otlp-json (one request per line)",
        LogFormat.OtlpJsonDocument => "otlp-json (single document)",
        LogFormat.Structlog => "structlog json",
        LogFormat.SerilogClef => "serilog clef",
        LogFormat.Pino => "pino",
        LogFormat.GenericJson => "generic json lines",
        _ => "plain text",
    };

    /// <summary>The existing importer that converts this format (specs/local-sources.md, "Migration").</summary>
    public string Importer => Format switch
    {
        LogFormat.LatticeJsonl => "JsonlExchangeReader",
        LogFormat.OtlpJsonLines or LogFormat.OtlpJsonDocument => "OtlpJson",
        LogFormat.Text => "RawTextImporter",
        _ => "StructuredJsonlImporter",
    };

    public bool IsStructuredJson => Format is LogFormat.Structlog or LogFormat.SerilogClef or LogFormat.Pino or LogFormat.GenericJson;
}

/// <summary>Detects a log file's format from its first lines (read-only, bounded).</summary>
public static class FormatSniffer
{
    public static FormatDetection Detect(string path, int maxLines = 64, int maxBytes = 256 * 1024)
    {
        using var fs = Inputs.OpenShared(path);
        var buf = new byte[(int)Math.Min(maxBytes, fs.Length)];
        var n = fs.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
        return Detect(buf.AsSpan(0, n), fs.Length <= n, maxLines);
    }

    public static FormatDetection Detect(ReadOnlySpan<byte> head, bool isWholeFile, int maxLines = 64)
    {
        if (head.StartsWith("﻿"u8)) head = head[3..]; // UTF-8 byte order mark EF BB BF
        var lines = new List<byte[]>();
        var rest = head;
        while (rest.Length > 0 && lines.Count < maxLines)
        {
            var nl = rest.IndexOf((byte)'\n');
            if (nl < 0)
            {
                if (isWholeFile) lines.Add(rest.ToArray()); // else a cut-off line is not sampled
                break;
            }
            lines.Add(rest[..nl].TrimEnd((byte)'\r').ToArray());
            rest = rest[(nl + 1)..];
        }
        var nonEmpty = lines.Where(l => l.AsSpan().IndexOfAnyExcept((byte)' ', (byte)'\t') >= 0).ToList();
        if (nonEmpty.Count == 0)
        {
            // One huge line with no newline yet: an OTLP document is the only JSON we can still recognize.
            var text = Encoding.UTF8.GetString(head[..Math.Min(head.Length, 4096)]);
            if (text.TrimStart().StartsWith('{') && text.Contains("\"resourceLogs\"", StringComparison.Ordinal))
                return new(LogFormat.OtlpJsonDocument, 0, 0, "single JSON document with resourceLogs");
            return new(LogFormat.Text, 0, 0, "no complete line to sample");
        }
        var first = nonEmpty[0];
        using (var firstDoc = TryParse(first))
        {
            if (firstDoc is { RootElement.ValueKind: JsonValueKind.Object } d)
            {
                var e = d.RootElement;
                if (e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "header"
                    && e.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.String && f.GetString() == "latticelog.jsonl")
                    return new(LogFormat.LatticeJsonl, nonEmpty.Count, nonEmpty.Count, "latticelog.jsonl header line");
                if (e.TryGetProperty("resourceLogs", out _))
                    return new(LogFormat.OtlpJsonLines, nonEmpty.Count, nonEmpty.Count, "each line is an OTLP ExportLogsServiceRequest");
            }
            else if (first.AsSpan().TrimStart((byte)' ').StartsWith("{"u8)
                     && Encoding.UTF8.GetString(head[..Math.Min(head.Length, 4096)]).Contains("\"resourceLogs\"", StringComparison.Ordinal))
                return new(LogFormat.OtlpJsonDocument, nonEmpty.Count, 0, "multi-line JSON document with resourceLogs");
        }
        int json = 0, clef = 0, pino = 0, structlog = 0;
        foreach (var l in nonEmpty)
        {
            using var d = TryParse(l);
            if (d is not { RootElement.ValueKind: JsonValueKind.Object }) continue;
            json++;
            var e = d.RootElement;
            bool Has(string k) => e.TryGetProperty(k, out _);
            if (Has("@t") || Has("@mt") || Has("@m")) clef++;
            else if (e.TryGetProperty("level", out var lv) && lv.ValueKind == JsonValueKind.Number && (Has("time") || Has("pid") || Has("hostname"))) pino++;
            else if (Has("event") && (Has("level") || Has("log_level") || Has("timestamp"))) structlog++;
        }
        // Tracebacks or banners between JSON lines are kept as text records; the file is still JSON lines.
        if (json * 2 < nonEmpty.Count)
            return new(LogFormat.Text, nonEmpty.Count, json, json == 0 ? "no line is a JSON object" : $"only {json} of {nonEmpty.Count} sampled lines are JSON objects");
        var reason = $"{json} of {nonEmpty.Count} sampled lines are JSON objects";
        if (clef * 2 >= json) return new(LogFormat.SerilogClef, nonEmpty.Count, json, reason + "; @t/@mt/@m keys");
        if (pino * 2 >= json) return new(LogFormat.Pino, nonEmpty.Count, json, reason + "; numeric level with time/pid/hostname");
        if (structlog * 2 >= json) return new(LogFormat.Structlog, nonEmpty.Count, json, reason + "; event + level/timestamp keys");
        return new(LogFormat.GenericJson, nonEmpty.Count, json, reason);
    }

    private static JsonDocument? TryParse(byte[] line)
    {
        try
        {
            var r = new Utf8JsonReader(line);
            JsonDocument? doc = JsonDocument.ParseValue(ref r);
            try
            {
                if (r.BytesConsumed < line.Length && line.AsSpan((int)r.BytesConsumed).IndexOfAnyExcept((byte)' ', (byte)'\t', (byte)'\r') >= 0) return null;
                var owned = doc;
                doc = null; // ownership passes to the caller
                return owned;
            }
            finally { doc?.Dispose(); }
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>Turns a byte range of a log file into records with the existing importers.</summary>
internal static class RecordParsers
{
    public const string AttrImportError = "latticelog.import.error";

    /// <summary>
    /// Records of <paramref name="range"/> (complete lines). Lines an importer cannot parse become text records flagged
    /// <see cref="RecordFlags.LossyImport"/>; nothing is dropped. <paramref name="defs"/> carries latticelog.jsonl header/
    /// context/schema lines across incremental runs; new definition lines are appended to <paramref name="newDefs"/>.
    /// </summary>
    public static IEnumerable<LogRecord> Parse(LogFormat format, Stream range, string fileName, string? defaultService, IReadOnlyList<string> defs,
        List<string> newDefs, LossReport loss, long now)
    {
        var svcCtx = defaultService is null ? null : new ContextSnapshot(ContextKind.Resource, null, [new("service.name", defaultService)], null);
        switch (format)
        {
            case LogFormat.Text:
                return RawTextImporter.Read(range, fileName, now, svcCtx);
            case LogFormat.LatticeJsonl:
                return Exchange(range, defs, newDefs, svcCtx, loss, now);
            case LogFormat.OtlpJsonLines:
                return OtlpLines(range, svcCtx, loss, now);
            case LogFormat.OtlpJsonDocument:
                return OtlpJson.Read(range, loss);
            default:
                return StructuredJsonlImporter.Read(range, new StructuredJsonlImporter.Options(false, defaultService), loss, now);
        }
    }

    private static LogRecord TextRecord(byte[] line, string error, long lineNo, ContextSnapshot? ctx, long now)
    {
        var r = new LogRecord { TimestampUnixNanos = now, ObservedUnixNanos = now, Context = ctx, Flags = RecordFlags.LossyImport };
        r.SetBody(Value.From(Encoding.UTF8.GetString(line)));
        r.Attributes = [new(AttrImportError, error), new("latticelog.import.line", lineNo)];
        return r;
    }

    private static IEnumerable<LogRecord> Exchange(Stream range, IReadOnlyList<string> defs, List<string> newDefs, ContextSnapshot? ctx, LossReport loss, long now)
    {
        var reader = new JsonlExchangeReader();
        foreach (var d in defs) reader.ReadLine(d);
        long lineNo = 0;
        foreach (var (bytes, _) in RawLines.Split(range, 16 << 20))
        {
            lineNo++;
            var text = Encoding.UTF8.GetString(bytes.AsSpan().TrimEnd((byte)'\r'));
            if (text.Length == 0) continue;
            LogRecord? rec;
            try { rec = reader.ReadLine(text); }
            catch (FormatException ex)
            {
                loss.Add("unparseable_jsonl_exchange_line_kept_as_text");
                rec = TextRecord(bytes, ex.Message, lineNo, ctx, now);
            }
            if (rec is null) { newDefs.Add(text); continue; } // header/context/schema: needed to resume later
            yield return rec;
        }
    }

    private static IEnumerable<LogRecord> OtlpLines(Stream range, ContextSnapshot? ctx, LossReport loss, long now)
    {
        long lineNo = 0;
        foreach (var (bytes, _) in RawLines.Split(range, 64 << 20))
        {
            lineNo++;
            if (bytes.AsSpan().IndexOfAnyExcept((byte)' ', (byte)'\t', (byte)'\r') < 0) continue;
            List<LogRecord> recs;
            try
            {
                using var input = new MemoryStream(bytes, writable: false);
                recs = OtlpJson.Read(input, loss);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException or InvalidOperationException or KeyNotFoundException)
            {
                loss.Add("unparseable_otlp_line_kept_as_text");
                recs = [TextRecord(bytes, ex.Message, lineNo, ctx, now)];
            }
            foreach (var r in recs) yield return r;
        }
    }
}

/// <summary>Read-only access to originals: shared with writers/rotators, never locked, never modified.</summary>
internal static class Inputs
{
    public static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
}

/// <summary>A read-only window [start, end) over a stream.</summary>
internal sealed class RangeStream(Stream inner, long from, long end) : Stream
{
    private readonly long _start = from;
    private long _pos = from;
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        if (_pos >= end) return 0;
        if (inner.Position != _pos) inner.Position = _pos;
        var n = inner.Read(buffer[..(int)Math.Min(buffer.Length, end - _pos)]);
        _pos += n;
        return n;
    }
    public long Consumed => _pos - _start;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => end - _start;
    public override long Position { get => _pos - _start; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
