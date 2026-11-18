using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace LatticeLog.Storage.Format;

/// <summary>Raised for any malformed, truncated or unsupported input. Readers never guess (P06-T07).</summary>
public sealed class LlogFormatException(string message) : Exception(message);

/// <summary>Growable little-endian byte buffer with LEB128 varints (specs/llog-format-v0.md §3).</summary>
public sealed class ByteWriter
{
    private byte[] _buf;
    private int _pos;

    public ByteWriter(int capacity = 4096) => _buf = ArrayPool<byte>.Shared.Rent(Math.Max(64, capacity));

    public int Length => _pos;
    public ReadOnlySpan<byte> WrittenSpan => _buf.AsSpan(0, _pos);
    public ReadOnlyMemory<byte> WrittenMemory => _buf.AsMemory(0, _pos);

    public void Clear() => _pos = 0;

    public void Release()
    {
        if (_buf.Length > 0) ArrayPool<byte>.Shared.Return(_buf);
        _buf = [];
        _pos = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Ensure(int extra)
    {
        if (_pos + extra <= _buf.Length) return;
        Grow(extra);
    }

    private void Grow(int extra)
    {
        var n = ArrayPool<byte>.Shared.Rent(Math.Max(_buf.Length * 2, _pos + extra));
        _buf.AsSpan(0, _pos).CopyTo(n);
        if (_buf.Length > 0) ArrayPool<byte>.Shared.Return(_buf);
        _buf = n;
    }

    public Span<byte> GetSpan(int size) { Ensure(size); return _buf.AsSpan(_pos, size); }
    public void Advance(int n) => _pos += n;

    public void WriteByte(byte b) { Ensure(1); _buf[_pos++] = b; }
    public void WriteBytes(ReadOnlySpan<byte> b) { Ensure(b.Length); b.CopyTo(_buf.AsSpan(_pos)); _pos += b.Length; }
    public void WriteUInt16(ushort v) { Ensure(2); BinaryPrimitives.WriteUInt16LittleEndian(_buf.AsSpan(_pos), v); _pos += 2; }
    public void WriteUInt32(uint v) { Ensure(4); BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(_pos), v); _pos += 4; }
    public void WriteUInt64(ulong v) { Ensure(8); BinaryPrimitives.WriteUInt64LittleEndian(_buf.AsSpan(_pos), v); _pos += 8; }
    public void WriteInt64(long v) => WriteUInt64(unchecked((ulong)v));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteVarUInt(ulong v)
    {
        Ensure(10);
        var b = _buf;
        var p = _pos;
        while (v >= 0x80)
        {
            b[p++] = (byte)(v | 0x80);
            v >>= 7;
        }
        b[p++] = (byte)v;
        _pos = p;
    }

    public void WriteVarInt(long v) => WriteVarUInt(ZigZag.Encode(v));

    public void WriteString(string s)
    {
        var max = ByteReader.StrictUtf8.GetMaxByteCount(s.Length);
        if (max < 0x4000)
        {
            // Fast path: reserve worst case, write length after encoding.
            Ensure(max + 2);
            var n = ByteReader.StrictUtf8.GetBytes(s, _buf.AsSpan(_pos + (max < 0x80 ? 1 : 2)));
            if (max < 0x80)
            {
                _buf[_pos] = (byte)n;
                _pos += 1 + n;
            }
            else if (n < 0x80)
            {
                // Length fits in one byte: move data one byte back.
                _buf.AsSpan(_pos + 2, n).CopyTo(_buf.AsSpan(_pos + 1));
                _buf[_pos] = (byte)n;
                _pos += 1 + n;
            }
            else
            {
                _buf[_pos] = (byte)(n | 0x80);
                _buf[_pos + 1] = (byte)(n >> 7);
                _pos += 2 + n;
            }
            return;
        }
        var count = ByteReader.StrictUtf8.GetByteCount(s);
        WriteVarUInt((ulong)count);
        Ensure(count);
        ByteReader.StrictUtf8.GetBytes(s, _buf.AsSpan(_pos));
        _pos += count;
    }

