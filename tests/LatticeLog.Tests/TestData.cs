using System.Text;

namespace LatticeLog.Tests;

/// <summary>Deterministic random generator of adversarial values and records for property-based tests.</summary>
public sealed class TestData(int seed)
{
    private readonly Random _r = new(seed);

    public static readonly string[] Words = ["alpha", "beta", "gamma", "inventory", "order", "user", "payment", "stock", "timeout", "retry", "ok"];

    public static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "latticelog-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    public int Next(int max) => _r.Next(max);
    public bool Chance(double p) => _r.NextDouble() < p;

    public string String()
    {
        switch (_r.Next(10))
        {
            case 0: return "";
            case 1: return "héllo 世界 🎉 \u0000 \"quote\" \\ \n\t";
            case 2: return "lone \uD800 surrogate";
            case 3: return "\uDC00 trailing low";
            case 4: return new string('x', _r.Next(300, 2000));
            case 5:
                {
                    var sb = new StringBuilder();
                    var n = _r.Next(1, 30);
                    for (var i = 0; i < n; i++) sb.Append((char)_r.Next(0x20, 0xD7FF));
                    return sb.ToString();
                }
            default: return Words[_r.Next(Words.Length)] + (_r.Next(3) == 0 ? _r.Next(1000).ToString() : "");
        }
    }

    public string Key() => Words[_r.Next(Words.Length)] + (_r.Next(2) == 0 ? "." + Words[_r.Next(Words.Length)] : "");

    public Value ScalarValue()
    {
        switch (_r.Next(14))
        {
            case 0: return Value.Null;
            case 1: return Value.From(_r.Next(2) == 0);
            case 2: return Value.From(_r.Next(4) switch { 0 => long.MinValue, 1 => long.MaxValue, 2 => 0, _ => _r.NextInt64(-1_000_000, 1_000_000) });
            case 3: return Value.From(_r.Next(3) switch { 0 => ulong.MaxValue, 1 => 0UL, _ => (ulong)_r.NextInt64() });
            case 4:
                return Value.From(_r.Next(8) switch
                {
                    0 => double.NaN,
                    1 => BitConverter.Int64BitsToDouble(0x7FF0_0000_0000_0001), // signalling NaN payload
                    2 => -0.0,
                    3 => double.PositiveInfinity,
                    4 => double.Epsilon,
                    5 => double.MaxValue,
                    6 => 1.0,
                    _ => _r.NextDouble() * 1e6 - 5e5,
                });
            case 5:
            case 6: return Value.From(String());
            case 7:
                {
                    var b = new byte[_r.Next(0, 64)];
                    _r.NextBytes(b);
                    return Value.From(b);
                }
            case 8: return Value.FromTimestamp(_r.Next(3) == 0 ? -1_234_567_890_123 : _r.NextInt64(0, 4_000_000_000_000_000_000));
            case 9: return Value.FromDuration(_r.NextInt64(-1_000_000_000, 1_000_000_000_000));
            case 10: return Value.From(new Guid(BitConverter.GetBytes(_r.NextInt64()).Concat(BitConverter.GetBytes(_r.NextInt64())).ToArray()));
            case 11: return Value.From(_r.Next(3) switch { 0 => decimal.MaxValue, 1 => -0.000123400m, _ => (decimal)_r.NextDouble() * 1000m });
            default: return Value.From(_r.Next(-50, 50));
        }
    }

    public Value AnyValue(int depth = 0)
    {
        if (depth < 3 && _r.Next(8) == 0)
        {
            var n = _r.Next(0, 5);
            return Value.From(Enumerable.Range(0, n).Select(_ => AnyValue(depth + 1)).ToArray());
        }
        if (depth < 3 && _r.Next(8) == 0)
        {
            var n = _r.Next(0, 5);
            return Value.From(Enumerable.Range(0, n).Select(_ => new LogAttribute(Key(), AnyValue(depth + 1))).ToArray());
        }
        return ScalarValue();
    }

