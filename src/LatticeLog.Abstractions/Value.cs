using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace LatticeLog;

/// <summary>Logical value types of the LatticeLog event model (specs/event-model.md §3).</summary>
public enum ValueKind : byte
{
    Null = 0,
    Bool = 1,
    Int64 = 3,
    UInt64 = 4,
    Double = 5,
    String = 6,
    Bytes = 7,
    Array = 8,
    Map = 9,
    Timestamp = 10,
    Duration = 11,
    Uuid = 12,
    Decimal = 13,
}

/// <summary>
/// Immutable tagged value. Scalars are stored inline (no boxing); strings, byte arrays,
/// arrays and maps are stored by reference. Equality is exact: doubles compare by bit pattern
/// (so NaN payloads and -0.0 are preserved and distinguished), strings compare ordinally.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct Value : IEquatable<Value>
{
    private readonly object? _obj;
    private readonly long _a;
    private readonly long _b;

    public ValueKind Kind { get; }

    private Value(ValueKind kind, long a = 0, long b = 0, object? obj = null)
    {
        Kind = kind;
        _a = a;
        _b = b;
        _obj = obj;
    }

    public static readonly Value Null = default;

    public static Value From(bool v) => new(ValueKind.Bool, v ? 1 : 0);
    public static Value From(long v) => new(ValueKind.Int64, v);
    public static Value From(int v) => new(ValueKind.Int64, v);
    public static Value From(ulong v) => new(ValueKind.UInt64, unchecked((long)v));
    public static Value From(double v) => new(ValueKind.Double, BitConverter.DoubleToInt64Bits(v));
    public static Value From(string? v) => v is null ? Null : new(ValueKind.String, obj: v);
    public static Value From(byte[]? v) => v is null ? Null : new(ValueKind.Bytes, obj: v);
    public static Value From(Value[]? v) => v is null ? Null : new(ValueKind.Array, obj: v);
    public static Value From(LogAttribute[]? v) => v is null ? Null : new(ValueKind.Map, obj: v);
    public static Value FromTimestamp(long unixNanos) => new(ValueKind.Timestamp, unixNanos);
    public static Value From(DateTimeOffset v) => FromTimestamp(UnixTime.ToUnixNanos(v));
    public static Value FromDuration(long nanos) => new(ValueKind.Duration, nanos);
    public static Value From(TimeSpan v) => FromDuration(checked(v.Ticks * 100));
    public static Value From(Guid v)
    {
        Span<byte> b = stackalloc byte[16];
        // RFC 4122 big-endian byte order so that the binary form is language independent.
        v.TryWriteBytes(b, bigEndian: true, out _);
        return new(ValueKind.Uuid, MemoryMarshal.Read<long>(b), MemoryMarshal.Read<long>(b[8..]));
    }
    public static Value FromUuidBytes(ReadOnlySpan<byte> rfc4122)
    {
        if (rfc4122.Length != 16) throw new ArgumentException("UUID requires 16 bytes", nameof(rfc4122));
        return new(ValueKind.Uuid, MemoryMarshal.Read<long>(rfc4122), MemoryMarshal.Read<long>(rfc4122[8..]));
    }
    public static Value From(decimal v)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(v, bits);
        return new(ValueKind.Decimal, (uint)bits[0] | ((long)bits[1] << 32), (uint)bits[2] | ((long)bits[3] << 32));
    }

    public bool IsNull => Kind == ValueKind.Null;
    public bool AsBool() { Expect(ValueKind.Bool); return _a != 0; }
    public long AsInt64() { Expect(ValueKind.Int64); return _a; }
    public ulong AsUInt64() { Expect(ValueKind.UInt64); return unchecked((ulong)_a); }
    public double AsDouble() { Expect(ValueKind.Double); return BitConverter.Int64BitsToDouble(_a); }
    public long AsDoubleBits() { Expect(ValueKind.Double); return _a; }
    public string AsString() { Expect(ValueKind.String); return (string)_obj!; }
    public byte[] AsBytes() { Expect(ValueKind.Bytes); return (byte[])_obj!; }
    public Value[] AsArray() { Expect(ValueKind.Array); return (Value[])_obj!; }
    public LogAttribute[] AsMap() { Expect(ValueKind.Map); return (LogAttribute[])_obj!; }
    public long AsTimestampUnixNanos() { Expect(ValueKind.Timestamp); return _a; }
    public long AsDurationNanos() { Expect(ValueKind.Duration); return _a; }
    public void WriteUuidBytes(Span<byte> destination)
    {
        Expect(ValueKind.Uuid);
        MemoryMarshal.Write(destination, in _a);
        MemoryMarshal.Write(destination[8..], in _b);
    }
    public Guid AsGuid()
    {
        Span<byte> b = stackalloc byte[16];
        WriteUuidBytes(b);
        return new Guid(b, bigEndian: true);
    }
    public decimal AsDecimal()
    {
        Expect(ValueKind.Decimal);
        return new decimal([(int)_a, (int)(_a >> 32), (int)_b, (int)(_b >> 32)]);
    }
    /// <summary>Raw .NET-independent decimal layout: lo, mid, hi (96-bit unsigned mantissa) and flags (scale bits 16-23, sign bit 31).</summary>
    public (int Lo, int Mid, int Hi, int Flags) AsDecimalParts()
    {
        Expect(ValueKind.Decimal);
        return ((int)_a, (int)(_a >> 32), (int)_b, (int)(_b >> 32));
    }
    public static Value FromDecimalParts(int lo, int mid, int hi, int flags)
        => From(new decimal([lo, mid, hi, flags]));

    /// <summary>True when the string contains an unpaired UTF-16 surrogate and therefore cannot be represented as UTF-8 losslessly.</summary>
    public static bool HasLoneSurrogate(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { i++; continue; }
                return true;
            }
            if (char.IsLowSurrogate(c)) return true;
        }
        return false;
    }

    private void Expect(ValueKind k)
    {
        if (Kind != k) throw new InvalidOperationException($"Value is {Kind}, not {k}.");
    }

    public bool Equals(Value other)
    {
        if (Kind != other.Kind) return false;
        switch (Kind)
        {
            case ValueKind.Null: return true;
            case ValueKind.String: return string.Equals((string)_obj!, (string)other._obj!, StringComparison.Ordinal);
            case ValueKind.Bytes: return ((byte[])_obj!).AsSpan().SequenceEqual((byte[])other._obj!);
            case ValueKind.Array:
                {
                    var x = (Value[])_obj!; var y = (Value[])other._obj!;
                    if (x.Length != y.Length) return false;
                    for (var i = 0; i < x.Length; i++) if (!x[i].Equals(y[i])) return false;
                    return true;
                }
            case ValueKind.Map:
                {
                    var x = (LogAttribute[])_obj!; var y = (LogAttribute[])other._obj!;
                    if (x.Length != y.Length) return false;
                    for (var i = 0; i < x.Length; i++) if (!x[i].Equals(y[i])) return false;
                    return true;
                }
            default: return _a == other._a && _b == other._b;
        }
    }

    public override bool Equals(object? obj) => obj is Value v && Equals(v);
    public static bool operator ==(Value l, Value r) => l.Equals(r);
    public static bool operator !=(Value l, Value r) => !l.Equals(r);

    public override int GetHashCode()
    {
        return Kind switch
        {
            ValueKind.String => HashCode.Combine(Kind, StringComparer.Ordinal.GetHashCode((string)_obj!)),
            ValueKind.Bytes => HashCode.Combine(Kind, ((byte[])_obj!).Length),
            ValueKind.Array => HashCode.Combine(Kind, ((Value[])_obj!).Length),
            ValueKind.Map => HashCode.Combine(Kind, ((LogAttribute[])_obj!).Length),
            _ => HashCode.Combine(Kind, _a, _b),
        };
    }

    /// <summary>Human readable rendering. Not a serialization format (see the canonical JSON in LatticeLog.Storage).</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        AppendTo(sb);
        return sb.ToString();
    }

    public void AppendTo(StringBuilder sb)
    {
        switch (Kind)
        {
            case ValueKind.Null: sb.Append("null"); break;
            case ValueKind.Bool: sb.Append(AsBool() ? "true" : "false"); break;
            case ValueKind.Int64: sb.Append(_a.ToString(CultureInfo.InvariantCulture)); break;
            case ValueKind.UInt64: sb.Append(AsUInt64().ToString(CultureInfo.InvariantCulture)); break;
            case ValueKind.Double: sb.Append(AsDouble().ToString("R", CultureInfo.InvariantCulture)); break;
            case ValueKind.String: sb.Append(AsString()); break;
            case ValueKind.Bytes: sb.Append("0x").Append(Convert.ToHexString(AsBytes())); break;
            case ValueKind.Timestamp: sb.Append(UnixTime.FormatRfc3339(_a)); break;
            case ValueKind.Duration: sb.Append(_a.ToString(CultureInfo.InvariantCulture)).Append("ns"); break;
            case ValueKind.Uuid: sb.Append(AsGuid().ToString("D")); break;
            case ValueKind.Decimal: sb.Append(AsDecimal().ToString(CultureInfo.InvariantCulture)); break;
            case ValueKind.Array:
                sb.Append('[');
                var arr = AsArray();
                for (var i = 0; i < arr.Length; i++) { if (i > 0) sb.Append(", "); arr[i].AppendTo(sb); }
                sb.Append(']');
                break;
            case ValueKind.Map:
                sb.Append('{');
                var map = AsMap();
                for (var i = 0; i < map.Length; i++) { if (i > 0) sb.Append(", "); sb.Append(map[i].Key).Append('='); map[i].Value.AppendTo(sb); }
                sb.Append('}');
                break;
        }
    }

    public static implicit operator Value(string? v) => From(v);
    public static implicit operator Value(long v) => From(v);
    public static implicit operator Value(int v) => From(v);
    public static implicit operator Value(bool v) => From(v);
    public static implicit operator Value(double v) => From(v);
}

