using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace LatticeLog.Storage.Json;

public enum JsonValueStyle
{
    /// <summary>Type-preserving: decode(encode(x)) == x (P13-T10 exit format).</summary>
    Lossless,
    /// <summary>Compact, readable rendering for humans and external tools (P12-T01). Not guaranteed reversible.</summary>
    Friendly,
}

/// <summary>Canonical JSON rendering of values and records (specs/evidence-protocol.md §3, specs/jsonl-exchange.md).</summary>
public static class CanonicalJson
{
    public static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
        SkipValidation = false,
    };

    public static string ReferenceFor(string storeId, ulong sequence) => $"llog://{storeId}/e/{sequence}";

    public static bool TryParseReference(string reference, out string storeId, out ulong sequence)
    {
        storeId = ""; sequence = 0;
        const string prefix = "llog://";
        if (!reference.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var rest = reference[prefix.Length..];
        var parts = rest.Split('/');
        if (parts.Length != 3 || parts[1] != "e" || parts[0].Length == 0) return false;
        if (!ulong.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out sequence) || sequence == 0) return false;
        storeId = parts[0];
        return true;
    }

    public static void WriteValue(Utf8JsonWriter w, in Value v, JsonValueStyle style, int maxStringChars = int.MaxValue)
    {
        switch (v.Kind)
        {
            case ValueKind.Null: w.WriteNullValue(); break;
            case ValueKind.Bool: w.WriteBooleanValue(v.AsBool()); break;
            case ValueKind.Int64: w.WriteNumberValue(v.AsInt64()); break;
            case ValueKind.UInt64:
                if (style == JsonValueStyle.Friendly) w.WriteNumberValue(v.AsUInt64());
                else { w.WriteStartObject(); w.WriteString("$u64", v.AsUInt64().ToString(CultureInfo.InvariantCulture)); w.WriteEndObject(); }
                break;
            case ValueKind.Double:
                {
                    var d = v.AsDouble();
                    if (double.IsNaN(d))
                    {
                        if (style == JsonValueStyle.Friendly) w.WriteStringValue("NaN");
                        else { w.WriteStartObject(); w.WriteString("$f64bits", v.AsDoubleBits().ToString("x16", CultureInfo.InvariantCulture)); w.WriteEndObject(); }
                    }
                    else if (double.IsInfinity(d))
                    {
                        if (style == JsonValueStyle.Friendly) w.WriteStringValue(d > 0 ? "Infinity" : "-Infinity");
                        else { w.WriteStartObject(); w.WriteString("$f64", d > 0 ? "Infinity" : "-Infinity"); w.WriteEndObject(); }
                    }
                    else
                    {
                        var s = d.ToString("R", CultureInfo.InvariantCulture);
                        if (style == JsonValueStyle.Lossless && !s.Contains('.') && !s.Contains('E') && !s.Contains('e')) s += ".0";
                        if (s == "-0" || (style == JsonValueStyle.Lossless && s == "-0.0")) s = "-0.0";
                        w.WriteRawValue(s, skipInputValidation: true);
                    }
                    break;
                }
            case ValueKind.String:
                {
                    var s = v.AsString();
                    if (Value.HasLoneSurrogate(s))
                    {
                        var bytes = new byte[s.Length * 2];
                        for (var i = 0; i < s.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), s[i]);
                        w.WriteStartObject(); w.WriteString("$utf16le", Convert.ToBase64String(bytes)); w.WriteEndObject();
                    }
                    else if (s.Length > maxStringChars)
                    {
                        w.WriteStringValue(s[..maxStringChars] + $"…[truncated {s.Length - maxStringChars} chars]");
                    }
                    else w.WriteStringValue(s);
                    break;
                }
            case ValueKind.Bytes:
                w.WriteStartObject(); w.WriteBase64String("$bytes", v.AsBytes()); w.WriteEndObject();
                break;
            case ValueKind.Array:
                w.WriteStartArray();
                foreach (var x in v.AsArray()) WriteValue(w, x, style, maxStringChars);
                w.WriteEndArray();
                break;
            case ValueKind.Map:
                if (style == JsonValueStyle.Friendly && !HasDuplicateKeys(v.AsMap()))
                {
                    w.WriteStartObject();
                    foreach (var kv in v.AsMap()) { w.WritePropertyName(kv.Key); WriteValue(w, kv.Value, style, maxStringChars); }
                    w.WriteEndObject();
                }
                else
                {
                    w.WriteStartObject();
                    w.WritePropertyName("$map");
                    WritePairs(w, v.AsMap(), style, maxStringChars);
                    w.WriteEndObject();
                }
                break;
            case ValueKind.Timestamp:
                if (style == JsonValueStyle.Friendly) w.WriteStringValue(UnixTime.FormatRfc3339(v.AsTimestampUnixNanos()));
                else { w.WriteStartObject(); w.WriteString("$ts", UnixTime.FormatRfc3339(v.AsTimestampUnixNanos())); w.WriteEndObject(); }
                break;
            case ValueKind.Duration:
                w.WriteStartObject(); w.WriteString("$dur_ns", v.AsDurationNanos().ToString(CultureInfo.InvariantCulture)); w.WriteEndObject();
                break;
            case ValueKind.Uuid:
                if (style == JsonValueStyle.Friendly) w.WriteStringValue(v.AsGuid().ToString("D"));
                else { w.WriteStartObject(); w.WriteString("$uuid", v.AsGuid().ToString("D")); w.WriteEndObject(); }
                break;
            case ValueKind.Decimal:
                if (style == JsonValueStyle.Friendly) w.WriteRawValue(v.AsDecimal().ToString(CultureInfo.InvariantCulture), skipInputValidation: true);
                else { w.WriteStartObject(); w.WriteString("$dec", v.AsDecimal().ToString(CultureInfo.InvariantCulture)); w.WriteEndObject(); }
                break;
        }
    }

    public static bool HasDuplicateKeys(IReadOnlyList<LogAttribute> attrs)
    {
        if (attrs.Count < 2) return false;
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in attrs) if (!set.Add(a.Key)) return true;
        return false;
    }

    public static void WritePairs(Utf8JsonWriter w, IReadOnlyList<LogAttribute> attrs, JsonValueStyle style, int maxStringChars = int.MaxValue)
    {
        w.WriteStartArray();
        foreach (var a in attrs)
        {
            w.WriteStartArray();
            w.WriteStringValue(a.Key);
            WriteValue(w, a.Value, style, maxStringChars);
            w.WriteEndArray();
        }
        w.WriteEndArray();
    }

    public static Value ReadValue(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Null: return Value.Null;
            case JsonValueKind.True: return Value.From(true);
            case JsonValueKind.False: return Value.From(false);
            case JsonValueKind.String: return Value.From(e.GetString());
            case JsonValueKind.Number:
                {
                    var raw = e.GetRawText();
                    if (raw.IndexOfAny(['.', 'e', 'E']) < 0 && e.TryGetInt64(out var l)) return Value.From(l);
                    if (raw.IndexOfAny(['.', 'e', 'E']) < 0 && e.TryGetUInt64(out var u)) return Value.From(u);
                    return Value.From(double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture));
                }
            case JsonValueKind.Array:
                return Value.From(e.EnumerateArray().Select(ReadValue).ToArray());
            case JsonValueKind.Object:
                {
                    using var it = e.EnumerateObject();
                    var props = e.EnumerateObject().ToList();
                    if (props.Count == 1 && props[0].Name.StartsWith('$'))
                    {
                        var p = props[0];
                        switch (p.Name)
                        {
                            case "$u64": return Value.From(ulong.Parse(p.Value.GetString()!, CultureInfo.InvariantCulture));
                            case "$f64bits": return Value.From(BitConverter.Int64BitsToDouble(long.Parse(p.Value.GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
                            case "$f64": return Value.From(p.Value.GetString() == "Infinity" ? double.PositiveInfinity : double.NegativeInfinity);
                            case "$bytes": return Value.From(p.Value.GetBytesFromBase64());
                            case "$ts": return UnixTime.TryParseRfc3339(p.Value.GetString()!, out var ts) ? Value.FromTimestamp(ts) : throw new FormatException("Bad $ts");
                            case "$dur_ns": return Value.FromDuration(long.Parse(p.Value.GetString()!, CultureInfo.InvariantCulture));
                            case "$uuid": return Value.From(Guid.Parse(p.Value.GetString()!));
                            case "$dec": return Value.From(decimal.Parse(p.Value.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture));
                            case "$utf16le":
                                {
                                    var b = p.Value.GetBytesFromBase64();
                                    var chars = new char[b.Length / 2];
                                    for (var i = 0; i < chars.Length; i++) chars[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(i * 2));
                                    return Value.From(new string(chars));
                                }
                            case "$map": return Value.From(ReadPairs(p.Value));
                        }
                    }
                    return Value.From(props.Select(p => new LogAttribute(p.Name, ReadValue(p.Value))).ToArray());
                }
            default: throw new FormatException($"Unsupported JSON value {e.ValueKind}.");
        }
    }

    public static LogAttribute[] ReadPairs(JsonElement arr)
    {
        var list = new List<LogAttribute>();
        foreach (var pair in arr.EnumerateArray())
        {
            if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() != 2) throw new FormatException("Attribute pair must be [key, value].");
            list.Add(new LogAttribute(pair[0].GetString() ?? throw new FormatException("Attribute key must be a string."), ReadValue(pair[1])));
        }
        return [.. list];
    }

    public static void WriteException(Utf8JsonWriter w, ExceptionInfo ex, int maxStringChars = int.MaxValue)
    {
        w.WriteStartObject();
        w.WriteString("type", ex.Type);
        if (ex.Message is not null) { w.WritePropertyName("message"); WriteValue(w, Value.From(ex.Message), JsonValueStyle.Lossless, maxStringChars); }
        if (ex.StackTrace is not null) { w.WritePropertyName("stack"); WriteValue(w, Value.From(ex.StackTrace), JsonValueStyle.Lossless, maxStringChars); }
        if (ex.Inner.Count > 0)
        {
            w.WriteStartArray("inner");
            foreach (var i in ex.Inner) WriteException(w, i, maxStringChars);
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    public static ExceptionInfo ReadException(JsonElement e)
    {
        string? Str(string name) => e.TryGetProperty(name, out var p) ? ReadValue(p).AsString() : null;
        var inner = e.TryGetProperty("inner", out var arr) ? arr.EnumerateArray().Select(ReadException).ToArray() : [];
        return new ExceptionInfo(e.GetProperty("type").GetString()!, Str("message"), Str("stack"), inner);
    }

    /// <summary>Friendly single-record JSON used by query/evidence outputs.</summary>
    public static void WriteFriendlyRecord(Utf8JsonWriter w, LogRecord r, string storeId, FriendlyOptions o)
    {
        w.WriteStartObject();
        w.WriteString("ref", ReferenceFor(storeId, r.Sequence));
        w.WriteString("time", UnixTime.FormatRfc3339(r.TimestampUnixNanos));
        // Arrival/collection time when known (importers, collector): event time and arrival time are different facts.
        if (r.ObservedUnixNanos != 0) w.WriteString("observed_time", UnixTime.FormatRfc3339(r.ObservedUnixNanos));
        w.WriteString("severity", r.Severity.ToText());
        if (r.EffectiveEventName is { } en) w.WriteString("event", en);
        if (r.EffectiveEventId != 0) w.WriteNumber("event_id", r.EffectiveEventId);
        if (r.ServiceName is { } svc) w.WriteString("service", svc);
        if (r.Event?.MessageTemplate is { } tmpl && o.RenderMessage) w.WriteString("message", RenderTemplate(tmpl, r.Attributes, o));
        if (r.HasBody) { w.WritePropertyName("body"); WriteValue(w, o.Redact(null, "body", r.Body), JsonValueStyle.Friendly, o.MaxStringChars); }
        if (r.Attributes.Length > 0)
        {
            if (HasDuplicateKeys(r.Attributes))
            {
                w.WritePropertyName("attributes");
                WritePairs(w, r.Attributes.Select(a => a with { Value = o.Redact(r, a.Key, a.Value) }).ToArray(), JsonValueStyle.Friendly, o.MaxStringChars);
            }
            else
            {
                w.WriteStartObject("attributes");
                foreach (var a in r.Attributes) { w.WritePropertyName(a.Key); WriteValue(w, o.Redact(r, a.Key, a.Value), JsonValueStyle.Friendly, o.MaxStringChars); }
                w.WriteEndObject();
            }
        }
        if (!r.TraceId.IsEmpty) w.WriteString("trace_id", r.TraceId.ToString());
        if (!r.SpanId.IsEmpty) w.WriteString("span_id", r.SpanId.ToString());
        if (r.OperationId is not null) w.WriteString("operation_id", r.OperationId);
        if (r.ParentOperationId is not null) w.WriteString("parent_operation_id", r.ParentOperationId);
        if (r.CorrelationId is not null) w.WriteString("correlation_id", r.CorrelationId);
        if (r.Exception is not null) { w.WritePropertyName("exception"); WriteException(w, r.Exception, o.MaxStringChars); }
        if (o.IncludeContext && r.Context is not null)
        {
            w.WriteStartObject("context");
            foreach (var a in r.Context.Flatten()) { w.WritePropertyName(a.Key); WriteValue(w, o.Redact(null, a.Key, a.Value), JsonValueStyle.Friendly, o.MaxStringChars); }
            w.WriteEndObject();
        }
        if (r.Flags != 0) w.WriteString("flags", r.Flags.ToString());
        w.WriteEndObject();
    }

    public static string RenderTemplate(string template, IReadOnlyList<LogAttribute> attrs, FriendlyOptions? o = null)
    {
        var sb = new StringBuilder(template.Length + 32);
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c == '{' && i + 1 < template.Length && template[i + 1] == '{') { sb.Append('{'); i++; continue; }
            if (c == '}' && i + 1 < template.Length && template[i + 1] == '}') { sb.Append('}'); i++; continue; }
            if (c == '{')
            {
                var end = template.IndexOf('}', i + 1);
                if (end < 0) { sb.Append(template, i, template.Length - i); break; }
                var name = template[(i + 1)..end];
                var colon = name.IndexOf(':');
                if (colon >= 0) name = name[..colon];
                var found = false;
                foreach (var a in attrs)
                {
                    if (a.Key == name)
                    {
                        var v = o is null ? a.Value : o.Redact(null, a.Key, a.Value);
                        sb.Append(v.ToString());
                        found = true;
                        break;
                    }
                }
                if (!found) sb.Append('{').Append(name).Append('}');
                i = end;
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    public static string ToFriendlyJson(LogRecord r, string storeId, FriendlyOptions? o = null)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, WriterOptions)) WriteFriendlyRecord(w, r, storeId, o ?? FriendlyOptions.Default);
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}

