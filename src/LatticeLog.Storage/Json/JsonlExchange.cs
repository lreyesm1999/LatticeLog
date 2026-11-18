using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LatticeLog.Storage.Json;

/// <summary>
/// Lossless JSON Lines exchange format "latticelog.jsonl" v1 (specs/jsonl-exchange.md).
/// Guarantees decode(encode(records)) == records, so data can leave LatticeLog without the binary format (P13-T10).
/// </summary>
public sealed class JsonlExchangeWriter : IDisposable
{
    private readonly Stream _out;
    private readonly bool _leaveOpen;
    private readonly Dictionary<EventDescriptor, int> _schemas = new();
    private readonly Dictionary<ContextSnapshot, int> _contexts = new(ReferenceEqualityComparer.Instance);
    private readonly ArrayBufferWriterStream _buf = new();
    public long RecordsWritten { get; private set; }

    public JsonlExchangeWriter(Stream output, string? storeId, bool leaveOpen = false)
    {
        _out = output;
        _leaveOpen = leaveOpen;
        WriteLine(w =>
        {
            w.WriteStartObject();
            w.WriteString("type", "header");
            w.WriteString("format", "latticelog.jsonl");
            w.WriteNumber("version", 1);
            if (storeId is not null) w.WriteString("store_id", storeId);
            w.WriteEndObject();
        });
    }

    private void WriteLine(Action<Utf8JsonWriter> body)
    {
        _buf.Reset();
        using (var w = new Utf8JsonWriter(_buf, CanonicalJson.WriterOptions)) body(w);
        _buf.WriteByte((byte)'\n');
        _out.Write(_buf.Span);
    }

