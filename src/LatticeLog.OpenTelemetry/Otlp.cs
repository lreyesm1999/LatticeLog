using System.Globalization;
using System.Text;
using System.Text.Json;
using LatticeLog.Storage.Json;

namespace LatticeLog.OpenTelemetry;

/// <summary>Counts every place where a mapping could not preserve full fidelity (P13-T09). Never silent.</summary>
public sealed class LossReport
{
    public Dictionary<string, long> Items { get; } = new(StringComparer.Ordinal);
    public void Add(string what, long n = 1) => Items[what] = Items.GetValueOrDefault(what) + n;
    public bool IsLossless => Items.Count == 0;
    public override string ToString() => IsLossless ? "lossless" : string.Join(", ", Items.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}"));
}

/// <summary>
/// Mapping between LatticeLog records and the OpenTelemetry Logs Data Model, serialized as OTLP/JSON
/// (ExportLogsServiceRequest, proto3 JSON mapping with hex trace/span ids). See specs/otel-mapping.md.
/// </summary>
public static class OtlpJson
{
    public const string AttrEventId = "latticelog.event.id";
    public const string AttrEventVersion = "latticelog.event.version";
    public const string AttrOperationId = "latticelog.operation_id";
    public const string AttrParentOperationId = "latticelog.parent_operation_id";
    public const string AttrCorrelationId = "latticelog.correlation_id";
    public const string AttrSequence = "latticelog.seq";