/// <summary>Output options shared by CLI, evidence and MCP (redaction always applies to every output, P12-T15).</summary>
public sealed class FriendlyOptions
{
    public static readonly FriendlyOptions Default = new();
    public bool IncludeContext { get; init; } = true;
    public bool RenderMessage { get; init; } = true;
    public int MaxStringChars { get; init; } = 4096;
    public RedactionPolicy Redaction { get; init; } = RedactionPolicy.Default;

    public Value Redact(LogRecord? r, string key, Value v) => Redaction.Apply(r, key, v);
}

/// <summary>
/// Output redaction (defense in depth; producer-side redaction is the primary control, P18-T03).
/// Keys matching sensitive patterns and fields declared Secret/Personal in the schema are masked.
/// </summary>
public sealed class RedactionPolicy
{
    public static readonly RedactionPolicy Default = new();
    public static readonly RedactionPolicy None = new() { Enabled = false };
    public const string Mask = "[REDACTED]";

    public bool Enabled { get; init; } = true;
    public bool RedactPersonal { get; init; }
    public IReadOnlyList<string> SensitiveKeyFragments { get; init; } =
        ["password", "passwd", "secret", "token", "apikey", "api_key", "api-key", "authorization", "cookie", "set-cookie", "private_key", "credential", "session_id", "connectionstring", "connection_string"];

