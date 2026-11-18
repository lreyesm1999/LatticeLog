using System.Buffers.Binary;

namespace LatticeLog.Storage.Format;

/// <summary>Constants of the .llog v0.1 format. Normative text: specs/llog-format-v0.md.</summary>
public static class Llog
{
    public static ReadOnlySpan<byte> Magic => "LLOG\r\n\u001a\n"u8;
    public const ushort FormatMajor = 0;
    public const ushort FormatMinor = 2;
    /// <summary>Optional feature bit 0 (format 0.2): the footer carries a copy of the segment catalogs.</summary>
    public const uint OptionalFooterCatalog = 1;
    public const int FileHeaderSize = 40;
    public const int FrameHeaderSize = 24;
    public const int TrailerSize = 20;
    public const uint TrailerMagic = 0x544C464C; // "LFLT" little endian bytes 4C 46 4C 54 => "LFLT"

    /// <summary>Hard limit for a stored payload (protects readers from corrupted lengths).</summary>
    public const int MaxPayloadBytes = 64 * 1024 * 1024;
    /// <summary>Hard limit for a decoded payload (anti decompression bomb, P04-T12).</summary>
    public const int MaxDecodedBytes = 64 * 1024 * 1024;
    /// <summary>Maximum accepted expansion ratio for compressed payloads.</summary>
    public const int MaxExpansionRatio = 1100;
    public const int MaxRecordsPerBlock = 1_000_000;
    public const int MaxValueDepth = 32;
    public const int MaxAttributes = 4096;
    public const int MaxDictionaryEntries = 65535;

    /// <summary>Required feature bits understood by this reader. Unknown required bits => refuse (P04-T11).</summary>
    public const uint KnownRequiredFeatures = 0;
}

public enum BlockType : byte
{
    Schema = 1,
    Context = 2,
    Records = 3,
    Footer = 15,
}

public enum CodecId : byte
{
    /// <summary>Row-wise self-describing records (reference codec).</summary>
    Raw = 0,
    /// <summary>Lattice-S: block dictionary, deltas, schema-shaped attributes.</summary>
    Structural = 1,
    /// <summary>Lattice-C: Lattice-S + block trace table + sequential context inheritance.</summary>
    Contextual = 2,
}

public enum Compression : byte
{
    None = 0,
    /// <summary>Raw DEFLATE (RFC 1951), no zlib header.</summary>
    Deflate = 1,
}