    /// <summary>Writes one ExportLogsServiceRequest containing <paramref name="records"/>, grouped by resource.</summary>
    public static void Write(Stream output, IEnumerable<LogRecord> records, LossReport loss, bool indented = false)
    {
        using var w = new Utf8JsonWriter(output, CanonicalJson.WriterOptions with { Indented = indented });
        w.WriteStartObject();
        w.WriteStartArray("resourceLogs");
        foreach (var group in records.GroupBy(r => RootOf(r.Context), ReferenceEqualityComparer.Instance))
        {
            var root = (ContextSnapshot?)group.Key;
            w.WriteStartObject();
            w.WriteStartObject("resource");
            WriteAttributes(w, root?.Attributes ?? [], loss);
            w.WriteEndObject();
            w.WriteStartArray("scopeLogs");
            w.WriteStartObject();
            w.WriteStartObject("scope");
            w.WriteString("name", "latticelog");
            w.WriteEndObject();
            w.WriteStartArray("logRecords");
            foreach (var r in group) WriteRecord(w, r, loss);
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static ContextSnapshot? RootOf(ContextSnapshot? c)
    {
        while (c?.Parent is not null) c = c.Parent;
        return c;
    }

    private static void WriteRecord(Utf8JsonWriter w, LogRecord r, LossReport loss)
    {
        w.WriteStartObject();
        w.WriteString("timeUnixNano", r.TimestampUnixNanos.ToString(CultureInfo.InvariantCulture));
        if (r.TimestampUnixNanos < 0) loss.Add("negative_timestamp_unrepresentable_as_fixed64");
        if (r.ObservedUnixNanos != 0) w.WriteString("observedTimeUnixNano", r.ObservedUnixNanos.ToString(CultureInfo.InvariantCulture));
        if (r.MonotonicNanos != 0) loss.Add("monotonic_time_dropped");
        if (r.Severity != Severity.Unspecified)
        {
            w.WriteNumber("severityNumber", (int)r.Severity);
            w.WriteString("severityText", r.Severity.ToText());
        }
        if (r.EffectiveEventName is { } en) w.WriteString("eventName", en);
        if (r.HasBody) { w.WritePropertyName("body"); WriteAnyValue(w, r.Body, loss); }
        var attrs = new List<LogAttribute>();
        // Inherited (non-resource) context attributes are flattened: provenance own-vs-inherited is lost.
        if (r.Context?.Parent is not null)
        {
            var root = RootOf(r.Context)!;
            var rootKeys = root.Attributes.Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var a in r.Context.Flatten())
                if (!rootKeys.Contains(a.Key) || !root.TryGet(a.Key, out var rv) || !rv.Equals(a.Value)) attrs.Add(a);
            if (attrs.Count > 0) loss.Add("context_flattened_into_attributes");
        }
        attrs.AddRange(r.Attributes);
        if (r.EffectiveEventId != 0) attrs.Add(new LogAttribute(AttrEventId, (long)r.EffectiveEventId));
        if (r.Event is { } d)
        {
            attrs.Add(new LogAttribute(AttrEventVersion, (long)d.Version));
            loss.Add("event_schema_descriptor_not_exported");
        }
        if (r.OperationId is not null) attrs.Add(new LogAttribute(AttrOperationId, r.OperationId));
        if (r.ParentOperationId is not null) attrs.Add(new LogAttribute(AttrParentOperationId, r.ParentOperationId));
        if (r.CorrelationId is not null) attrs.Add(new LogAttribute(AttrCorrelationId, r.CorrelationId));
        if (r.Sequence != 0) attrs.Add(new LogAttribute(AttrSequence, Value.From(r.Sequence)));
        if (r.Exception is { } ex)
        {
            attrs.Add(new LogAttribute("exception.type", ex.Type));
            if (ex.Message is not null) attrs.Add(new LogAttribute("exception.message", ex.Message));
            if (ex.StackTrace is not null) attrs.Add(new LogAttribute("exception.stacktrace", ex.StackTrace));
            if (ex.Inner.Count > 0) loss.Add("inner_exceptions_dropped");
        }
        if (CanonicalJson.HasDuplicateKeys(attrs)) loss.Add("duplicate_attribute_keys");
        WriteAttributes(w, attrs, loss);
        if (r.TraceFlags != 0) w.WriteNumber("flags", r.TraceFlags);
        if (!r.TraceId.IsEmpty) w.WriteString("traceId", r.TraceId.ToString());
        if (!r.SpanId.IsEmpty) w.WriteString("spanId", r.SpanId.ToString());
        if (r.Flags != 0) loss.Add("latticelog_record_flags_dropped");
        w.WriteEndObject();
    }

    private static void WriteAttributes(Utf8JsonWriter w, IEnumerable<LogAttribute> attrs, LossReport loss)
    {
        w.WriteStartArray("attributes");
        foreach (var a in attrs)
        {
            w.WriteStartObject();
            w.WriteString("key", a.Key);
            w.WritePropertyName("value");
            WriteAnyValue(w, a.Value, loss);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteAnyValue(Utf8JsonWriter w, Value v, LossReport loss)
    {
        w.WriteStartObject();
        switch (v.Kind)
        {
            case ValueKind.Null: break; // empty AnyValue
            case ValueKind.Bool: w.WriteBoolean("boolValue", v.AsBool()); break;
            case ValueKind.Int64: w.WriteString("intValue", v.AsInt64().ToString(CultureInfo.InvariantCulture)); break;
            case ValueKind.UInt64:
                if (v.AsUInt64() <= long.MaxValue) w.WriteString("intValue", v.AsUInt64().ToString(CultureInfo.InvariantCulture));
                else { w.WriteString("stringValue", v.AsUInt64().ToString(CultureInfo.InvariantCulture)); loss.Add("uint64_above_int64_as_string"); }
                loss.Add("uint64_type_as_int");
                break;
            case ValueKind.Double:
                {
                    var d = v.AsDouble();
                    if (double.IsFinite(d)) w.WriteNumber("doubleValue", d);
                    else { w.WriteString("doubleValue", double.IsNaN(d) ? "NaN" : d > 0 ? "Infinity" : "-Infinity"); if (double.IsNaN(d)) loss.Add("nan_payload"); }
                    break;
                }
            case ValueKind.String:
                if (Value.HasLoneSurrogate(v.AsString())) loss.Add("lone_surrogate_replaced");
                w.WriteString("stringValue", Value.HasLoneSurrogate(v.AsString()) ? new string(v.AsString().Select(c => char.IsSurrogate(c) ? '�' : c).ToArray()) : v.AsString());
                break;
            case ValueKind.Bytes: w.WriteBase64String("bytesValue", v.AsBytes()); break;
            case ValueKind.Array:
                w.WriteStartObject("arrayValue");
                w.WriteStartArray("values");
                foreach (var x in v.AsArray()) WriteAnyValue(w, x, loss);
                w.WriteEndArray();
                w.WriteEndObject();
                break;
            case ValueKind.Map:
                w.WriteStartObject("kvlistValue");
                w.WriteStartArray("values");
                foreach (var kv in v.AsMap())
                {
                    w.WriteStartObject();
                    w.WriteString("key", kv.Key);
                    w.WritePropertyName("value");
                    WriteAnyValue(w, kv.Value, loss);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
                break;
            case ValueKind.Timestamp: w.WriteString("stringValue", UnixTime.FormatRfc3339(v.AsTimestampUnixNanos())); loss.Add("timestamp_as_string"); break;
            case ValueKind.Duration: w.WriteString("intValue", v.AsDurationNanos().ToString(CultureInfo.InvariantCulture)); loss.Add("duration_as_int_nanos"); break;
            case ValueKind.Uuid: w.WriteString("stringValue", v.AsGuid().ToString("D")); loss.Add("uuid_as_string"); break;
            case ValueKind.Decimal: w.WriteString("stringValue", v.AsDecimal().ToString(CultureInfo.InvariantCulture)); loss.Add("decimal_as_string"); break;
        }
        w.WriteEndObject();
    }

    // ------------------------------------------------------------------ import (receiver side, P13-T04)

    public sealed record ImportLimits(int MaxRecords = 1_000_000, int MaxAttributes = 1024, int MaxDepth = 16, long MaxBytes = 256L * 1024 * 1024);

    public static List<LogRecord> Read(Stream input, LossReport loss, ImportLimits? limits = null)
    {
        limits ??= new ImportLimits();
        if (input.CanSeek && input.Length > limits.MaxBytes) throw new InvalidDataException($"OTLP payload exceeds {limits.MaxBytes} bytes.");
        using var doc = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = limits.MaxDepth * 4 + 16 });
        var result = new List<LogRecord>();
        if (!doc.RootElement.TryGetProperty("resourceLogs", out var rls)) throw new InvalidDataException("Missing resourceLogs.");
        foreach (var rl in rls.EnumerateArray())
        {
            var resAttrs = rl.TryGetProperty("resource", out var res) && res.TryGetProperty("attributes", out var ra) ? ReadAttributes(ra, limits, 0) : [];
            var root = new ContextSnapshot(ContextKind.Resource, null, resAttrs, null);
            if (!rl.TryGetProperty("scopeLogs", out var sls)) continue;
            foreach (var sl in sls.EnumerateArray())
            {
                var ctx = root;
                if (sl.TryGetProperty("scope", out var scope) && scope.TryGetProperty("name", out var sn) && sn.GetString() is { Length: > 0 } scopeName && scopeName != "latticelog")
                {
                    var sattrs = new List<LogAttribute>();
                    if (scope.TryGetProperty("version", out var sv) && sv.GetString() is { } ver) sattrs.Add(new("otel.scope.version", ver));
                    if (scope.TryGetProperty("attributes", out var sa)) sattrs.AddRange(ReadAttributes(sa, limits, 0));
                    ctx = new ContextSnapshot(ContextKind.Scope, scopeName, sattrs, root);
                }
                if (!sl.TryGetProperty("logRecords", out var lrs)) continue;
                foreach (var lr in lrs.EnumerateArray())
                {
                    if (result.Count >= limits.MaxRecords) throw new InvalidDataException($"More than {limits.MaxRecords} records in one request.");
                    result.Add(ReadRecord(lr, ctx, limits, loss));
                }
            }
        }
        return result;
    }

    private static LogRecord ReadRecord(JsonElement e, ContextSnapshot ctx, ImportLimits limits, LossReport loss)
    {
        var r = new LogRecord { Context = ctx };
        if (e.TryGetProperty("timeUnixNano", out var t)) r.TimestampUnixNanos = ReadInt64(t);
        if (e.TryGetProperty("observedTimeUnixNano", out var ot)) r.ObservedUnixNanos = ReadInt64(ot);
        if (r.TimestampUnixNanos == 0 && r.ObservedUnixNanos != 0) { r.TimestampUnixNanos = r.ObservedUnixNanos; loss.Add("time_missing_used_observed"); }
        if (e.TryGetProperty("severityNumber", out var sn) && sn.TryGetInt32(out var n) && n is >= 0 and <= 24) r.Severity = (Severity)n;
        else if (e.TryGetProperty("severityText", out var st) && SeverityExtensions.TryParse(st.GetString() ?? "", out var s)) r.Severity = s;
        if (e.TryGetProperty("eventName", out var en) && en.GetString() is { Length: > 0 } name) r.EventName = name;
        if (e.TryGetProperty("body", out var body)) r.SetBody(ReadAnyValue(body, limits, 0));
        var attrs = e.TryGetProperty("attributes", out var a) ? ReadAttributes(a, limits, 0).ToList() : [];
        string? exType = null, exMsg = null, exStack = null;
        var kept = new List<LogAttribute>(attrs.Count);
        foreach (var attr in attrs)
        {
            switch (attr.Key)
            {
                case AttrEventId when attr.Value.Kind == ValueKind.Int64 && attr.Value.AsInt64() is > 0 and <= uint.MaxValue: r.EventId = (uint)attr.Value.AsInt64(); break;
                case AttrEventVersion: break;
                case AttrOperationId when attr.Value.Kind == ValueKind.String: r.OperationId = attr.Value.AsString(); break;
                case AttrParentOperationId when attr.Value.Kind == ValueKind.String: r.ParentOperationId = attr.Value.AsString(); break;
                case AttrCorrelationId when attr.Value.Kind == ValueKind.String: r.CorrelationId = attr.Value.AsString(); break;
                case AttrSequence: break; // sequence is reassigned by the destination store
                case "exception.type" when attr.Value.Kind == ValueKind.String: exType = attr.Value.AsString(); break;
                case "exception.message" when attr.Value.Kind == ValueKind.String: exMsg = attr.Value.AsString(); break;
                case "exception.stacktrace" when attr.Value.Kind == ValueKind.String: exStack = attr.Value.AsString(); break;
                default: kept.Add(attr); break;
            }
        }
        if (exType is not null || exMsg is not null || exStack is not null) r.Exception = new ExceptionInfo(exType ?? "(unknown)", exMsg, exStack, []);
        r.Attributes = kept.ToArray();
        if (e.TryGetProperty("flags", out var fl) && fl.TryGetUInt32(out var flags)) r.TraceFlags = (byte)(flags & 0xFF);
        if (e.TryGetProperty("traceId", out var tid) && tid.GetString() is { Length: > 0 } th)
        {
            if (TraceId.TryParse(th.ToLowerInvariant(), out var id) && !id.IsEmpty) r.TraceId = id; else loss.Add("invalid_trace_id_dropped");
        }
        if (e.TryGetProperty("spanId", out var sid) && sid.GetString() is { Length: > 0 } sh)
        {
            if (SpanId.TryParse(sh.ToLowerInvariant(), out var id) && !id.IsEmpty) r.SpanId = id; else loss.Add("invalid_span_id_dropped");
        }
        if (e.TryGetProperty("droppedAttributesCount", out var dac) && dac.TryGetInt64(out var dropped) && dropped > 0) loss.Add("dropped_attributes_reported_by_source", dropped);
        return r;
    }

    private static long ReadInt64(JsonElement e) => e.ValueKind == JsonValueKind.String
        ? (ulong.TryParse(e.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var u) ? unchecked((long)u) : throw new InvalidDataException("Invalid fixed64 timestamp."))
        : e.GetInt64();

    private static LogAttribute[] ReadAttributes(JsonElement arr, ImportLimits limits, int depth)
    {
        var list = new List<LogAttribute>();
        foreach (var kv in arr.EnumerateArray())
        {
            if (list.Count >= limits.MaxAttributes) throw new InvalidDataException($"More than {limits.MaxAttributes} attributes.");
            var key = kv.GetProperty("key").GetString() ?? "";
            list.Add(new LogAttribute(key, kv.TryGetProperty("value", out var v) ? ReadAnyValue(v, limits, depth + 1) : Value.Null));
        }
        return list.ToArray();
    }

    private static Value ReadAnyValue(JsonElement v, ImportLimits limits, int depth)
    {
        if (depth > limits.MaxDepth) throw new InvalidDataException("AnyValue nesting too deep.");
        if (v.ValueKind != JsonValueKind.Object) return Value.Null;
        foreach (var p in v.EnumerateObject())
        {
            switch (p.Name)
            {
                case "stringValue": return Value.From(p.Value.GetString());
                case "boolValue": return Value.From(p.Value.GetBoolean());
                case "intValue": return Value.From(ReadInt64(p.Value));
                case "doubleValue":
                    return p.Value.ValueKind == JsonValueKind.String
                        ? Value.From(double.Parse(p.Value.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture))
                        : Value.From(p.Value.GetDouble());
                case "bytesValue": return Value.From(p.Value.GetBytesFromBase64());
                case "arrayValue":
                    return Value.From(p.Value.TryGetProperty("values", out var vals) ? vals.EnumerateArray().Select(x => ReadAnyValue(x, limits, depth + 1)).ToArray() : []);
                case "kvlistValue":
                    return Value.From(p.Value.TryGetProperty("values", out var kvs) ? ReadAttributes(kvs, limits, depth + 1) : []);
            }
        }
        return Value.Null;
    }
}

/// <summary>
/// Structured JSON Lines importer for common logger outputs (Serilog CLEF, pino, zap, generic). Recognized keys map to
/// record fields; every other key is kept as an attribute in its original order (P13-T05).
/// </summary>
public static class StructuredJsonlImporter
{
    private static readonly string[] TimeKeys = ["@t", "timestamp", "time", "ts", "@timestamp", "Timestamp"];
    private static readonly string[] LevelKeys = ["@l", "level", "severity", "lvl", "log.level", "Level"];
    private static readonly string[] MessageKeys = ["@m", "msg", "message", "Message"];
    private static readonly string[] TemplateKeys = ["@mt", "MessageTemplate"];
    private static readonly string[] ExceptionKeys = ["@x", "exception", "Exception", "error.stack"];
    private static readonly string[] TraceKeys = ["trace_id", "traceId", "@tr", "TraceId"];
    private static readonly string[] SpanKeys = ["span_id", "spanId", "@sp", "SpanId"];
    private static readonly string[] EventKeys = ["event", "event_name", "event.name", "EventName"];
    private static readonly string[] ServiceKeys = ["service", "service.name", "Application"];

    public sealed record Options(bool PreserveRawLine = false, string? DefaultService = null, int MaxLineBytes = 1 << 20);

    /// <summary>Where a top-level JSON key lands in the record (for migration previews). The first matching key of each kind wins.</summary>
    public static string DescribeKey(string key)
    {
        if (Array.IndexOf(TimeKeys, key) >= 0) return "time";
        if (Array.IndexOf(LevelKeys, key) >= 0) return "severity";
        if (Array.IndexOf(MessageKeys, key) >= 0) return "body (message)";
        if (Array.IndexOf(TemplateKeys, key) >= 0) return "attribute message_template";
        if (Array.IndexOf(ExceptionKeys, key) >= 0) return "exception (type parsed from the text, Python tracebacks included)";
        if (Array.IndexOf(TraceKeys, key) >= 0) return "trace_id";
        if (Array.IndexOf(SpanKeys, key) >= 0) return "span_id";
        if (Array.IndexOf(EventKeys, key) >= 0) return "event name when identifier-like, else body (message)";
        if (Array.IndexOf(ServiceKeys, key) >= 0) return "service.name";
        return "attribute (typed)";
    }

    public static IEnumerable<LogRecord> Read(Stream input, Options options, LossReport loss, long importTimeNanos)
    {
        var services = new Dictionary<string, ContextSnapshot>(StringComparer.Ordinal);
        ContextSnapshot Ctx(string? svc)
        {
            svc ??= options.DefaultService ?? "";
            if (!services.TryGetValue(svc, out var c))
                services[svc] = c = new ContextSnapshot(ContextKind.Resource, null, svc.Length == 0 ? [] : [new("service.name", svc)], null);
            return c;
        }
        var lineNo = 0L;
        foreach (var (bytes, _) in RawLines.Split(input, options.MaxLineBytes))
        {
            lineNo++;
            var text = bytes.AsSpan().TrimEnd((byte)'\r');
            if (text.IndexOfAnyExcept((byte)' ', (byte)9) < 0) continue; // blank line (spaces/tabs only)
            LogRecord r;
            try { r = Parse(text, Ctx, loss, importTimeNanos); }
            catch (JsonException ex)
            {
                loss.Add("unparseable_json_line_kept_as_text");
                r = new LogRecord { TimestampUnixNanos = importTimeNanos, ObservedUnixNanos = importTimeNanos, Context = Ctx(null), Flags = RecordFlags.LossyImport };
                r.SetBody(Value.From(Encoding.UTF8.GetString(text)));
                r.Attributes = [new("latticelog.import.error", ex.Message), new("latticelog.import.line", lineNo)];
            }
            if (options.PreserveRawLine)
            {
                r.Attributes = [.. r.Attributes, new LogAttribute("latticelog.raw", Value.From(bytes))];
                r.Flags |= RecordFlags.RawImport;
            }
            yield return r;
        }
    }

    private static LogRecord Parse(ReadOnlySpan<byte> line, Func<string?, ContextSnapshot> ctx, LossReport loss, long importTime)
    {
        var reader = new Utf8JsonReader(line);
        using var doc = JsonDocument.ParseValue(ref reader);
        var e = doc.RootElement;
        if (e.ValueKind != JsonValueKind.Object) throw new JsonException("Line is not a JSON object.");
        var r = new LogRecord { ObservedUnixNanos = importTime };
        var attrs = new List<LogAttribute>();
        string? service = null, message = null, template = null, eventText = null, eventKey = null;
        foreach (var p in e.EnumerateObject())
        {
            var k = p.Name;
            if (Array.IndexOf(TimeKeys, k) >= 0 && r.TimestampUnixNanos == 0 && TryTime(p.Value, out var ts)) { r.TimestampUnixNanos = ts; continue; }
            if (Array.IndexOf(LevelKeys, k) >= 0 && r.Severity == 0 && TryLevel(p.Value, out var sev)) { r.Severity = sev; continue; }
            if (Array.IndexOf(MessageKeys, k) >= 0 && message is null && p.Value.ValueKind == JsonValueKind.String) { message = p.Value.GetString(); continue; }
            if (Array.IndexOf(TemplateKeys, k) >= 0 && template is null && p.Value.ValueKind == JsonValueKind.String) { template = p.Value.GetString(); continue; }
            if (Array.IndexOf(ExceptionKeys, k) >= 0 && r.Exception is null && p.Value.ValueKind == JsonValueKind.String)
            {
                var text = p.Value.GetString()!;
                var (type, msg) = ParseExceptionText(text);
                r.Exception = new ExceptionInfo(type, msg, text, []);
                continue;
            }
            if (Array.IndexOf(TraceKeys, k) >= 0 && r.TraceId.IsEmpty && TraceId.TryParse(p.Value.GetString()?.ToLowerInvariant(), out var tid)) { r.TraceId = tid; continue; }
            if (Array.IndexOf(SpanKeys, k) >= 0 && r.SpanId.IsEmpty && SpanId.TryParse(p.Value.GetString()?.ToLowerInvariant(), out var sid)) { r.SpanId = sid; continue; }
            if (Array.IndexOf(EventKeys, k) >= 0 && eventText is null && p.Value.ValueKind == JsonValueKind.String) { eventText = p.Value.GetString(); eventKey = k; continue; }
            if (Array.IndexOf(ServiceKeys, k) >= 0 && service is null && p.Value.ValueKind == JsonValueKind.String) { service = p.Value.GetString(); continue; }
            attrs.Add(new LogAttribute(k, FromJson(p.Value)));
        }
        if (r.TimestampUnixNanos == 0)
        {
            r.TimestampUnixNanos = importTime;
            attrs.Add(new LogAttribute("latticelog.import.time_missing", true));
            loss.Add("timestamp_missing_used_import_time");
        }
        if (eventText is not null)
        {
            // structlog (and similar) put the human message under "event". Identifier-like values are event names
            // ("bridge_emit_failed"); free text ("Heartbeat failed for group x") is the message, not a name.
            if (LooksLikeEventName(eventText)) r.EventName = eventText;
            else if (message is null) message = eventText;
            else attrs.Add(new LogAttribute(eventKey!, eventText));
        }
        if (template is not null) attrs.Add(new LogAttribute("message_template", template));
        if (message is not null) r.SetBody(Value.From(message));
        r.Attributes = attrs.ToArray();
        r.Context = ctx(service);
        return r;
    }

    /// <summary>True for identifier-like event names (letters, digits, <c>_ . : - /</c>, no spaces, at most 128 chars).</summary>
    public static bool LooksLikeEventName(string s)
    {
        if (s.Length is 0 or > 128 || !(char.IsLetter(s[0]) || s[0] == '_')) return false;
        foreach (var c in s)
            if (!(char.IsLetterOrDigit(c) || c is '_' or '.' or ':' or '-' or '/')) return false;
        return true;
    }

    /// <summary>
    /// Splits a textual exception into (type, message). Python tracebacks name the exception on the first unindented
    /// line after the last frame (<c>httpx.HTTPStatusError: …</c>); other runtimes (.NET, Java) put it on the first line.
    /// </summary>
    public static (string Type, string? Message) ParseExceptionText(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines[0].TrimStart().StartsWith("Traceback (most recent call last)", StringComparison.Ordinal))
        {
            var lastFrame = Array.FindLastIndex(lines, l => l.StartsWith("  File ", StringComparison.Ordinal));
            var j = lastFrame + 1;
            while (lastFrame >= 0 && j < lines.Length && (lines[j].Length == 0 || char.IsWhiteSpace(lines[j][0]))) j++;
            if (lastFrame >= 0 && j < lines.Length)
            {
                var exLine = lines[j];
                var colon = exLine.IndexOf(':');
                var type = (colon > 0 ? exLine[..colon] : exLine).Trim();
                if (IsDottedIdentifier(type))
                {
                    var msg = string.Join('\n', [colon > 0 ? exLine[(colon + 1)..].Trim() : "", .. lines[(j + 1)..]]).Trim();
                    return (type, msg.Length == 0 ? null : msg);
                }
            }
            return ("(unparsed)", null);
        }
        var firstLine = lines[0];
        var c = firstLine.IndexOf(':');
        return (c > 0 ? firstLine[..c].Trim() : "(unparsed)", c > 0 ? firstLine[(c + 1)..].Trim() : null);
    }

    private static bool IsDottedIdentifier(string s)
    {
        if (s.Length == 0 || !(char.IsLetter(s[0]) || s[0] == '_')) return false;
        foreach (var c in s) if (!(char.IsLetterOrDigit(c) || c is '_' or '.')) return false;
        return true;
    }

    private static Value FromJson(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => Value.From(v.GetString()),
        JsonValueKind.Number => v.TryGetInt64(out var l) && !v.GetRawText().Contains('.') && !v.GetRawText().Contains('e') && !v.GetRawText().Contains('E') ? Value.From(l) : Value.From(v.GetDouble()),
        JsonValueKind.True => Value.From(true),
        JsonValueKind.False => Value.From(false),
        JsonValueKind.Array => Value.From(v.EnumerateArray().Take(4096).Select(FromJson).ToArray()),
        JsonValueKind.Object => Value.From(v.EnumerateObject().Take(4096).Select(p => new LogAttribute(p.Name, FromJson(p.Value))).ToArray()),
        _ => Value.Null,
    };

    internal static bool TryTime(JsonElement v, out long nanos)
    {
        nanos = 0;
        if (v.ValueKind == JsonValueKind.String) return UnixTime.TryParseRfc3339(v.GetString()!, out nanos);
        if (v.ValueKind != JsonValueKind.Number) return false;
        var d = v.GetDouble();
        // Heuristic on magnitude (documented in specs/otel-mapping.md): ns > 1e17, µs > 1e14, ms > 1e11, else seconds.
        nanos = d switch
        {
            > 1e17 => v.TryGetInt64(out var l) ? l : (long)d,
            > 1e14 => (long)(d * 1_000),
            > 1e11 => (long)(d * 1_000_000),
            _ => (long)(d * 1_000_000_000),
        };
        return true;
    }

    private static bool TryLevel(JsonElement v, out Severity s)
    {
        s = 0;
        if (v.ValueKind == JsonValueKind.String) return SeverityExtensions.TryParse(v.GetString()!, out s);
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
        {
            // pino numeric levels: 10 trace, 20 debug, 30 info, 40 warn, 50 error, 60 fatal
            s = n switch { 10 => Severity.Trace, 20 => Severity.Debug, 30 => Severity.Info, 40 => Severity.Warn, 50 => Severity.Error, 60 => Severity.Fatal, >= 0 and <= 24 => (Severity)n, _ => 0 };
            return s != 0;
        }
        return false;
    }
}

/// <summary>Splits a byte stream into lines on '\n' without decoding (bytes kept exactly; '\r' remains part of the line).</summary>
public static class RawLines
{
    public static IEnumerable<(byte[] Line, bool HadNewline)> Split(Stream s, int maxLineBytes)
    {
        var buf = new byte[64 * 1024];
        var line = new MemoryStream();
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0)
        {
            var start = 0;
            for (var i = 0; i < n; i++)
            {
                if (buf[i] != (byte)'\n') continue;
                line.Write(buf, start, i - start);
                yield return (line.ToArray(), true);
                line.SetLength(0);
                start = i + 1;
            }
            line.Write(buf, start, n - start);
            if (line.Length > maxLineBytes) throw new InvalidDataException($"Line longer than {maxLineBytes} bytes.");
        }
        if (line.Length > 0) yield return (line.ToArray(), false);
    }
}

