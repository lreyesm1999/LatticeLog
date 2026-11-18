using LatticeLog.Storage.Format;

namespace LatticeLog.Storage.Codecs;

/// <summary>
/// Record block codecs. All codecs are lossless for the logical model (structured-exact, spec §3.2):
/// decode(encode(x)) == x, including attribute order, nulls, NaN payloads and unpaired surrogates in values.
/// </summary>
public static class RecordCodec
{
    [Flags]
    private enum P : uint
    {
        Observed = 1 << 0,
        Monotonic = 1 << 1,
        SchemaRef = 1 << 2,
        EventId = 1 << 3,
        EventName = 1 << 4,
        Body = 1 << 5,
        TraceId = 1 << 6,
        SpanId = 1 << 7,
        TraceFlags = 1 << 8,
        OperationId = 1 << 9,
        ParentOperationId = 1 << 10,
        CorrelationId = 1 << 11,
        Exception = 1 << 12,
        ContextRef = 1 << 13,
        Flags = 1 << 14,
        AttrsShaped = 1 << 15,
        CtxInherit = 1 << 16,
        CtxFields = TraceId | SpanId | TraceFlags | OperationId | ParentOperationId | CorrelationId | ContextRef,
        AllKnown = (1 << 17) - 1,
    }

    public static void Encode(CodecId codec, IReadOnlyList<LogRecord> records, int start, int count, SegmentTables tables, ByteWriter w)
    {
        switch (codec)
        {
            case CodecId.Raw:
                for (var i = start; i < start + count; i++) EncodeOne(records[i], null, tables, w, null, null, null);
                break;
            case CodecId.Structural:
            case CodecId.Contextual:
                {
                    var dict = BlockDictionary.Build(records, start, count);
                    dict.Write(w);
                    var state = new StructState(codec == CodecId.Contextual);
                    for (var i = start; i < start + count; i++)
                    {
                        EncodeOne(records[i], dict, tables, w, state, i > start ? records[i - 1] : null, state.Traces);
                    }
                    break;
                }
            default: throw new ArgumentOutOfRangeException(nameof(codec));
        }
    }

    public static void Decode(CodecId codec, ReadOnlySpan<byte> payload, int count, SegmentTables tables, List<LogRecord> output)
    {
        var r = new ByteReader(payload);
        BlockDictionary? dict = null;
        StructState? state = null;
        if (codec is CodecId.Structural or CodecId.Contextual)
        {
            dict = BlockDictionary.Read(ref r);
            state = new StructState(codec == CodecId.Contextual);
        }
        else if (codec != CodecId.Raw) throw new LlogFormatException($"Unknown codec {(byte)codec}.");
        LogRecord? prev = null;
        for (var i = 0; i < count; i++)
        {
            var rec = DecodeOne(ref r, dict, tables, state, prev);
            output.Add(rec);
            prev = rec;
        }
        if (!r.End) throw new LlogFormatException("Trailing bytes after last record in block.");
    }

    private sealed class StructState(bool contextual)
    {
        public readonly bool Contextual = contextual;
        public ulong PrevSeq;
        public long PrevTs;
        public readonly TraceTable? Traces = contextual ? new TraceTable() : null;
    }

    private sealed class TraceTable
    {
        public readonly List<TraceId> List = [];
        public readonly Dictionary<TraceId, int> Index = [];
    }

    private static bool AttrsMatchSchema(LogRecord r)
    {
        var e = r.Event;
        if (e is null || e.Fields.Count != r.Attributes.Length || e.Fields.Count == 0 || e.Fields.Count > 63) return false;
        for (var i = 0; i < e.Fields.Count; i++) if (e.Fields[i].Name != r.Attributes[i].Key) return false;
        return true;
    }

    private static bool SameCtx(LogRecord a, LogRecord b)
        => a.TraceId == b.TraceId && a.SpanId == b.SpanId && a.TraceFlags == b.TraceFlags
           && a.OperationId == b.OperationId && a.ParentOperationId == b.ParentOperationId
           && a.CorrelationId == b.CorrelationId && ReferenceEquals(a.Context, b.Context);