    private int SchemaRef(EventDescriptor d)
    {
        if (_schemas.TryGetValue(d, out var id)) return id;
        id = _schemas.Count + 1;
        _schemas[d] = id;
        WriteLine(w =>
        {
            w.WriteStartObject();
            w.WriteString("type", "schema");
            w.WriteNumber("ref", id);
            w.WriteNumber("id", d.Id);
            w.WriteString("name", d.Name);
            w.WriteNumber("version", d.Version);
            w.WriteNumber("severity_number", (int)d.Severity);
            if (d.MessageTemplate is not null) w.WriteString("template", d.MessageTemplate);
            if (d.Description is not null) w.WriteString("description", d.Description);
            w.WriteStartArray("fields");
            foreach (var f in d.Fields)
            {
                w.WriteStartObject();
                w.WriteString("name", f.Name);
                w.WriteString("kind", f.Kind.ToString());
                if (f.Sensitivity != Sensitivity.None) w.WriteString("sensitivity", f.Sensitivity.ToString());
                if (f.Description is not null) w.WriteString("description", f.Description);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
        return id;
    }

    private int ContextRef(ContextSnapshot c)
    {
        if (_contexts.TryGetValue(c, out var id)) return id;
        var parent = c.Parent is null ? 0 : ContextRef(c.Parent);
        id = _contexts.Count + 1;
        _contexts[c] = id;
        WriteLine(w =>
        {
            w.WriteStartObject();
            w.WriteString("type", "context");
            w.WriteNumber("ref", id);
            if (parent != 0) w.WriteNumber("parent", parent);
            w.WriteString("kind", c.Kind.ToString());
            if (c.Name is not null) w.WriteString("name", c.Name);
            w.WritePropertyName("attributes");
            CanonicalJson.WritePairs(w, c.Attributes, JsonValueStyle.Lossless);
            w.WriteEndObject();
        });
        return id;
    }

    public void Write(LogRecord r)
    {
        var schema = r.Event is null ? 0 : SchemaRef(r.Event);
        var ctx = r.Context is null ? 0 : ContextRef(r.Context);
        WriteLine(w =>
        {
            w.WriteStartObject();
            w.WriteString("type", "record");
            if (r.Sequence != 0) w.WriteString("seq", r.Sequence.ToString(CultureInfo.InvariantCulture));
            w.WriteString("time", UnixTime.FormatRfc3339(r.TimestampUnixNanos));
            if (r.ObservedUnixNanos != 0) w.WriteString("observed_time", UnixTime.FormatRfc3339(r.ObservedUnixNanos));
            if (r.MonotonicNanos != 0) w.WriteString("monotonic_ns", r.MonotonicNanos.ToString(CultureInfo.InvariantCulture));
            w.WriteNumber("severity_number", (int)r.Severity);
            if (schema != 0) w.WriteNumber("schema", schema);
            if (r.EventId != 0) w.WriteNumber("event_id", r.EventId);
            if (r.EventName is not null) w.WriteString("event", r.EventName);
            if (r.HasBody) { w.WritePropertyName("body"); CanonicalJson.WriteValue(w, r.Body, JsonValueStyle.Lossless); }
            if (r.Attributes.Length > 0) { w.WritePropertyName("attributes"); CanonicalJson.WritePairs(w, r.Attributes, JsonValueStyle.Lossless); }
            if (!r.TraceId.IsEmpty) w.WriteString("trace_id", r.TraceId.ToString());
            if (!r.SpanId.IsEmpty) w.WriteString("span_id", r.SpanId.ToString());
            if (r.TraceFlags != 0) w.WriteNumber("trace_flags", r.TraceFlags);
            if (r.OperationId is not null) w.WriteString("operation_id", r.OperationId);
            if (r.ParentOperationId is not null) w.WriteString("parent_operation_id", r.ParentOperationId);
            if (r.CorrelationId is not null) w.WriteString("correlation_id", r.CorrelationId);
            if (r.Exception is not null) { w.WritePropertyName("exception"); CanonicalJson.WriteException(w, r.Exception); }
            if (ctx != 0) w.WriteNumber("context", ctx);
            if (r.Flags != 0) w.WriteNumber("flags", (int)r.Flags);
            w.WriteEndObject();
        });
        RecordsWritten++;
    }

    public void Dispose()
    {
        _out.Flush();
        if (!_leaveOpen) _out.Dispose();
    }

    private sealed class ArrayBufferWriterStream : Stream
    {
        private byte[] _b = new byte[4096];
        private int _n;
        public ReadOnlySpan<byte> Span => _b.AsSpan(0, _n);
        public void Reset() => _n = 0;
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_n + buffer.Length > _b.Length) Array.Resize(ref _b, Math.Max(_b.Length * 2, _n + buffer.Length));
            buffer.CopyTo(_b.AsSpan(_n));
            _n += buffer.Length;
        }
        public override void WriteByte(byte value) => Write([value]);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _n;
        public override long Position { get => _n; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

public sealed class JsonlExchangeReader
{
    private readonly Dictionary<int, EventDescriptor> _schemas = new();
    private readonly Dictionary<int, ContextSnapshot> _contexts = new();

    public string? StoreId { get; private set; }

    /// <summary>Parses the stream. Malformed lines raise FormatException with the line number (no silent skipping).</summary>
    public IEnumerable<LogRecord> Read(TextReader input)
    {
        var lineNo = 0;
        var sawHeader = false;
        while (input.ReadLine() is { } line)
        {
            lineNo++;
            if (line.Length == 0) continue;
            LogRecord? rec;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var e = doc.RootElement;
                var type = e.GetProperty("type").GetString();
                rec = null;
                switch (type)
                {
                    case "header":
                        if (e.GetProperty("format").GetString() != "latticelog.jsonl") throw new FormatException("Unknown exchange format.");
                        if (e.GetProperty("version").GetInt32() != 1) throw new FormatException("Unsupported exchange version.");
                        StoreId = e.TryGetProperty("store_id", out var sid) ? sid.GetString() : null;
                        sawHeader = true;
                        break;
                    case "schema":
                        {
                            var fields = e.GetProperty("fields").EnumerateArray().Select(f => new EventField(
                                f.GetProperty("name").GetString()!,
                                Enum.Parse<ValueKind>(f.GetProperty("kind").GetString()!),
                                f.TryGetProperty("sensitivity", out var s) ? Enum.Parse<Sensitivity>(s.GetString()!) : Sensitivity.None,
                                f.TryGetProperty("description", out var d) ? d.GetString() : null)).ToArray();
                            _schemas[e.GetProperty("ref").GetInt32()] = new EventDescriptor(
                                e.GetProperty("id").GetUInt32(), e.GetProperty("name").GetString()!, e.GetProperty("version").GetUInt16(),
                                (Severity)e.GetProperty("severity_number").GetByte(),
                                e.TryGetProperty("template", out var t) ? t.GetString() : null, fields,
                                e.TryGetProperty("description", out var desc) ? desc.GetString() : null);
                            break;
                        }
                    case "context":
                        {
                            var parent = e.TryGetProperty("parent", out var p) ? _contexts[p.GetInt32()] : null;
                            _contexts[e.GetProperty("ref").GetInt32()] = new ContextSnapshot(
                                Enum.Parse<ContextKind>(e.GetProperty("kind").GetString()!),
                                e.TryGetProperty("name", out var n) ? n.GetString() : null,
                                CanonicalJson.ReadPairs(e.GetProperty("attributes")), parent);
                            break;
                        }
                    case "record":
                        rec = ParseRecord(e);
                        break;
                    default:
                        throw new FormatException($"Unknown line type '{type}'.");
                }
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
            {
                throw new FormatException($"latticelog.jsonl line {lineNo}: {ex.Message}", ex);
            }
            if (!sawHeader) throw new FormatException("latticelog.jsonl must start with a header line.");
            if (rec is not null) yield return rec;
        }
    }

    private LogRecord ParseRecord(JsonElement e)
    {
        var r = new LogRecord();
        if (e.TryGetProperty("seq", out var seq)) r.Sequence = ulong.Parse(seq.GetString()!, CultureInfo.InvariantCulture);
        r.TimestampUnixNanos = ParseTime(e.GetProperty("time").GetString()!);
        if (e.TryGetProperty("observed_time", out var ot)) r.ObservedUnixNanos = ParseTime(ot.GetString()!);
        if (e.TryGetProperty("monotonic_ns", out var mono)) r.MonotonicNanos = long.Parse(mono.GetString()!, CultureInfo.InvariantCulture);
        r.Severity = (Severity)e.GetProperty("severity_number").GetByte();
        if (e.TryGetProperty("schema", out var s)) r.Event = _schemas[s.GetInt32()];
        if (e.TryGetProperty("event_id", out var eid)) r.EventId = eid.GetUInt32();
        if (e.TryGetProperty("event", out var en)) r.EventName = en.GetString();
        if (e.TryGetProperty("body", out var body)) r.SetBody(CanonicalJson.ReadValue(body));
        if (e.TryGetProperty("attributes", out var attrs)) r.Attributes = CanonicalJson.ReadPairs(attrs);
        if (e.TryGetProperty("trace_id", out var tid)) r.TraceId = TraceId.Parse(tid.GetString()!);
        if (e.TryGetProperty("span_id", out var sid)) r.SpanId = SpanId.Parse(sid.GetString()!);
        if (e.TryGetProperty("trace_flags", out var tf)) r.TraceFlags = tf.GetByte();
        if (e.TryGetProperty("operation_id", out var op)) r.OperationId = op.GetString();
        if (e.TryGetProperty("parent_operation_id", out var pop)) r.ParentOperationId = pop.GetString();
        if (e.TryGetProperty("correlation_id", out var cid)) r.CorrelationId = cid.GetString();
        if (e.TryGetProperty("exception", out var ex)) r.Exception = CanonicalJson.ReadException(ex);
        if (e.TryGetProperty("context", out var c)) r.Context = _contexts[c.GetInt32()];
        if (e.TryGetProperty("flags", out var fl)) r.Flags = (RecordFlags)fl.GetByte();
        return r;
    }

    private static long ParseTime(string s) => UnixTime.TryParseRfc3339(s, out var t) ? t : throw new FormatException($"Invalid timestamp '{s}'.");
}