    public void WriteLengthPrefixed(ReadOnlySpan<byte> b) { WriteVarUInt((ulong)b.Length); WriteBytes(b); }

    public void PatchUInt32(int offset, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(offset), v);

    public byte[] ToArray() => WrittenSpan.ToArray();
}

public static class ZigZag
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Encode(long v) => unchecked((ulong)((v << 1) ^ (v >> 63)));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Decode(ulong v) => unchecked((long)(v >> 1) ^ -(long)(v & 1));
}

/// <summary>Bounds-checked reader. Every overflow or truncation raises <see cref="LlogFormatException"/>.</summary>
public ref struct ByteReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    public ByteReader(ReadOnlySpan<byte> data) { _data = data; _pos = 0; }

    public int Position => _pos;
    public int Remaining => _data.Length - _pos;
    public bool End => _pos >= _data.Length;

    private void Need(int n)
    {
        if ((uint)n > (uint)(_data.Length - _pos)) throw new LlogFormatException($"Truncated data: need {n} bytes at offset {_pos}, have {_data.Length - _pos}.");
    }

    public byte ReadByte() { Need(1); return _data[_pos++]; }
    public ushort ReadUInt16() { Need(2); var v = BinaryPrimitives.ReadUInt16LittleEndian(_data[_pos..]); _pos += 2; return v; }
    public uint ReadUInt32() { Need(4); var v = BinaryPrimitives.ReadUInt32LittleEndian(_data[_pos..]); _pos += 4; return v; }
    public ulong ReadUInt64() { Need(8); var v = BinaryPrimitives.ReadUInt64LittleEndian(_data[_pos..]); _pos += 8; return v; }
    public long ReadInt64() => unchecked((long)ReadUInt64());

    public ReadOnlySpan<byte> ReadBytes(int n) { Need(n); var s = _data.Slice(_pos, n); _pos += n; return s; }

    public ulong ReadVarUInt()
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            Need(1);
            var b = _data[_pos++];
            if (shift == 63 && b > 1) throw new LlogFormatException("Varint overflows 64 bits.");
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift > 63) throw new LlogFormatException("Varint longer than 10 bytes.");
        }
    }

    public long ReadVarInt() => ZigZag.Decode(ReadVarUInt());

    /// <summary>Reads a varint used as a length/count and validates it against the remaining bytes (each element needs at least <paramref name="minBytesPerItem"/> bytes).</summary>
    public int ReadCount(int minBytesPerItem = 1, int max = int.MaxValue)
    {
        var v = ReadVarUInt();
        if (v > (ulong)max) throw new LlogFormatException($"Count {v} exceeds limit {max}.");
        if (minBytesPerItem > 0 && v > (ulong)(Remaining / minBytesPerItem)) throw new LlogFormatException($"Count {v} exceeds remaining data ({Remaining} bytes).");
        return (int)v;
    }

    public string ReadString()
    {
        var n = ReadCount(1);
        var span = ReadBytes(n);
        try { return StrictUtf8.GetString(span); }
        catch (DecoderFallbackException) { throw new LlogFormatException("Invalid UTF-8 in string."); }
    }

    public static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
}

/// <summary>CRC-32 (IEEE 802.3, reflected, poly 0xEDB88320) — identical to zlib's crc32 so independent readers can use standard libraries.</summary>
public static class Checksum
{
    public static uint Crc32(ReadOnlySpan<byte> data) => System.IO.Hashing.Crc32.HashToUInt32(data);
}

/// <summary>FNV-1a 64 bit; used by bloom filters. Deterministic across languages.</summary>
public static class Fnv1a
{
    public static ulong Hash(ReadOnlySpan<byte> data)
    {
        var h = 0xcbf29ce484222325UL;
        foreach (var b in data) { h ^= b; h *= 0x100000001b3UL; }
        return h;
    }

    public static ulong Hash(string s)
    {
        Span<byte> tmp = s.Length <= 128 ? stackalloc byte[Encoding.UTF8.GetMaxByteCount(s.Length)] : new byte[Encoding.UTF8.GetMaxByteCount(s.Length)];
        var n = Encoding.UTF8.GetBytes(s, tmp);
        return Hash(tmp[..n]);
    }
}