    private static void EncodeOne(LogRecord rec, BlockDictionary? dict, SegmentTables tables, ByteWriter w, StructState? st, LogRecord? prev, TraceTable? traces)
    {
        P p = 0;
        if (rec.ObservedUnixNanos != 0) p |= P.Observed;
        if (rec.MonotonicNanos != 0) p |= P.Monotonic;
        if (rec.Event is not null) p |= P.SchemaRef;
        if (rec.EventId != 0) p |= P.EventId;
        if (rec.EventName is not null) p |= P.EventName;
        if (rec.HasBody) p |= P.Body;
        if (!rec.TraceId.IsEmpty) p |= P.TraceId;
        if (!rec.SpanId.IsEmpty) p |= P.SpanId;
        if (rec.TraceFlags != 0) p |= P.TraceFlags;
        if (rec.OperationId is not null) p |= P.OperationId;
        if (rec.ParentOperationId is not null) p |= P.ParentOperationId;
        if (rec.CorrelationId is not null) p |= P.CorrelationId;
        if (rec.Exception is not null) p |= P.Exception;
        if (rec.Context is not null) p |= P.ContextRef;
        if (rec.Flags != 0) p |= P.Flags;
        var shaped = dict is not null && AttrsMatchSchema(rec);
        if (shaped) p |= P.AttrsShaped;
        var inherit = st is { Contextual: true } && prev is not null && SameCtx(rec, prev);
        if (inherit) { p |= P.CtxInherit; p &= ~P.CtxFields; }

        w.WriteVarUInt((ulong)p);
        if (st is null)
        {
            w.WriteVarUInt(rec.Sequence);
            w.WriteVarInt(rec.TimestampUnixNanos);
        }
        else
        {
            w.WriteVarInt(unchecked((long)(rec.Sequence - st.PrevSeq)));
            w.WriteVarInt(unchecked(rec.TimestampUnixNanos - st.PrevTs));
            st.PrevSeq = rec.Sequence;
            st.PrevTs = rec.TimestampUnixNanos;
        }
        w.WriteByte((byte)rec.Severity);
        if ((p & P.Observed) != 0) w.WriteVarInt(rec.ObservedUnixNanos);
        if ((p & P.Monotonic) != 0) w.WriteVarInt(rec.MonotonicNanos);
        if ((p & P.SchemaRef) != 0) w.WriteVarUInt((ulong)tables.SchemaRef(rec.Event!));
        if ((p & P.EventId) != 0) w.WriteVarUInt(rec.EventId);
        if ((p & P.EventName) != 0) ValueCodec.WriteText(w, ValueCodec.SanitizeKey(rec.EventName!), dict);
        if ((p & P.Body) != 0) ValueCodec.Write(w, rec.Body, dict);
        if ((p & P.TraceId) != 0)
        {
            if (traces is not null)
            {
                if (traces.Index.TryGetValue(rec.TraceId, out var ti)) w.WriteVarUInt((ulong)ti + 1);
                else
                {
                    w.WriteVarUInt(0);
                    rec.TraceId.WriteBytes(w.GetSpan(16)); w.Advance(16);
                    traces.Index[rec.TraceId] = traces.List.Count;
                    traces.List.Add(rec.TraceId);
                }
            }
            else { rec.TraceId.WriteBytes(w.GetSpan(16)); w.Advance(16); }
        }
        if ((p & P.SpanId) != 0) w.WriteUInt64(rec.SpanId.Value);
        if ((p & P.TraceFlags) != 0) w.WriteByte(rec.TraceFlags);
        if ((p & P.OperationId) != 0) ValueCodec.WriteText(w, ValueCodec.SanitizeKey(rec.OperationId!), dict);
        if ((p & P.ParentOperationId) != 0) ValueCodec.WriteText(w, ValueCodec.SanitizeKey(rec.ParentOperationId!), dict);
        if ((p & P.CorrelationId) != 0) ValueCodec.WriteText(w, ValueCodec.SanitizeKey(rec.CorrelationId!), dict);
        if ((p & P.Exception) != 0) ValueCodec.WriteException(w, rec.Exception!, dict);
        if ((p & P.ContextRef) != 0) w.WriteVarUInt((ulong)tables.ContextRef(rec.Context!));
        if ((p & P.Flags) != 0) w.WriteByte((byte)rec.Flags);

        var attrs = rec.Attributes;
        if (shaped)
        {
            var fields = rec.Event!.Fields;
            ulong mismatch = 0;
            for (var i = 0; i < attrs.Length; i++) if (attrs[i].Value.Kind != fields[i].Kind) mismatch |= 1UL << i;
            w.WriteVarUInt(mismatch);
            for (var i = 0; i < attrs.Length; i++)
            {
                if ((mismatch & (1UL << i)) != 0) ValueCodec.Write(w, attrs[i].Value, dict);
                else ValueCodec.WriteUntagged(w, attrs[i].Value, dict);
            }
        }
        else
        {
            w.WriteVarUInt((ulong)attrs.Length);
            foreach (var a in attrs)
            {
                ValueCodec.WriteText(w, ValueCodec.SanitizeKey(a.Key), dict);
                ValueCodec.Write(w, a.Value, dict);
            }
        }
    }

