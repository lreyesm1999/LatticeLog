using System.Buffers.Binary;
using System.Text;

namespace LatticeLog.Storage.Format;

/// <summary>Wire tags of values (specs/llog-format-v0.md §4).</summary>
public enum ValueTag : byte
{
    Null = 0,
    False = 1,
    True = 2,
    Int64 = 3,
    UInt64 = 4,
    Double = 5,
    StringUtf8 = 6,
    Bytes = 7,
    Array = 8,
    Map = 9,
    Timestamp = 10,
    Duration = 11,
    Uuid = 12,
    Decimal = 13,
    /// <summary>UTF-16 code units, used only when the string has unpaired surrogates (exact .NET fidelity).</summary>
    StringUtf16 = 14,
    /// <summary>Reference into the block string dictionary (structural codecs only).</summary>
    StringRef = 15,
}

/// <summary>Optional string dictionary hook used by structural codecs.</summary>
public interface IStringTable
{
    /// <summary>Returns 0-based index or -1.</summary>
    int IndexOf(string s);
    string Get(int index);
}

public static class ValueCodec
{
    public static void Write(ByteWriter w, in Value v, IStringTable? dict = null, int depth = 0)
    {
        if (depth > Llog.MaxValueDepth) throw new LlogFormatException("Value nesting too deep.");
        switch (v.Kind)
        {
            case ValueKind.Null: w.WriteByte((byte)ValueTag.Null); break;
            case ValueKind.Bool: w.WriteByte((byte)(v.AsBool() ? ValueTag.True : ValueTag.False)); break;
            case ValueKind.Int64: w.WriteByte((byte)ValueTag.Int64); w.WriteVarInt(v.AsInt64()); break;
            case ValueKind.UInt64: w.WriteByte((byte)ValueTag.UInt64); w.WriteVarUInt(v.AsUInt64()); break;
            case ValueKind.Double: w.WriteByte((byte)ValueTag.Double); w.WriteInt64(v.AsDoubleBits()); break;
            case ValueKind.String: WriteStringValue(w, v.AsString(), dict); break;
            case ValueKind.Bytes: w.WriteByte((byte)ValueTag.Bytes); w.WriteLengthPrefixed(v.AsBytes()); break;
            case ValueKind.Array:
                {
                    var a = v.AsArray();
                    w.WriteByte((byte)ValueTag.Array);
                    w.WriteVarUInt((ulong)a.Length);
                    foreach (var item in a) Write(w, item, dict, depth + 1);
                    break;
                }
            case ValueKind.Map:
                {
                    var m = v.AsMap();
                    w.WriteByte((byte)ValueTag.Map);
                    w.WriteVarUInt((ulong)m.Length);
                    foreach (var kv in m)
                    {
                        WriteText(w, kv.Key, dict);
                        Write(w, kv.Value, dict, depth + 1);
                    }
                    break;
                }
            case ValueKind.Timestamp: w.WriteByte((byte)ValueTag.Timestamp); w.WriteVarInt(v.AsTimestampUnixNanos()); break;
            case ValueKind.Duration: w.WriteByte((byte)ValueTag.Duration); w.WriteVarInt(v.AsDurationNanos()); break;
            case ValueKind.Uuid: w.WriteByte((byte)ValueTag.Uuid); v.WriteUuidBytes(w.GetSpan(16)); w.Advance(16); break;
            case ValueKind.Decimal:
                {
                    w.WriteByte((byte)ValueTag.Decimal);
                    var (lo, mid, hi, flags) = v.AsDecimalParts();
                    w.WriteUInt32((uint)lo); w.WriteUInt32((uint)mid); w.WriteUInt32((uint)hi); w.WriteUInt32((uint)flags);
                    break;
                }
            default: throw new LlogFormatException($"Unknown value kind {v.Kind}.");
        }
    }