/// <summary>
/// Raw text import in byte-exact mode (P06-T04, spec §3.2 "Raw byte-exact"): each line's bytes are the record body,
/// regardless of encoding; the exporter reproduces the original file byte for byte. Timestamp/level are parsed
/// heuristically when a line starts with an RFC 3339 timestamp and/or a level word; ambiguity is flagged.
/// </summary>
public static class RawTextImporter
{
    public const string AttrFile = "log.file.name";
    public const string AttrNoFinalNewline = "latticelog.raw.no_final_newline";
    public const string AttrParse = "latticelog.parse";

    public static IEnumerable<LogRecord> Read(Stream input, string fileName, long importTimeNanos, ContextSnapshot? context = null, int maxLineBytes = 1 << 20)
    {
        context ??= new ContextSnapshot(ContextKind.Resource, null, [], null);
        var fileCtx = new ContextSnapshot(ContextKind.Custom, "file", [new(AttrFile, fileName)], context);
        long lastTs = importTimeNanos;
        foreach (var (bytes, hadNewline) in RawLines.Split(input, maxLineBytes))
        {
            var r = new LogRecord { ObservedUnixNanos = importTimeNanos, Context = fileCtx, Flags = RecordFlags.RawImport };
            r.SetBody(Value.From(bytes));
            var attrs = new List<LogAttribute>();
            var (ts, sev, status) = ParsePrefix(bytes);
            if (ts is { } t) { r.TimestampUnixNanos = t; lastTs = t; }
            else { r.TimestampUnixNanos = lastTs; attrs.Add(new(AttrParse, status)); }
            r.Severity = sev;
            if (!hadNewline) attrs.Add(new(AttrNoFinalNewline, true));
            r.Attributes = attrs.ToArray();
            yield return r;
        }
    }

