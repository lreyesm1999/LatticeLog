using System.Globalization;
using LatticeLog.Storage.Format;

namespace LatticeLog.Query;

/// <summary>
/// Typed predicate evaluation (specs/query-language.md §4). A missing field makes every comparison false
/// (including !=); use EXISTS / IS NULL to test presence. CONTAINS/STARTSWITH are ordinal and case-insensitive.
/// </summary>
public static class Evaluator
{
    public static Func<LogRecord, bool> Compile(Expr e) => e switch
    {
        TrueExpr => static _ => true,
        AndExpr a => And(Compile(a.Left), Compile(a.Right)),
        OrExpr o => Or(Compile(o.Left), Compile(o.Right)),
        NotExpr n => Not(Compile(n.Inner)),
        CompareExpr c => r => Compare(r, c.Field, c.Op, c.Value),
        InExpr i => r => i.Values.Any(v => Compare(r, i.Field, CompareOp.Eq, v)),
        ExistsExpr x => r => TryGet(r, x.Field, out _),
        ContainsExpr c => r => TextMatch(r, c.Field, c.Needle, prefix: false),
        StartsWithExpr s => r => TextMatch(r, s.Field, s.Prefix, prefix: true),
        _ => throw new NotSupportedException(e.GetType().Name),
    };

    private static Func<LogRecord, bool> And(Func<LogRecord, bool> a, Func<LogRecord, bool> b) => r => a(r) && b(r);
    private static Func<LogRecord, bool> Or(Func<LogRecord, bool> a, Func<LogRecord, bool> b) => r => a(r) || b(r);
    private static Func<LogRecord, bool> Not(Func<LogRecord, bool> a) => r => !a(r);

    public static bool TryGet(LogRecord r, FieldRef f, out Value v)
    {
        switch (f.Kind)
        {
            case BuiltinField.Attribute: return r.TryGetAttribute(f.Name!, out v);
            case BuiltinField.Context:
                if (r.Context is not null && r.Context.TryGet(f.Name!, out v)) return true;
                v = default; return false;
            case BuiltinField.Time: v = Value.FromTimestamp(r.TimestampUnixNanos); return true;
            case BuiltinField.Severity: v = Value.From((long)r.Severity); return true;
            case BuiltinField.Event: v = Value.From(r.EffectiveEventName); return r.EffectiveEventName is not null;
            case BuiltinField.EventId: v = Value.From((long)r.EffectiveEventId); return r.EffectiveEventId != 0;
            case BuiltinField.Service: v = Value.From(r.ServiceName); return r.ServiceName is not null;
            case BuiltinField.TraceId: v = Value.From(r.TraceId.IsEmpty ? null : r.TraceId.ToString()); return !r.TraceId.IsEmpty;
            case BuiltinField.SpanId: v = Value.From(r.SpanId.IsEmpty ? null : r.SpanId.ToString()); return !r.SpanId.IsEmpty;
            case BuiltinField.OperationId: v = Value.From(r.OperationId); return r.OperationId is not null;
            case BuiltinField.ParentOperationId: v = Value.From(r.ParentOperationId); return r.ParentOperationId is not null;
            case BuiltinField.CorrelationId: v = Value.From(r.CorrelationId); return r.CorrelationId is not null;
            case BuiltinField.Body: v = r.Body; return r.HasBody;
            case BuiltinField.Sequence: v = Value.From(r.Sequence); return true;
            case BuiltinField.ExceptionType: v = Value.From(r.Exception?.Type); return r.Exception is not null;
            case BuiltinField.ExceptionMessage: v = Value.From(r.Exception?.Message); return r.Exception?.Message is not null;
            default: v = default; return false;
        }
    }