    private static void WriteStringValue(ByteWriter w, string s, IStringTable? dict)
    {
        if (dict is not null)
        {
            var idx = dict.IndexOf(s);
            if (idx >= 0) { w.WriteByte((byte)ValueTag.StringRef); w.WriteVarUInt((ulong)idx); return; }
        }
        var start = w.Length;
        try
        {
            w.WriteByte((byte)ValueTag.StringUtf8);
            w.WriteString(s);
        }
        catch (EncoderFallbackException)
        {
            // Unpaired surrogates: keep exact UTF-16 code units instead of silently replacing them.
            RewindTo(w, start);
            w.WriteByte((byte)ValueTag.StringUtf16);
            w.WriteVarUInt((ulong)s.Length);
            var span = w.GetSpan(s.Length * 2);
            for (var i = 0; i < s.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(span[(i * 2)..], s[i]);
            w.Advance(s.Length * 2);
        }
    }

    private static void RewindTo(ByteWriter w, int position) => w.Advance(position - w.Length);

    /// <summary>
    /// Text used for keys and names: in structural codecs a varint (0 = literal follows, n = dict[n-1]);
    /// in the raw codec a plain length-prefixed UTF-8 string. Keys must be valid Unicode; unpaired
    /// surrogates are replaced by U+FFFD by <see cref="SanitizeKey"/> before reaching here.
    /// </summary>
    public static void WriteText(ByteWriter w, string s, IStringTable? dict)
    {
        if (dict is null) { w.WriteString(s); return; }
        var idx = dict.IndexOf(s);
        if (idx >= 0) { w.WriteVarUInt((ulong)idx + 1); return; }
        w.WriteVarUInt(0);
        w.WriteString(s);
    }

    public static string ReadText(ref ByteReader r, IStringTable? dict)
    {
        if (dict is null) return r.ReadString();
        var t = r.ReadVarUInt();
        if (t == 0) return r.ReadString();
        return dict.Get(checked((int)(t - 1)));
    }

    public static string SanitizeKey(string s) => Value.HasLoneSurrogate(s) ? new string(s.Select(c => char.IsSurrogate(c) ? '�' : c).ToArray()) : s;

    public static Value Read(ref ByteReader r, IStringTable? dict = null, int depth = 0)
    {
        if (depth > Llog.MaxValueDepth) throw new LlogFormatException("Value nesting too deep.");
        var tag = (ValueTag)r.ReadByte();
        return ReadPayload(ref r, tag, dict, depth);
    }

    public static Value ReadPayload(ref ByteReader r, ValueTag tag, IStringTable? dict, int depth)
    {
        switch (tag)
        {
            case ValueTag.Null: return Value.Null;
            case ValueTag.False: return Value.From(false);
            case ValueTag.True: return Value.From(true);
            case ValueTag.Int64: return Value.From(r.ReadVarInt());
            case ValueTag.UInt64: return Value.From(r.ReadVarUInt());
            case ValueTag.Double: return Value.From(BitConverter.Int64BitsToDouble(r.ReadInt64()));
            case ValueTag.StringUtf8: return Value.From(r.ReadString());
            case ValueTag.StringUtf16:
                {
                    var n = r.ReadCount(2);
                    var bytes = r.ReadBytes(n * 2);
                    var chars = new char[n];
                    for (var i = 0; i < n; i++) chars[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(bytes[(i * 2)..]);
                    return Value.From(new string(chars));
                }
            case ValueTag.StringRef:
                if (dict is null) throw new LlogFormatException("String reference outside of a dictionary codec.");
                return Value.From(dict.Get(checked((int)r.ReadVarUInt())));
            case ValueTag.Bytes: return Value.From(r.ReadBytes(r.ReadCount(1)).ToArray());
            case ValueTag.Array:
                {
                    var n = r.ReadCount(1);
                    var a = new Value[n];
                    for (var i = 0; i < n; i++) a[i] = Read(ref r, dict, depth + 1);
                    return Value.From(a);
                }
            case ValueTag.Map:
                {
                    var n = r.ReadCount(2);
                    var m = new LogAttribute[n];
                    for (var i = 0; i < n; i++)
                    {
                        var k = ReadText(ref r, dict);
                        m[i] = new LogAttribute(k, Read(ref r, dict, depth + 1));
                    }
                    return Value.From(m);
                }
            case ValueTag.Timestamp: return Value.FromTimestamp(r.ReadVarInt());
            case ValueTag.Duration: return Value.FromDuration(r.ReadVarInt());
            case ValueTag.Uuid: return Value.FromUuidBytes(r.ReadBytes(16));
            case ValueTag.Decimal:
                {
                    var lo = (int)r.ReadUInt32(); var mid = (int)r.ReadUInt32(); var hi = (int)r.ReadUInt32(); var flags = (int)r.ReadUInt32();
                    // Validate .NET decimal invariants: scale <= 28 and unused bits zero.
                    if ((flags & 0x7F00FFFF) != 0 || ((flags >> 16) & 0xFF) > 28) throw new LlogFormatException("Invalid decimal encoding.");
                    return Value.FromDecimalParts(lo, mid, hi, flags);
                }
            default: throw new LlogFormatException($"Unknown value tag {(byte)tag}.");
        }
    }

    /// <summary>Kind-specific payload without tag (used for schema-shaped attributes when the kind matches the field).</summary>
    public static void WriteUntagged(ByteWriter w, in Value v, IStringTable? dict)
    {
        // Bool and String keep their tag: for them the tag carries information (true/false, utf8/utf16/ref).
        switch (v.Kind)
        {
            case ValueKind.Int64: w.WriteVarInt(v.AsInt64()); return;
            case ValueKind.UInt64: w.WriteVarUInt(v.AsUInt64()); return;
            case ValueKind.Double: w.WriteInt64(v.AsDoubleBits()); return;
            case ValueKind.Timestamp: w.WriteVarInt(v.AsTimestampUnixNanos()); return;
            case ValueKind.Duration: w.WriteVarInt(v.AsDurationNanos()); return;
            case ValueKind.Bool:
            case ValueKind.String:
                Write(w, v, dict);
                return;
            default:
                {
                    var start = w.Length;
                    Write(w, v, dict);
                    RemoveByteAt(w, start, w.Length - start);
                    return;
                }
        }
    }

    private static void RemoveByteAt(ByteWriter w, int start, int len)
    {
        var tmp = w.WrittenSpan.Slice(start + 1, len - 1).ToArray();
        RewindTo(w, start);
        w.WriteBytes(tmp);
    }

    public static Value ReadUntagged(ref ByteReader r, ValueKind kind, IStringTable? dict)
    {
        return kind switch
        {
            ValueKind.Bool or ValueKind.String => Read(ref r, dict),
            ValueKind.Int64 => ReadPayload(ref r, ValueTag.Int64, dict, 0),
            ValueKind.UInt64 => ReadPayload(ref r, ValueTag.UInt64, dict, 0),
            ValueKind.Double => ReadPayload(ref r, ValueTag.Double, dict, 0),
            ValueKind.Bytes => ReadPayload(ref r, ValueTag.Bytes, dict, 0),
            ValueKind.Array => ReadPayload(ref r, ValueTag.Array, dict, 0),
            ValueKind.Map => ReadPayload(ref r, ValueTag.Map, dict, 0),
            ValueKind.Timestamp => ReadPayload(ref r, ValueTag.Timestamp, dict, 0),
            ValueKind.Duration => ReadPayload(ref r, ValueTag.Duration, dict, 0),
            ValueKind.Uuid => ReadPayload(ref r, ValueTag.Uuid, dict, 0),
            ValueKind.Decimal => ReadPayload(ref r, ValueTag.Decimal, dict, 0),
            _ => throw new LlogFormatException($"Field kind {kind} cannot be untagged."),
        };
    }

    public static void WriteException(ByteWriter w, ExceptionInfo ex, IStringTable? dict, int depth = 0)
    {
        if (depth > ExceptionInfo.MaxDepth) throw new LlogFormatException("Exception nesting too deep.");
        WriteText(w, ex.Type, dict);
        byte p = 0;
        if (ex.Message is not null) p |= 1;
        if (ex.StackTrace is not null) p |= 2;
        w.WriteByte(p);
        if (ex.Message is not null) WriteStringValue(w, ex.Message, dict);
        if (ex.StackTrace is not null) WriteStringValue(w, ex.StackTrace, dict);
        w.WriteVarUInt((ulong)ex.Inner.Count);
        foreach (var i in ex.Inner) WriteException(w, i, dict, depth + 1);
    }

    public static ExceptionInfo ReadException(ref ByteReader r, IStringTable? dict, int depth = 0)
    {
        if (depth > ExceptionInfo.MaxDepth) throw new LlogFormatException("Exception nesting too deep.");
        var type = ReadText(ref r, dict);
        var p = r.ReadByte();
        if ((p & ~3) != 0) throw new LlogFormatException("Invalid exception presence bits.");
        string? msg = null, st = null;
        if ((p & 1) != 0) msg = ReadStringValue(ref r, dict);
        if ((p & 2) != 0) st = ReadStringValue(ref r, dict);
        var n = r.ReadCount(2, ExceptionInfo.MaxInnerPerLevel * 4);
        var inner = new ExceptionInfo[n];
        for (var i = 0; i < n; i++) inner[i] = ReadException(ref r, dict, depth + 1);
        return new ExceptionInfo(type, msg, st, inner);
    }

    private static string ReadStringValue(ref ByteReader r, IStringTable? dict)
    {
        var v = Read(ref r, dict);
        if (v.Kind != ValueKind.String) throw new LlogFormatException("Expected string value.");
        return v.AsString();
    }
}