    private static (long? Ts, Severity Sev, string Status) ParsePrefix(byte[] line)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(line, 0, Math.Min(line.Length, 128)); }
        catch (DecoderFallbackException) { text = Encoding.Latin1.GetString(line, 0, Math.Min(line.Length, 128)); }
        var parts = text.Split([' ', '\t', '[', ']', '|'], StringSplitOptions.RemoveEmptyEntries);
        long? ts = null;
        var sev = Severity.Unspecified;
        if (parts.Length > 0 && UnixTime.TryParseRfc3339(parts[0], out var t)) ts = t;
        else if (parts.Length > 1 && UnixTime.TryParseRfc3339(parts[0] + "T" + parts[1], out var t2)) ts = t2;
        foreach (var p in parts.Take(4))
            if (SeverityExtensions.TryParse(p.Trim(':'), out var s) && !char.IsDigit(p[0])) { sev = s; break; }
        return (ts, sev, ts is null ? "timestamp_not_found_inherited_previous" : "ok");
    }

    /// <summary>Writes records produced by <see cref="Read"/> back to the exact original bytes.</summary>
    public static void Export(IEnumerable<LogRecord> records, Stream output)
    {
        foreach (var r in records)
        {
            if (!r.Flags.HasFlag(RecordFlags.RawImport) || !r.HasBody || r.Body.Kind != ValueKind.Bytes)
                throw new InvalidOperationException($"Record {r.Sequence} is not a raw import; use JSONL export instead.");
            output.Write(r.Body.AsBytes());
            if (!r.TryGetAttribute(AttrNoFinalNewline, out _)) output.WriteByte((byte)'\n');
        }
    }
}
