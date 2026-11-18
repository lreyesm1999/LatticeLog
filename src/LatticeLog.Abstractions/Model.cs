using System.Buffers.Binary;
using System.Globalization;

namespace LatticeLog;

/// <summary>
/// Severity numbers follow the OpenTelemetry Logs Data Model (1..24). 0 means unspecified.
/// The named members are the first number of each OTel range.
/// </summary>
public enum Severity : byte
{
    Unspecified = 0,
    Trace = 1,
    Debug = 5,
    Info = 9,
    Warn = 13,
    Error = 17,
    Fatal = 21,
}

public static class SeverityExtensions
{
    public const byte MaxNumber = 24;

    /// <summary>Short text of the OTel range ("TRACE".."FATAL"), including the sub level, e.g. ERROR2.</summary>
    public static string ToText(this Severity s)
    {
        var n = (int)s;
        if (n == 0) return "UNSPECIFIED";
        if (n > MaxNumber) return n.ToString(CultureInfo.InvariantCulture);
        string[] names = ["TRACE", "DEBUG", "INFO", "WARN", "ERROR", "FATAL"];
        var bucket = (n - 1) / 4;
        var sub = (n - 1) % 4;
        return sub == 0 ? names[bucket] : names[bucket] + (sub + 1).ToString(CultureInfo.InvariantCulture);
    }

    public static bool TryParse(string text, out Severity severity)
    {
        severity = Severity.Unspecified;
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n is >= 0 and <= MaxNumber)
        {
            severity = (Severity)n; return true;
        }
        var t = text.Trim().ToUpperInvariant();
        var sub = 1;
        if (t.Length > 1 && char.IsAsciiDigit(t[^1])) { sub = t[^1] - '0'; t = t[..^1]; }
        if (sub is < 1 or > 4) return false;
        int baseNum = t switch
        {
            "TRACE" or "TRC" or "VERBOSE" => 1,
            "DEBUG" or "DBG" => 5,
            "INFO" or "INF" or "INFORMATION" => 9,
            "WARN" or "WRN" or "WARNING" => 13,
            "ERROR" or "ERR" => 17,
            "FATAL" or "FTL" or "CRITICAL" or "CRIT" => 21,
            _ => 0,
        };
        if (baseNum == 0) return false;
        severity = (Severity)(baseNum + sub - 1);
        return true;
    }
}