/// <summary>An ordered key/value pair. Attribute order is preserved by every codec.</summary>
public readonly record struct LogAttribute(string Key, Value Value)
{
    public override string ToString() => $"{Key}={Value}";
}

public static class UnixTime
{
    public const long NanosPerTick = 100;
    private static readonly long EpochTicks = DateTime.UnixEpoch.Ticks;

    public static long ToUnixNanos(DateTimeOffset t) => checked((t.UtcTicks - EpochTicks) * NanosPerTick);
    public static long ToUnixNanos(DateTime t) => ToUnixNanos(new DateTimeOffset(t.ToUniversalTime()));
    public static DateTimeOffset FromUnixNanos(long nanos) => new(EpochTicks + nanos / NanosPerTick, TimeSpan.Zero);
    public static long NowUnixNanos() => ToUnixNanos(DateTimeOffset.UtcNow);

    /// <summary>RFC 3339 with 9 fractional digits, always UTC ('Z'). Handles timestamps before 1970.</summary>
    public static string FormatRfc3339(long unixNanos)
    {
        var sub = unixNanos % 1_000_000_000;
        var secs = unixNanos / 1_000_000_000;
        if (sub < 0) { sub += 1_000_000_000; secs -= 1; }
        var dt = DateTimeOffset.FromUnixTimeSeconds(secs);
        return dt.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "." + sub.ToString("D9", CultureInfo.InvariantCulture) + "Z";
    }