    private static bool Compare(LogRecord r, FieldRef f, CompareOp op, Literal lit)
    {
        if (f.Kind == BuiltinField.Text) return op == CompareOp.Eq ? TextMatch(r, f, lit.Raw?.ToString() ?? "", false) : false;
        if (!TryGet(r, f, out var v)) return false;
        if (lit.Raw is null) return op switch { CompareOp.Eq => v.IsNull, CompareOp.Ne => !v.IsNull, _ => false };
        int? c;
        if (f.Kind == BuiltinField.Severity) c = CompareSeverity(v, lit);
        else if (f.Kind == BuiltinField.Time) c = CompareTime(v.AsTimestampUnixNanos(), lit);
        else if (f.Kind is BuiltinField.TraceId or BuiltinField.SpanId) c = string.Compare(v.AsString(), lit.Text.ToLowerInvariant(), StringComparison.Ordinal);
        else c = CompareValue(v, lit);
        if (c is null) return op == CompareOp.Ne;
        return op switch
        {
            CompareOp.Eq => c == 0,
            CompareOp.Ne => c != 0,
            CompareOp.Lt => c < 0,
            CompareOp.Le => c <= 0,
            CompareOp.Gt => c > 0,
            _ => c >= 0,
        };
    }

    public static Severity ParseSeverity(Literal lit)
    {
        if (lit.Raw is long n && n is >= 0 and <= SeverityExtensions.MaxNumber) return (Severity)n;
        if (SeverityExtensions.TryParse(lit.Text, out var s)) return s;
        throw new QuerySyntaxException($"'{lit.Text}' is not a severity", 0);
    }

    private static int CompareSeverity(Value v, Literal lit) => v.AsInt64().CompareTo((long)ParseSeverity(lit));

    public static long ParseTime(Literal lit)
    {
        if (lit.Raw is long l) return l; // unix nanos
        if (UnixTime.TryParseRfc3339(lit.Text, out var t)) return t;
        throw new QuerySyntaxException($"'{lit.Text}' is not an RFC 3339 timestamp or unix nanoseconds", 0);
    }

    private static int CompareTime(long ts, Literal lit) => ts.CompareTo(ParseTime(lit));

    /// <summary>Typed comparison; null when types are not comparable.</summary>
    private static int? CompareValue(Value v, Literal lit)
    {
        switch (v.Kind)
        {
            case ValueKind.String:
                return lit.Raw is string or long or double or ulong ? string.Compare(v.AsString(), lit.Text, StringComparison.Ordinal) : null;
            case ValueKind.Int64:
                return lit.Raw switch { long l => v.AsInt64().CompareTo(l), double d => ((double)v.AsInt64()).CompareTo(d), ulong u => -1 * u.CompareTo((ulong)Math.Max(0, v.AsInt64())), _ => null };
            case ValueKind.UInt64:
                return lit.Raw switch { long l => l < 0 ? 1 : v.AsUInt64().CompareTo((ulong)l), ulong u => v.AsUInt64().CompareTo(u), double d => ((double)v.AsUInt64()).CompareTo(d), _ => null };
            case ValueKind.Double:
                return lit.Raw switch { long l => v.AsDouble().CompareTo(l), double d => v.AsDouble().CompareTo(d), ulong u => v.AsDouble().CompareTo(u), _ => null };
            case ValueKind.Decimal:
                return lit.Raw switch
                {
                    long l => v.AsDecimal().CompareTo(l),
                    double d => decimal.TryParse(lit.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? v.AsDecimal().CompareTo(m) : ((double)v.AsDecimal()).CompareTo(d),
                    _ => null,
                };
            case ValueKind.Bool:
                return lit.Raw is bool b ? v.AsBool().CompareTo(b) : null;
            case ValueKind.Timestamp:
                try { return v.AsTimestampUnixNanos().CompareTo(ParseTime(lit)); } catch (QuerySyntaxException) { return null; }
            case ValueKind.Duration:
                return lit.Raw is long dl ? v.AsDurationNanos().CompareTo(dl) : null;
            case ValueKind.Uuid:
                return Guid.TryParse(lit.Text, out var g) ? v.AsGuid().CompareTo(g) : null;
            case ValueKind.Null:
                return null;
            default:
                return string.Compare(v.ToString(), lit.Text, StringComparison.Ordinal);
        }
    }

    private static bool TextMatch(LogRecord r, FieldRef f, string needle, bool prefix)
    {
        bool Match(string? s) => s is not null && (prefix ? s.StartsWith(needle, StringComparison.OrdinalIgnoreCase) : s.Contains(needle, StringComparison.OrdinalIgnoreCase));
        if (f.Kind == BuiltinField.Text)
        {
            if (r.HasBody && AnyString(r.Body, Match)) return true;
            if (Match(r.EffectiveEventName)) return true;
            foreach (var a in r.Attributes) if (AnyString(a.Value, Match)) return true;
            for (var e = r.Exception; e is not null; e = e.Inner.Count > 0 ? e.Inner[0] : null)
                if (Match(e.Type) || Match(e.Message)) return true;
            return false;
        }
        return TryGet(r, f, out var v) && AnyString(v, Match);
    }

    private static bool AnyString(Value v, Func<string?, bool> match, int depth = 0)
    {
        switch (v.Kind)
        {
            case ValueKind.String: return match(v.AsString());
            case ValueKind.Array when depth < 8: return v.AsArray().Any(x => AnyString(x, match, depth + 1));
            case ValueKind.Map when depth < 8: return v.AsMap().Any(x => AnyString(x.Value, match, depth + 1));
            case ValueKind.Null: return false;
            default: return match(v.ToString());
        }
    }
}

/// <summary>Conjunctive constraints extracted from the top-level AND chain, used only for pruning (never for final matching).</summary>
public sealed class PruningConstraints
{
    public long MinTime { get; set; } = long.MinValue;
    public long MaxTime { get; set; } = long.MaxValue;
    public Severity? MinSeverity { get; set; }
    public HashSet<string>? Events { get; set; }
    public HashSet<string>? Services { get; set; }
    public List<string> BloomKeys { get; } = [];
    public ulong? AfterSequence { get; set; }
    public ulong? BeforeSequence { get; set; }