    public ExceptionInfo Exception(int depth = 0)
    {
        var inner = depth < 2 && Chance(0.3) ? new[] { Exception(depth + 1) } : [];
        return new ExceptionInfo("System." + Words[_r.Next(Words.Length)] + "Exception", Chance(0.9) ? String() : null,
            Chance(0.7) ? "   at Foo.Bar()\n   at Baz.Qux()" : null, inner);
    }

    public EventDescriptor Descriptor(uint id)
    {
        var n = _r.Next(0, 6);
        var fields = new List<EventField>();
        var used = new HashSet<string>();
        for (var i = 0; i < n; i++)
        {
            var k = Key();
            if (!used.Add(k)) continue;
            var kind = (new[] { ValueKind.Int64, ValueKind.String, ValueKind.Double, ValueKind.Bool, ValueKind.Uuid, ValueKind.Timestamp, ValueKind.Decimal, ValueKind.Bytes })[_r.Next(8)];
            fields.Add(new EventField(k, kind, Chance(0.1) ? Sensitivity.Personal : Sensitivity.None, Chance(0.3) ? "desc " + k : null));
        }
        return new EventDescriptor(id, "event." + Words[_r.Next(Words.Length)] + "." + id, (ushort)_r.Next(1, 4), (Severity)_r.Next(1, 25),
            Chance(0.5) ? "Template {" + (fields.Count > 0 ? fields[0].Name : "x") + "}" : null, fields, Chance(0.3) ? "described" : null);
    }

    public Value ValueOfKind(ValueKind k) => k switch
    {
        ValueKind.Int64 => Value.From(_r.NextInt64()),
        ValueKind.String => Value.From(String()),
        ValueKind.Double => Value.From(_r.NextDouble()),
        ValueKind.Bool => Value.From(Chance(0.5)),
        ValueKind.Uuid => Value.From(Guid.NewGuid()),
        ValueKind.Timestamp => Value.FromTimestamp(_r.NextInt64()),
        ValueKind.Decimal => Value.From((decimal)_r.Next()),
        ValueKind.Bytes => Value.From(new byte[] { 1, 2, 3 }),
        _ => Value.Null,
    };