    // Keys repeat across records: cache the decision (bounded, thread-safe). Measured: removes the writer-thread
    // bottleneck of matching every key against every pattern (reports/benchmarks-hotpath.md).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _keyCache = new(StringComparer.Ordinal);
    private const int MaxCachedKeys = 16_384;

    public bool IsSensitiveKey(string key)
    {
        if (_keyCache.TryGetValue(key, out var cached)) return cached;
        var result = false;
        foreach (var f in SensitiveKeyFragments)
            if (key.Contains(f, StringComparison.OrdinalIgnoreCase)) { result = true; break; }
        if (_keyCache.Count < MaxCachedKeys) _keyCache.TryAdd(key, result);
        return result;
    }

    public Value Apply(LogRecord? r, string key, Value v)
    {
        if (!Enabled || v.IsNull) return v;
        if (IsSensitiveKey(key)) return Value.From(Mask);
        if (r?.Event is { } e && e.MaxFieldSensitivity != Sensitivity.None)
        {
            var fields = e.Fields;
            for (var i = 0; i < fields.Count; i++) // index loop: no enumerator allocation on the writer thread
            {
                var f = fields[i];
                if (f.Name != key) continue;
                if (f.Sensitivity == Sensitivity.Secret || (RedactPersonal && f.Sensitivity == Sensitivity.Personal)) return Value.From(Mask);
                break;
            }
        }
        if (v.Kind == ValueKind.Map)
        {
            var m = v.AsMap();
            LogAttribute[]? copy = null;
            for (var i = 0; i < m.Length; i++)
            {
                var nv = Apply(null, m[i].Key, m[i].Value);
                if (!nv.Equals(m[i].Value)) { copy ??= (LogAttribute[])m.Clone(); copy[i] = m[i] with { Value = nv }; }
            }
            return copy is null ? v : Value.From(copy);
        }
        return v;
    }
}