    public static PruningConstraints From(Expr e)
    {
        var c = new PruningConstraints();
        foreach (var conj in Conjuncts(e)) c.Absorb(conj);
        return c;
    }

    private static IEnumerable<Expr> Conjuncts(Expr e)
    {
        if (e is AndExpr a)
        {
            foreach (var x in Conjuncts(a.Left)) yield return x;
            foreach (var x in Conjuncts(a.Right)) yield return x;
        }
        else yield return e;
    }

    private void Absorb(Expr e)
    {
        try
        {
            switch (e)
            {
                case CompareExpr { Field.Kind: BuiltinField.Time } t:
                    {
                        var ts = Evaluator.ParseTime(t.Value);
                        switch (t.Op)
                        {
                            case CompareOp.Eq: MinTime = Math.Max(MinTime, ts); MaxTime = Math.Min(MaxTime, ts); break;
                            case CompareOp.Gt: MinTime = Math.Max(MinTime, ts == long.MaxValue ? ts : ts + 1); break;
                            case CompareOp.Ge: MinTime = Math.Max(MinTime, ts); break;
                            case CompareOp.Lt: MaxTime = Math.Min(MaxTime, ts == long.MinValue ? ts : ts - 1); break;
                            case CompareOp.Le: MaxTime = Math.Min(MaxTime, ts); break;
                        }
                        break;
                    }
                case CompareExpr { Field.Kind: BuiltinField.Sequence, Value.Raw: long n } q when n >= 0:
                    {
                        var u = (ulong)n;
                        switch (q.Op)
                        {
                            case CompareOp.Gt: AfterSequence = Math.Max(AfterSequence ?? 0, u); break;
                            case CompareOp.Ge: if (u > 0) AfterSequence = Math.Max(AfterSequence ?? 0, u - 1); break;
                            case CompareOp.Lt: BeforeSequence = Math.Min(BeforeSequence ?? ulong.MaxValue, u); break;
                            case CompareOp.Le: BeforeSequence = Math.Min(BeforeSequence ?? ulong.MaxValue, u + 1); break;
                            case CompareOp.Eq:
                                if (u > 0) AfterSequence = Math.Max(AfterSequence ?? 0, u - 1);
                                BeforeSequence = Math.Min(BeforeSequence ?? ulong.MaxValue, u + 1);
                                break;
                        }
                        break;
                    }
                case CompareExpr { Field.Kind: BuiltinField.Severity } s when s.Op is CompareOp.Ge or CompareOp.Gt or CompareOp.Eq:
                    {
                        var sev = Evaluator.ParseSeverity(s.Value);
                        var min = s.Op == CompareOp.Gt ? (Severity)Math.Min((int)sev + 1, SeverityExtensions.MaxNumber) : sev;
                        if (MinSeverity is null || min > MinSeverity) MinSeverity = min;
                        break;
                    }
                case CompareExpr { Field.Kind: BuiltinField.Event, Op: CompareOp.Eq } ev when ev.Value.Raw is not null:
                    Events = Intersect(Events, [ev.Value.Text]); break;
                case InExpr { Field.Kind: BuiltinField.Event } evs:
                    Events = Intersect(Events, evs.Values.Where(v => v.Raw is not null).Select(v => v.Text)); break;
                case CompareExpr { Field.Kind: BuiltinField.Service, Op: CompareOp.Eq } sv when sv.Value.Raw is not null:
                    Services = Intersect(Services, [sv.Value.Text]); break;
                case InExpr { Field.Kind: BuiltinField.Service } svs:
                    Services = Intersect(Services, svs.Values.Where(v => v.Raw is not null).Select(v => v.Text)); break;
                case CompareExpr { Field.Kind: BuiltinField.TraceId, Op: CompareOp.Eq } tr:
                    if (TraceId.TryParse(tr.Value.Text.ToLowerInvariant(), out var tid)) BloomKeys.Add(BloomFilter.TraceKey(tid));
                    break;
                case CompareExpr { Field.Kind: BuiltinField.OperationId, Op: CompareOp.Eq } op when op.Value.Raw is not null:
                    BloomKeys.Add(BloomFilter.OperationKey(op.Value.Text)); break;
                case CompareExpr { Field.Kind: BuiltinField.CorrelationId, Op: CompareOp.Eq } co when co.Value.Raw is not null:
                    BloomKeys.Add(BloomFilter.CorrelationKey(co.Value.Text)); break;
            }
        }
        catch (QuerySyntaxException) { /* evaluated (and reported) by the matcher; no pruning */ }
    }