    public List<LogRecord> Records(int count, int schemaCount = 5, int contextCount = 4, long startTs = 1_763_400_000_000_000_000)
    {
        var schemas = Enumerable.Range(1, schemaCount).Select(i => Descriptor((uint)(1000 + i))).ToList();
        var root = new ContextSnapshot(ContextKind.Resource, null, [new("service.name", "svc-" + _r.Next(3)), new("host.name", "h1")], null);
        var contexts = new List<ContextSnapshot> { root };
        for (var i = 0; i < contextCount; i++)
            contexts.Add(new ContextSnapshot((ContextKind)_r.Next(0, 5), Chance(0.5) ? "scope" + i : null,
                [new LogAttribute(Key(), AnyValue())], contexts[_r.Next(contexts.Count)]));
        var traces = Enumerable.Range(0, 6).Select(_ => new TraceId((ulong)_r.NextInt64() | 1, (ulong)_r.NextInt64())).ToList();
        var list = new List<LogRecord>(count);
        var ts = startTs;
        for (var i = 0; i < count; i++)
        {
            ts += _r.Next(0, 3) == 0 ? -_r.Next(0, 1000) : _r.Next(0, 5_000_000);
            var r = new LogRecord { TimestampUnixNanos = ts, Severity = (Severity)_r.Next(0, 25) };
            if (Chance(0.6))
            {
                var s = schemas[_r.Next(schemas.Count)];
                r.Event = s;
                r.Severity = s.Severity;
                if (Chance(0.85))
                    r.Attributes = s.Fields.Select(f => new LogAttribute(f.Name, Chance(0.9) ? ValueOfKind(f.Kind) : ScalarValue())).ToArray();
                else
                    r.Attributes = Enumerable.Range(0, _r.Next(0, 4)).Select(_ => new LogAttribute(Key(), AnyValue())).ToArray();
            }
            else
            {
                if (Chance(0.3)) r.EventId = (uint)_r.Next(1, 100);
                if (Chance(0.5)) r.EventName = "free." + Words[_r.Next(Words.Length)];
                r.Attributes = Enumerable.Range(0, _r.Next(0, 6)).Select(_ => new LogAttribute(Key(), AnyValue())).ToArray();
            }
            if (Chance(0.5)) r.SetBody(AnyValue());
            if (Chance(0.2)) r.ObservedUnixNanos = ts + 1000;
            if (Chance(0.3)) r.MonotonicNanos = _r.NextInt64(1, long.MaxValue);
            if (Chance(0.6))
            {
                r.TraceId = traces[_r.Next(traces.Count)];
                if (Chance(0.8)) r.SpanId = new SpanId((ulong)_r.NextInt64() | 1);
                if (Chance(0.5)) r.TraceFlags = 1;
            }
            if (Chance(0.3)) r.OperationId = "op-" + _r.Next(10);
            if (Chance(0.1)) r.ParentOperationId = "op-" + _r.Next(10);
            if (Chance(0.1)) r.CorrelationId = "corr-" + _r.Next(5);
            if (Chance(0.1)) r.Exception = Exception();
            if (Chance(0.9)) r.Context = contexts[_r.Next(contexts.Count)];
            if (Chance(0.05)) r.Flags = RecordFlags.Redacted;
            // Context inheritance pattern (consecutive records sharing the same context tuple).
            if (i > 0 && Chance(0.3))
            {
                var p = list[^1];
                r.TraceId = p.TraceId; r.SpanId = p.SpanId; r.TraceFlags = p.TraceFlags; r.OperationId = p.OperationId;
                r.ParentOperationId = p.ParentOperationId; r.CorrelationId = p.CorrelationId; r.Context = p.Context;
            }
            list.Add(r);
        }
        return list;
    }

    public static void AssignSequences(IList<LogRecord> records, ulong first = 1)
    {
        for (var i = 0; i < records.Count; i++) records[i].Sequence = first + (ulong)i;
    }

    public static string Describe(LogRecord a, LogRecord b)
    {
        var diffs = new List<string>();
        if (a.TimestampUnixNanos != b.TimestampUnixNanos) diffs.Add("ts");
        if (a.Severity != b.Severity) diffs.Add("severity");
        if (!Equals(a.Event, b.Event)) diffs.Add("event");
        if (a.EventId != b.EventId) diffs.Add("eventid");
        if (a.EventName != b.EventName) diffs.Add("eventname");
        if (a.HasBody != b.HasBody || !a.Body.Equals(b.Body)) diffs.Add($"body {a.Body} vs {b.Body}");
        if (!a.Attributes.AsSpan().SequenceEqual(b.Attributes)) diffs.Add($"attrs [{string.Join(",", a.Attributes)}] vs [{string.Join(",", b.Attributes)}]");
        if (a.TraceId != b.TraceId) diffs.Add("trace");
        if (a.SpanId != b.SpanId) diffs.Add("span");
        if (a.OperationId != b.OperationId) diffs.Add("op");
        if (!Equals(a.Exception, b.Exception)) diffs.Add("exception");
        if (!(a.Context?.ChainEquals(b.Context) ?? b.Context is null)) diffs.Add("context");
        if (a.Flags != b.Flags) diffs.Add("flags");
        if (a.Sequence != b.Sequence) diffs.Add("seq");
        if (a.ObservedUnixNanos != b.ObservedUnixNanos) diffs.Add("observed");
        if (a.MonotonicNanos != b.MonotonicNanos) diffs.Add("mono");
        return string.Join("; ", diffs);
    }
}