    public static bool TryParseRfc3339(string s, out long unixNanos)
    {
        unixNanos = 0;
        // Split fractional part manually to keep nanosecond precision.
        var tIdx = s.IndexOf('T');
        if (tIdx < 0) tIdx = s.IndexOf(' ');
        var dot = s.IndexOf('.', Math.Max(0, tIdx));
        string main; long fracNanos = 0; string zone = "";
        if (dot >= 0)
        {
            var end = dot + 1;
            while (end < s.Length && char.IsAsciiDigit(s[end])) end++;
            var frac = s[(dot + 1)..end];
            if (frac.Length == 0 || frac.Length > 9) return false;
            fracNanos = long.Parse(frac.PadRight(9, '0'), CultureInfo.InvariantCulture);
            main = s[..dot];
            zone = s[end..];
        }
        else
        {
            var zi = s.IndexOfAny(['Z', 'z', '+'], Math.Max(0, tIdx));
            var minus = tIdx >= 0 ? s.IndexOf('-', tIdx) : -1;
            if (minus > 0 && (zi < 0 || minus < zi)) zi = minus;
            if (zi < 0) { main = s; zone = ""; } else { main = s[..zi]; zone = s[zi..]; }
        }
        if (!DateTimeOffset.TryParse(main + (zone.Length == 0 ? "Z" : zone), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto)) return false;
        try { unixNanos = checked(ToUnixNanos(dto) + fracNanos); return true; }
        catch (OverflowException) { return false; }
    }
}