    private static LogRecord DecodeOne(ref ByteReader r, BlockDictionary? dict, SegmentTables tables, StructState? st, LogRecord? prev)
    {
        var praw = r.ReadVarUInt();
        if ((praw & ~(ulong)P.AllKnown) != 0) throw new LlogFormatException($"Unknown record presence bits 0x{praw:x}.");
        var p = (P)praw;
        if (dict is null && (p & (P.AttrsShaped | P.CtxInherit)) != 0) throw new LlogFormatException("Structural presence bits in raw codec.");
        if ((p & P.CtxInherit) != 0 && (st is not { Contextual: true } || prev is null || (p & P.CtxFields) != 0))
            throw new LlogFormatException("Invalid context inheritance.");
        var rec = new LogRecord();
        if (st is null)
        {
            rec.Sequence = r.ReadVarUInt();
            rec.TimestampUnixNanos = r.ReadVarInt();
        }
        else
        {
            rec.Sequence = unchecked(st.PrevSeq + (ulong)r.ReadVarInt());
            rec.TimestampUnixNanos = unchecked(st.PrevTs + r.ReadVarInt());
            st.PrevSeq = rec.Sequence;
            st.PrevTs = rec.TimestampUnixNanos;
        }
        var sev = r.ReadByte();
        if (sev > SeverityExtensions.MaxNumber) throw new LlogFormatException("Severity out of range.");
        rec.Severity = (Severity)sev;
        if ((p & P.Observed) != 0) rec.ObservedUnixNanos = r.ReadVarInt();
        if ((p & P.Monotonic) != 0) rec.MonotonicNanos = r.ReadVarInt();
        if ((p & P.SchemaRef) != 0) rec.Event = tables.GetSchema(r.ReadVarUInt());
        if ((p & P.EventId) != 0)
        {
            var id = r.ReadVarUInt();
            if (id is 0 or > uint.MaxValue) throw new LlogFormatException("Event id out of range.");
            rec.EventId = (uint)id;
        }
        if ((p & P.EventName) != 0) rec.EventName = ValueCodec.ReadText(ref r, dict);
        if ((p & P.Body) != 0) { rec.Body = ValueCodec.Read(ref r, dict); rec.HasBody = true; }
        if ((p & P.TraceId) != 0)
        {
            if (st?.Traces is { } traces)
            {
                var k = r.ReadVarUInt();
                if (k == 0)
                {
                    rec.TraceId = TraceId.FromBytes(r.ReadBytes(16));
                    traces.List.Add(rec.TraceId);
                }
                else
                {
                    if (k > (ulong)traces.List.Count) throw new LlogFormatException("Trace reference out of range.");
                    rec.TraceId = traces.List[(int)k - 1];
                }
            }
            else rec.TraceId = TraceId.FromBytes(r.ReadBytes(16));
            if (rec.TraceId.IsEmpty) throw new LlogFormatException("Present trace id is all zeros.");
        }
        if ((p & P.SpanId) != 0)
        {
            rec.SpanId = new SpanId(r.ReadUInt64());
            if (rec.SpanId.IsEmpty) throw new LlogFormatException("Present span id is zero.");
        }
        if ((p & P.TraceFlags) != 0) rec.TraceFlags = r.ReadByte();
        if ((p & P.OperationId) != 0) rec.OperationId = ValueCodec.ReadText(ref r, dict);
        if ((p & P.ParentOperationId) != 0) rec.ParentOperationId = ValueCodec.ReadText(ref r, dict);
        if ((p & P.CorrelationId) != 0) rec.CorrelationId = ValueCodec.ReadText(ref r, dict);
        if ((p & P.Exception) != 0) rec.Exception = ValueCodec.ReadException(ref r, dict);
        if ((p & P.ContextRef) != 0) rec.Context = tables.GetContext(r.ReadVarUInt());
        if ((p & P.Flags) != 0)
        {
            var f = r.ReadByte();
            if ((f & ~7) != 0) throw new LlogFormatException("Unknown record flags.");
            rec.Flags = (RecordFlags)f;
        }
        if ((p & P.CtxInherit) != 0)
        {
            rec.TraceId = prev!.TraceId; rec.SpanId = prev.SpanId; rec.TraceFlags = prev.TraceFlags;
            rec.OperationId = prev.OperationId; rec.ParentOperationId = prev.ParentOperationId;
            rec.CorrelationId = prev.CorrelationId; rec.Context = prev.Context;
        }
        if ((p & P.AttrsShaped) != 0)
        {
            var fields = rec.Event?.Fields ?? throw new LlogFormatException("Shaped attributes without schema.");
            if (fields.Count is 0 or > 63) throw new LlogFormatException("Shaped attributes with invalid field count.");
            var mismatch = r.ReadVarUInt();
            if ((mismatch >> fields.Count) != 0) throw new LlogFormatException("Invalid kind mismatch bitmap.");
            var attrs = new LogAttribute[fields.Count];
            for (var i = 0; i < attrs.Length; i++)
            {
                var v = (mismatch & (1UL << i)) != 0 ? ValueCodec.Read(ref r, dict) : ValueCodec.ReadUntagged(ref r, fields[i].Kind, dict);
                if ((mismatch & (1UL << i)) != 0 && v.Kind == fields[i].Kind) throw new LlogFormatException("Non-canonical kind mismatch.");
                if ((mismatch & (1UL << i)) == 0 && v.Kind != fields[i].Kind) throw new LlogFormatException("Value kind does not match schema field.");
                attrs[i] = new LogAttribute(fields[i].Name, v);
            }
            rec.Attributes = attrs;
        }
        else
        {
            var n = r.ReadCount(2, Llog.MaxAttributes);
            if (n > 0)
            {
                var attrs = new LogAttribute[n];
                for (var i = 0; i < n; i++)
                {
                    var k = ValueCodec.ReadText(ref r, dict);
                    attrs[i] = new LogAttribute(k, ValueCodec.Read(ref r, dict));
                }
                rec.Attributes = attrs;
            }
        }
        return rec;
    }
}