/// <summary>W3C Trace Context trace-id (16 bytes). All-zero is invalid/absent.</summary>
public readonly record struct TraceId(ulong High, ulong Low)
{
    public static readonly TraceId Empty = default;
    public bool IsEmpty => High == 0 && Low == 0;
    public void WriteBytes(Span<byte> dest)
    {
        BinaryPrimitives.WriteUInt64BigEndian(dest, High);
        BinaryPrimitives.WriteUInt64BigEndian(dest[8..], Low);
    }
    public static TraceId FromBytes(ReadOnlySpan<byte> b) => new(BinaryPrimitives.ReadUInt64BigEndian(b), BinaryPrimitives.ReadUInt64BigEndian(b[8..]));
    public override string ToString() => High.ToString("x16", CultureInfo.InvariantCulture) + Low.ToString("x16", CultureInfo.InvariantCulture);
    public static bool TryParse(string? hex, out TraceId id)
    {
        id = default;
        if (hex is null || hex.Length != 32) return false;
        if (!ulong.TryParse(hex.AsSpan(0, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h)) return false;
        if (!ulong.TryParse(hex.AsSpan(16, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var l)) return false;
        id = new TraceId(h, l);
        return true;
    }
    public static TraceId Parse(string hex) => TryParse(hex, out var id) ? id : throw new FormatException("trace_id must be 32 hex characters");
}

/// <summary>W3C Trace Context span/parent-id (8 bytes). Zero is invalid/absent.</summary>
public readonly record struct SpanId(ulong Value)
{
    public static readonly SpanId Empty = default;
    public bool IsEmpty => Value == 0;
    public override string ToString() => Value.ToString("x16", CultureInfo.InvariantCulture);
    public static bool TryParse(string? hex, out SpanId id)
    {
        id = default;
        if (hex is null || hex.Length != 16) return false;
        if (!ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        id = new SpanId(v);
        return true;
    }
    public static SpanId Parse(string hex) => TryParse(hex, out var id) ? id : throw new FormatException("span_id must be 16 hex characters");
}

/// <summary>Language independent exception description (specs/event-model.md §7).</summary>
public sealed record ExceptionInfo(string Type, string? Message, string? StackTrace, IReadOnlyList<ExceptionInfo> Inner)
{
    public const int MaxDepth = 8;
    public const int MaxInnerPerLevel = 16;

    public static ExceptionInfo FromException(Exception ex) => From(ex, 0);

    private static ExceptionInfo From(Exception ex, int depth)
    {
        var inner = new List<ExceptionInfo>();
        if (depth < MaxDepth)
        {
            if (ex is AggregateException agg)
            {
                foreach (var e in agg.InnerExceptions.Take(MaxInnerPerLevel)) inner.Add(From(e, depth + 1));
            }
            else if (ex.InnerException is not null)
            {
                inner.Add(From(ex.InnerException, depth + 1));
            }
        }
        return new ExceptionInfo(ex.GetType().FullName ?? ex.GetType().Name, ex.Message, ex.StackTrace, inner);
    }

    public bool Equals(ExceptionInfo? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Type == other.Type && Message == other.Message && StackTrace == other.StackTrace && Inner.SequenceEqual(other.Inner);
    }

    public override int GetHashCode() => HashCode.Combine(Type, Message, StackTrace, Inner.Count);
}

/// <summary>Kind of an immutable context snapshot (specs/context-model.md).</summary>
public enum ContextKind : byte
{
    Resource = 0,
    Scope = 1,
    Operation = 2,
    Request = 3,
    Custom = 4,
}

/// <summary>
/// Immutable, versionless snapshot of inherited properties. Snapshots form a chain to the
/// resource root. A record references only its innermost snapshot; inherited attributes are
/// never copied into the record, so provenance (own vs inherited) is preserved.
/// </summary>
public sealed class ContextSnapshot
{
    public ContextSnapshot(ContextKind kind, string? name, IReadOnlyList<LogAttribute> attributes, ContextSnapshot? parent)
    {
        Kind = kind;
        Name = name;
        Attributes = attributes;
        Parent = parent;
        Depth = parent is null ? 0 : parent.Depth + 1;
        if (Depth > MaxDepth) throw new InvalidOperationException($"Context nesting exceeds {MaxDepth}.");
    }

    public const int MaxDepth = 64;
    public ContextKind Kind { get; }
    public string? Name { get; }
    public IReadOnlyList<LogAttribute> Attributes { get; }
    public ContextSnapshot? Parent { get; }
    public int Depth { get; }

    /// <summary>Find an attribute walking from this snapshot towards the root (innermost wins).</summary>
    public bool TryGet(string key, out Value value)
    {
        for (var c = this; c is not null; c = c.Parent)
        {
            for (var i = c.Attributes.Count - 1; i >= 0; i--)
            {
                if (c.Attributes[i].Key == key) { value = c.Attributes[i].Value; return true; }
            }
        }
        value = default;
        return false;
    }

    /// <summary>Flattened view, root first, inner values overriding outer ones.</summary>
    public IReadOnlyList<LogAttribute> Flatten()
    {
        var chain = new Stack<ContextSnapshot>();
        for (var c = this; c is not null; c = c.Parent) chain.Push(c);
        var order = new List<string>();
        var map = new Dictionary<string, Value>(StringComparer.Ordinal);
        while (chain.Count > 0)
        {
            foreach (var a in chain.Pop().Attributes)
            {
                if (!map.ContainsKey(a.Key)) order.Add(a.Key);
                map[a.Key] = a.Value;
            }
        }
        return order.Select(k => new LogAttribute(k, map[k])).ToArray();
    }

    /// <summary>Structural equality of the whole chain.</summary>
    public bool ChainEquals(ContextSnapshot? other)
    {
        var a = this; var b = other;
        while (a is not null && b is not null)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a.Kind != b.Kind || a.Name != b.Name || !a.Attributes.SequenceEqual(b.Attributes)) return false;
            a = a.Parent; b = b.Parent;
        }
        return a is null && b is null;
    }

    public string? ServiceName => TryGet("service.name", out var v) && v.Kind == ValueKind.String ? v.AsString() : null;
}

/// <summary>Classification of a field for capture and output policies (P01-T14, P18-T02).</summary>
public enum Sensitivity : byte
{
    None = 0,
    /// <summary>Personal data: kept by default, redactable in outputs.</summary>
    Personal = 1,
    /// <summary>Secret: never persisted by default (redacted in the producer).</summary>
    Secret = 2,
}

public sealed record EventField(string Name, ValueKind Kind, Sensitivity Sensitivity = Sensitivity.None, string? Description = null);

/// <summary>Stable description of an event type (P01-T04/T06). Identity is (Id, Version).</summary>
public sealed record EventDescriptor(
    uint Id,
    string Name,
    ushort Version,
    Severity Severity,
    string? MessageTemplate,
    IReadOnlyList<EventField> Fields,
    string? Description = null)
{
    public bool Equals(EventDescriptor? other)
        => other is not null && Id == other.Id && Name == other.Name && Version == other.Version && Severity == other.Severity
           && MessageTemplate == other.MessageTemplate && Description == other.Description && Fields.SequenceEqual(other.Fields);

    public override int GetHashCode() => HashCode.Combine(Id, Name, Version);
}

[Flags]
public enum RecordFlags : byte
{
    None = 0,
    /// <summary>At least one value was redacted by the producer before persistence.</summary>
    Redacted = 1,
    /// <summary>Body holds raw imported bytes preserved byte-exact (P06-T04).</summary>
    RawImport = 2,
    /// <summary>Record was produced by an adapter that could not preserve full fidelity (P13-T09).</summary>
    LossyImport = 4,
}

/// <summary>
/// Logical log record (specs/event-model.md). Independent of any binary representation.
/// </summary>
public sealed class LogRecord : IEquatable<LogRecord>
{
    public static readonly LogAttribute[] NoAttributes = [];

    /// <summary>Wall clock time of the event, Unix epoch nanoseconds (UTC).</summary>
    public long TimestampUnixNanos { get; set; }
    /// <summary>When the record was observed by the collector/SDK. 0 = absent.</summary>
    public long ObservedUnixNanos { get; set; }
    /// <summary>Optional local monotonic timestamp (nanoseconds, producer local, not comparable across processes). 0 = absent.</summary>
    public long MonotonicNanos { get; set; }
    public Severity Severity { get; set; }
    /// <summary>Typed event descriptor, if the record was produced from a contract.</summary>
    public EventDescriptor? Event { get; set; }
    /// <summary>Event id when no descriptor is present (e.g. bridged ILogger EventId). 0 = absent.</summary>
    public uint EventId { get; set; }
    /// <summary>Event name when no descriptor is present.</summary>
    public string? EventName { get; set; }
    public bool HasBody { get; set; }
    public Value Body { get; set; }
    public LogAttribute[] Attributes { get; set; } = NoAttributes;
    public TraceId TraceId { get; set; }
    public SpanId SpanId { get; set; }
    public byte TraceFlags { get; set; }
    public string? OperationId { get; set; }
    public string? ParentOperationId { get; set; }
    public string? CorrelationId { get; set; }
    public ExceptionInfo? Exception { get; set; }
    public ContextSnapshot? Context { get; set; }
    public RecordFlags Flags { get; set; }
    /// <summary>Store-wide monotonically increasing sequence number assigned by the writer (P04-T06). 0 = not yet assigned.</summary>
    public ulong Sequence { get; set; }

    public uint EffectiveEventId => Event?.Id ?? EventId;
    public string? EffectiveEventName => Event?.Name ?? EventName;
    public string? ServiceName => Context?.ServiceName;

    public bool TryGetAttribute(string key, out Value value)
    {
        foreach (var a in Attributes)
        {
            if (a.Key == key) { value = a.Value; return true; }
        }
        value = default;
        return false;
    }

    public void SetBody(Value body)
    {
        Body = body;
        HasBody = true;
    }

    public bool Equals(LogRecord? o)
    {
        if (o is null) return false;
        if (ReferenceEquals(this, o)) return true;
        return TimestampUnixNanos == o.TimestampUnixNanos
            && ObservedUnixNanos == o.ObservedUnixNanos
            && MonotonicNanos == o.MonotonicNanos
            && Severity == o.Severity
            && Equals(Event, o.Event)
            && EventId == o.EventId
            && EventName == o.EventName
            && HasBody == o.HasBody
            && Body.Equals(o.Body)
            && Attributes.AsSpan().SequenceEqual(o.Attributes)
            && TraceId == o.TraceId
            && SpanId == o.SpanId
            && TraceFlags == o.TraceFlags
            && OperationId == o.OperationId
            && ParentOperationId == o.ParentOperationId
            && CorrelationId == o.CorrelationId
            && Equals(Exception, o.Exception)
            && (Context is null ? o.Context is null : Context.ChainEquals(o.Context))
            && Flags == o.Flags
            && Sequence == o.Sequence;
    }

    public override bool Equals(object? obj) => Equals(obj as LogRecord);
    public override int GetHashCode() => HashCode.Combine(TimestampUnixNanos, Sequence, Severity, EffectiveEventId);

    public LogRecord ShallowClone() => (LogRecord)MemberwiseClone();
}