/// <summary>Fixed 40-byte file header.</summary>
public readonly record struct FileHeader(ushort Major, ushort Minor, uint RequiredFeatures, uint OptionalFeatures, ulong SegmentId, long CreatedUnixNanos)
{
    public void Write(Span<byte> d)
    {
        Llog.Magic.CopyTo(d);
        BinaryPrimitives.WriteUInt16LittleEndian(d[8..], Major);
        BinaryPrimitives.WriteUInt16LittleEndian(d[10..], Minor);
        BinaryPrimitives.WriteUInt32LittleEndian(d[12..], RequiredFeatures);
        BinaryPrimitives.WriteUInt32LittleEndian(d[16..], OptionalFeatures);
        BinaryPrimitives.WriteUInt64LittleEndian(d[20..], SegmentId);
        BinaryPrimitives.WriteInt64LittleEndian(d[28..], CreatedUnixNanos);
        BinaryPrimitives.WriteUInt32LittleEndian(d[36..], Checksum.Crc32(d[..36]));
    }

    public static FileHeader Read(ReadOnlySpan<byte> d)
    {
        if (d.Length < Llog.FileHeaderSize) throw new LlogFormatException("File shorter than header.");
        if (!d[..8].SequenceEqual(Llog.Magic)) throw new LlogFormatException("Not a .llog file (bad magic).");
        var crc = BinaryPrimitives.ReadUInt32LittleEndian(d[36..]);
        if (crc != Checksum.Crc32(d[..36])) throw new LlogFormatException("File header checksum mismatch.");
        var h = new FileHeader(
            BinaryPrimitives.ReadUInt16LittleEndian(d[8..]),
            BinaryPrimitives.ReadUInt16LittleEndian(d[10..]),
            BinaryPrimitives.ReadUInt32LittleEndian(d[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(d[16..]),
            BinaryPrimitives.ReadUInt64LittleEndian(d[20..]),
            BinaryPrimitives.ReadInt64LittleEndian(d[28..]));
        if (h.Major != Llog.FormatMajor) throw new LlogFormatException($"Unsupported format major version {h.Major} (this reader supports {Llog.FormatMajor}).");
        if ((h.RequiredFeatures & ~Llog.KnownRequiredFeatures) != 0) throw new LlogFormatException($"File requires unknown features 0x{h.RequiredFeatures:x8}.");
        return h;
    }
}

/// <summary>Fixed 24-byte block frame header.</summary>
public readonly record struct FrameHeader(BlockType Type, CodecId Codec, Compression Compression, byte Reserved, uint PayloadLength, uint DecodedLength, uint RecordCount, uint PayloadCrc)
{
    public void Write(Span<byte> d)
    {
        d[0] = (byte)Type;
        d[1] = (byte)Codec;
        d[2] = (byte)Compression;
        d[3] = Reserved;
        BinaryPrimitives.WriteUInt32LittleEndian(d[4..], PayloadLength);
        BinaryPrimitives.WriteUInt32LittleEndian(d[8..], DecodedLength);
        BinaryPrimitives.WriteUInt32LittleEndian(d[12..], RecordCount);
        BinaryPrimitives.WriteUInt32LittleEndian(d[16..], PayloadCrc);
        BinaryPrimitives.WriteUInt32LittleEndian(d[20..], Checksum.Crc32(d[..20]));
    }

    /// <summary>Parses and validates a frame header. Returns false if its own CRC fails (torn/corrupt header).</summary>
    public static bool TryRead(ReadOnlySpan<byte> d, out FrameHeader h)
    {
        h = default;
        if (d.Length < Llog.FrameHeaderSize) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(d[20..]) != Checksum.Crc32(d[..20])) return false;
        h = new FrameHeader((BlockType)d[0], (CodecId)d[1], (Compression)d[2], d[3],
            BinaryPrimitives.ReadUInt32LittleEndian(d[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(d[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(d[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(d[16..]));
        return true;
    }

    /// <summary>Semantic validation independent of CRC (limits, known enums).</summary>
    public string? Validate()
    {
        if (Type is not (BlockType.Schema or BlockType.Context or BlockType.Records or BlockType.Footer)) return $"unknown block type {(byte)Type}";
        if (Codec is not (CodecId.Raw or CodecId.Structural or CodecId.Contextual)) return $"unknown codec {(byte)Codec}";
        if (Compression is not (Compression.None or Compression.Deflate)) return $"unknown compression {(byte)Compression}";
        if (Reserved != 0) return "reserved byte is not zero";
        if (PayloadLength > Llog.MaxPayloadBytes) return $"payload length {PayloadLength} exceeds limit";
        if (DecodedLength > Llog.MaxDecodedBytes) return $"decoded length {DecodedLength} exceeds limit";
        if (Compression == Compression.None && DecodedLength != PayloadLength) return "uncompressed block with decoded length != payload length";
        if (Compression != Compression.None && (ulong)DecodedLength > (ulong)PayloadLength * Llog.MaxExpansionRatio + 64) return "expansion ratio exceeds limit";
        if (RecordCount > Llog.MaxRecordsPerBlock) return "record count exceeds limit";
        return null;
    }

    public long TotalLength => Llog.FrameHeaderSize + (long)PayloadLength;
}

/// <summary>Fixed 20-byte trailer at the end of a sealed segment.</summary>
public readonly record struct Trailer(ulong FooterOffset, uint Flags)
{
    public void Write(Span<byte> d)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(d, FooterOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(d[8..], Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(d[12..], Llog.TrailerMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(d[16..], Checksum.Crc32(d[..16]));
    }

    public static bool TryRead(ReadOnlySpan<byte> d, out Trailer t)
    {
        t = default;
        if (d.Length < Llog.TrailerSize) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(d[12..]) != Llog.TrailerMagic) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(d[16..]) != Checksum.Crc32(d[..16])) return false;
        t = new Trailer(BinaryPrimitives.ReadUInt64LittleEndian(d), BinaryPrimitives.ReadUInt32LittleEndian(d[8..]));
        return true;
    }
}