    private static HashSet<string> Intersect(HashSet<string>? current, IEnumerable<string> values)
    {
        var set = new HashSet<string>(values, StringComparer.Ordinal);
        if (current is not null) set.IntersectWith(current);
        return set;
    }

    /// <summary>Returns null when the segment may contain matches, or the reason it cannot.</summary>
    public string? ExcludeSegment(SegmentIndex? idx)
    {
        if (idx is null) return null; // unsealed: no summary, must scan
        if (!idx.HasRecords) return "empty";
        if (idx.MaxTimestamp < MinTime || idx.MinTimestamp > MaxTime) return "time";
        if (AfterSequence is { } a && idx.LastSequence <= a) return "cursor";
        if (BeforeSequence is { } b && idx.FirstSequence >= b) return "cursor";
        if (MinSeverity is { } ms && !idx.HasSeverityAtLeast(ms)) return "severity";
        if (Events is not null && !idx.Events.Any(e => Events.Contains(e.Name))) return "event";
        if (Services is not null && !idx.Services.Any(Services.Contains)) return "service";
        if (BloomKeys.Count > 0)
        {
            if (idx.Keys is null) return "bloom";
            foreach (var k in BloomKeys) if (!idx.Keys.MightContain(k)) return "bloom";
        }
        return null;
    }

    /// <summary>
    /// Returns null when the block may contain matches, or the reason it cannot. Severity, event and service are
    /// checked only when the block has a format 0.3 summary and <paramref name="idx"/> (its segment footer) is given.
    /// </summary>
    public string? ExcludeBlock(BlockIndexEntry b, SegmentIndex? idx = null)
    {
        if (b.Type != BlockType.Records) return null;
        if (b.MaxTimestamp < MinTime || b.MinTimestamp > MaxTime) return "time";
        if (AfterSequence is { } a && b.LastSequence <= a) return "cursor";
        if (BeforeSequence is { } bs && b.FirstSequence >= bs) return "cursor";
        if (idx is null || b.Summary is not { } s) return null;
        if (MinSeverity is { } ms && !s.HasSeverityAtLeast(ms)) return "severity";
        if (Events is not null && !AnyNamed(s.EventIndexes, i => idx.Events[i].Name, Events)) return "event";
        if (Services is not null && !AnyNamed(s.ServiceIndexes, i => idx.Services[i], Services)) return "service";
        return null;
    }

    private static bool AnyNamed(int[] indexes, Func<int, string> name, HashSet<string> wanted)
    {
        foreach (var i in indexes) if (wanted.Contains(name(i))) return true;
        return false;
    }
}