/// <summary>Block-local string dictionary (Lattice-S). Strings occurring at least twice are interned in first-appearance order.</summary>
public sealed class BlockDictionary : IStringTable
{
    private readonly Dictionary<string, int> _index;
    private readonly string[] _items;

    private BlockDictionary(string[] items, Dictionary<string, int> index) { _items = items; _index = index; }

    public int Count => _items.Length;
    public int IndexOf(string s) => _index.TryGetValue(s, out var i) ? i : -1;
    public string Get(int index) => (uint)index < (uint)_items.Length ? _items[index] : throw new LlogFormatException($"Dictionary index {index} out of range.");

    public static BlockDictionary Build(IReadOnlyList<LogRecord> records, int start, int count)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        void Add(string? s)
        {
            if (s is null || s.Length < 2) return;
            ref var c = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(counts, s, out var exists);
            if (!exists) order.Add(s);
            c++;
        }
        void AddValue(in Value v, int depth)
        {
            if (depth > Llog.MaxValueDepth) return;
            switch (v.Kind)
            {
                case ValueKind.String: Add(v.AsString()); break;
                case ValueKind.Array: foreach (var x in v.AsArray()) AddValue(x, depth + 1); break;
                case ValueKind.Map: foreach (var kv in v.AsMap()) { Add(kv.Key); AddValue(kv.Value, depth + 1); } break;
            }
        }
        void AddEx(ExceptionInfo? e, int depth)
        {
            if (e is null || depth > ExceptionInfo.MaxDepth) return;
            Add(e.Type); Add(e.Message); Add(e.StackTrace);
            foreach (var i in e.Inner) AddEx(i, depth + 1);
        }
        for (var i = start; i < start + count; i++)
        {
            var r = records[i];
            Add(r.EventName); Add(r.OperationId); Add(r.ParentOperationId); Add(r.CorrelationId);
            if (r.HasBody) AddValue(r.Body, 0);
            var shaped = r.Event is { } e && e.Fields.Count == r.Attributes.Length;
            foreach (var a in r.Attributes)
            {
                if (!shaped) Add(a.Key);
                AddValue(a.Value, 0);
            }
            AddEx(r.Exception, 0);
        }
        var items = new List<string>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in order)
        {
            if (counts[s] < 2 || Value.HasLoneSurrogate(s)) continue;
            if (items.Count >= Llog.MaxDictionaryEntries) break;
            index[s] = items.Count;
            items.Add(s);
        }
        return new BlockDictionary([.. items], index);
    }

    public void Write(ByteWriter w)
    {
        w.WriteVarUInt((ulong)_items.Length);
        foreach (var s in _items) w.WriteString(s);
    }

    public static BlockDictionary Read(ref ByteReader r)
    {
        var n = r.ReadCount(1, Llog.MaxDictionaryEntries);
        var items = new string[n];
        var index = new Dictionary<string, int>(n, StringComparer.Ordinal);
        for (var i = 0; i < n; i++)
        {
            items[i] = r.ReadString();
            if (!index.TryAdd(items[i], i)) throw new LlogFormatException("Duplicate dictionary entry.");
        }
        return new BlockDictionary(items, index);
    }
}
