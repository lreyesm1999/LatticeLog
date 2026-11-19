using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using LatticeLog.Storage.Codecs;
using LatticeLog.Storage.Format;
using LatticeLog.Storage.Json;

namespace LatticeLog.Ingest;

/// <summary>Constants of the ingestion protocol v1 (specs/ingest-protocol.md).</summary>
public static class IngestProtocol
{
    public const string Path = "/v1/ingest";
    public const string ContentType = "application/vnd.latticelog.batch";
    public const string AckHeader = "LatticeLog-Ack";
    public const int HeaderSize = 48;
    public const byte Version = 1;
    public const byte EncodingJsonl = 1;
    /// <summary>Self-contained mini segment: catalog copy + Lattice-S records (specs/ingest-protocol.md §1.3).</summary>
    public const byte EncodingLlog = 2;
    public const byte CompressionNone = 0;
    public const byte CompressionGzip = 1;
    public const byte FlagGapAcknowledged = 1;
    public const string IngestContextName = "ingest";
    public const string AttrProducer = "ingest.producer";
    public const string AttrFirstLocalSeq = "ingest.first_local_seq";
    private static ReadOnlySpan<byte> Magic => "LLIB"u8;

    /// <summary>
    /// Encodes a dense batch (records carry their local sequence numbers, first..last without gaps).
    /// </summary>
    public static byte[] EncodeBatch(string producerId, IReadOnlyList<LogRecord> records, bool gzip = true, bool gapAcknowledged = false, byte encoding = EncodingJsonl)
    {
        if (records.Count == 0) throw new ArgumentException("A batch needs at least one record.", nameof(records));
        var producer = ParseProducer(producerId);
        var first = records[0].Sequence;
        for (var i = 0; i < records.Count; i++)
            if (records[i].Sequence != first + (ulong)i) throw new ArgumentException("Batch sequences must be dense and ascending.", nameof(records));
        // Binary bodies cannot carry keys/names with lone surrogates exactly (the store would sanitize them and flag the
        // record); such batches travel as JSONL so the flag is set by the server exactly as for local writes.
        if (encoding == EncodingLlog && NeedsExactText(records)) encoding = EncodingJsonl;
        using var body = new MemoryStream();
        using (Stream target = gzip ? new GZipStream(body, CompressionLevel.Fastest, leaveOpen: true) : new NonClosingStream(body))
        {
            if (encoding == EncodingLlog) WriteLlogBody(target, records);
            else
            {
                using var w = new JsonlExchangeWriter(target, producerId, leaveOpen: true);
                foreach (var r in records) w.Write(r);
            }
        }
        var payload = body.GetBuffer().AsSpan(0, (int)body.Length);
        var result = new byte[HeaderSize + payload.Length];
        var h = result.AsSpan();
        Magic.CopyTo(h);
        h[4] = Version;
        h[5] = encoding;
        h[6] = gzip ? CompressionGzip : CompressionNone;
        h[7] = gapAcknowledged ? FlagGapAcknowledged : (byte)0;
        producer.CopyTo(h[8..24]);
        BinaryPrimitives.WriteUInt64LittleEndian(h[24..], first);
        BinaryPrimitives.WriteUInt64LittleEndian(h[32..], first + (ulong)records.Count - 1);
        BinaryPrimitives.WriteUInt32LittleEndian(h[40..], (uint)records.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(h[44..], Checksum.Crc32(payload));
        payload.CopyTo(h[HeaderSize..]);
        return result;
    }

    private static bool NeedsExactText(IReadOnlyList<LogRecord> records)
    {
        foreach (var r in records)
        {
            foreach (var a in r.Attributes) if (Value.HasLoneSurrogate(a.Key)) return true;
            if (r.EventName is { } en && Value.HasLoneSurrogate(en)) return true;
            if (r.OperationId is { } op && Value.HasLoneSurrogate(op)) return true;
            if (r.ParentOperationId is { } pop && Value.HasLoneSurrogate(pop)) return true;
            if (r.CorrelationId is { } cid && Value.HasLoneSurrogate(cid)) return true;
        }
        return false;
    }

    /// <summary>varint catalog length, catalog copy (schemas + contexts), u8 codec, Lattice-S records payload.</summary>
    private static void WriteLlogBody(Stream target, IReadOnlyList<LogRecord> records)
    {
        var tables = new SegmentTables();
        var (schemas, contexts) = tables.CollectNew(records, 0, records.Count);
        var scratch = new ByteWriter();
        tables.EncodeSchemaBlock(schemas, scratch);   // registers the entries in the tables
        tables.EncodeContextBlock(contexts, scratch);
        scratch.Release();
        var catalog = tables.EncodeCatalogCopy();
        var w = new ByteWriter(64 * 1024);
        w.WriteVarUInt((ulong)catalog.Length);
        w.WriteBytes(catalog);
        w.WriteByte((byte)CodecId.Structural);
        RecordCodec.Encode(CodecId.Structural, records, 0, records.Count, tables, w);
        target.Write(w.WrittenSpan);
        w.Release();
    }

    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
        protected override void Dispose(bool disposing) => base.Dispose(false); // leave the inner stream open
    }

    public static byte[] ParseProducer(string producerId)
    {
        byte[] bytes;
        try { bytes = Convert.FromHexString(producerId); }
        catch (FormatException) { throw new ArgumentException("producer id must be 32 hex characters (a store id).", nameof(producerId)); }
        if (bytes.Length != 16 || bytes.All(b => b == 0)) throw new ArgumentException("producer id must be 32 hex characters and not all zero.", nameof(producerId));
        return bytes;
    }
}

/// <summary>Server-side limits (specs/ingest-protocol.md §1.2).</summary>
public sealed record IngestLimits
{
    public int MaxRequestBytes { get; init; } = 8 * 1024 * 1024;
    public int MaxDecodedBytes { get; init; } = 64 * 1024 * 1024;
    public int MaxCompressionRatio { get; init; } = 100;
    public int MaxRecordsPerBatch { get; init; } = 50_000;
    public int MaxLineBytes { get; init; } = 1024 * 1024;
}

/// <summary>Protocol violation with the HTTP status and error code to answer.</summary>
public sealed class IngestException(int status, string error, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Error { get; } = error;
}

/// <summary>A validated, decoded batch.</summary>
public sealed record DecodedBatch(string ProducerId, ulong FirstLocalSeq, ulong LastLocalSeq, bool GapAcknowledged, List<LogRecord> Records);

public static class BatchDecoder
{
    /// <summary>Validates the envelope, checksum and limits, decompresses with limits and decodes the records.</summary>
    public static DecodedBatch Decode(ReadOnlySpan<byte> request, IngestLimits limits)
    {
        if (request.Length > limits.MaxRequestBytes) throw new IngestException(413, "too_large", $"request larger than {limits.MaxRequestBytes} bytes");
        if (request.Length < IngestProtocol.HeaderSize || !request[..4].SequenceEqual("LLIB"u8))
            throw new IngestException(400, "invalid_envelope", "missing LLIB envelope header");
        if (request[4] != IngestProtocol.Version) throw new IngestException(400, "invalid_envelope", $"unsupported envelope version {request[4]}");
        var encoding = request[5];
        if (encoding is not (IngestProtocol.EncodingJsonl or IngestProtocol.EncodingLlog))
            throw new IngestException(415, "unsupported_encoding", $"body encoding {encoding} not supported");
        var compression = request[6];
        if (compression is not (IngestProtocol.CompressionNone or IngestProtocol.CompressionGzip))
            throw new IngestException(415, "unsupported_encoding", $"compression {compression} not supported");
        var flags = request[7];
        var producerBytes = request[8..24];
        if (producerBytes.IndexOfAnyExcept((byte)0) < 0) throw new IngestException(400, "invalid_envelope", "producer id is zero");
        var producer = Convert.ToHexString(producerBytes).ToLowerInvariant();
        var first = BinaryPrimitives.ReadUInt64LittleEndian(request[24..]);
        var last = BinaryPrimitives.ReadUInt64LittleEndian(request[32..]);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(request[40..]);
        var crc = BinaryPrimitives.ReadUInt32LittleEndian(request[44..]);
        if (first == 0 || last < first || count == 0 || last - first + 1 != count)
            throw new IngestException(400, "invalid_envelope", "sequence range and record count do not match");
        if (count > limits.MaxRecordsPerBatch) throw new IngestException(413, "too_large", $"more than {limits.MaxRecordsPerBatch} records");
        var body = request[IngestProtocol.HeaderSize..];
        if (Checksum.Crc32(body) != crc) throw new IngestException(400, "checksum_mismatch", "body CRC-32 mismatch");

        var decoded = compression == IngestProtocol.CompressionGzip ? Inflate(body, limits) : body.ToArray();
        if (decoded.Length > limits.MaxDecodedBytes) throw new IngestException(413, "too_large", "decoded body too large");
        CheckLineLengths(decoded, limits.MaxLineBytes);

        var records = encoding == IngestProtocol.EncodingLlog
            ? DecodeLlogBody(decoded, (int)count)
            : DecodeJsonlBody(decoded, count, producer);
        if (records.Count != count) throw new IngestException(400, "invalid_batch", $"declared {count} records, body has {records.Count}");
        for (var i = 0; i < records.Count; i++)
            if (records[i].Sequence != first + (ulong)i) throw new IngestException(400, "invalid_batch", "record sequences do not match the envelope range");
        return new DecodedBatch(producer, first, last, (flags & IngestProtocol.FlagGapAcknowledged) != 0, records);
    }

    /// <summary>JSONL body: strict UTF-8, never more records than the envelope declares, same producer as the envelope.</summary>
    private static List<LogRecord> DecodeJsonlBody(byte[] decoded, uint count, string producer)
    {
        try
        {
            var reader = new JsonlExchangeReader();
            using var text = new StreamReader(new MemoryStream(decoded), new UTF8Encoding(false, true));
            var records = new List<LogRecord>();
            foreach (var r in reader.Read(text))
            {
                if (records.Count >= count) throw new IngestException(400, "invalid_batch", "more records than declared");
                records.Add(r);
            }
            if (reader.StoreId is { } sid && !string.Equals(sid, producer, StringComparison.OrdinalIgnoreCase))
                throw new IngestException(400, "invalid_batch", "body store_id differs from the envelope producer");
            return records;
        }
        catch (FormatException ex) { throw new IngestException(400, "decode_failed", ex.Message); }
        catch (DecoderFallbackException ex) { throw new IngestException(400, "decode_failed", "body is not valid UTF-8: " + ex.Message); }
    }

    /// <summary>Binary body: every length and count is bounded by the reader; any inconsistency is a 400, never a crash.</summary>
    private static List<LogRecord> DecodeLlogBody(byte[] body, int count)
    {
        try
        {
            var r = new ByteReader(body);
            var catalogLength = r.ReadVarUInt();
            if (catalogLength > (ulong)(body.Length - r.Position)) throw new LlogFormatException("catalog length exceeds the body");
            var tables = SegmentTables.DecodeCatalogCopy(body.AsSpan(r.Position, (int)catalogLength));
            var codecPos = r.Position + (int)catalogLength;
            if (codecPos >= body.Length) throw new LlogFormatException("missing codec");
            var codec = (CodecId)body[codecPos];
            if (codec is not (CodecId.Raw or CodecId.Structural or CodecId.Contextual)) throw new LlogFormatException($"unknown codec {(byte)codec}");
            var list = new List<LogRecord>(count);
            RecordCodec.Decode(codec, body.AsSpan(codecPos + 1), count, tables, list);
            return list;
        }
        catch (Exception ex) when (ex is LlogFormatException or OverflowException or ArgumentException or IndexOutOfRangeException or InvalidOperationException)
        {
            throw new IngestException(400, "decode_failed", "invalid binary body: " + ex.Message);
        }
    }

    /// <summary>Decompression bomb protection: absolute size and ratio limits, checked while inflating.</summary>
    private static byte[] Inflate(ReadOnlySpan<byte> body, IngestLimits limits)
    {
        var max = (int)Math.Min(limits.MaxDecodedBytes, (long)Math.Max(body.Length, 1) * limits.MaxCompressionRatio);
        using var gz = new GZipStream(new MemoryStream(body.ToArray()), CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        try
        {
            int n;
            while ((n = gz.Read(buffer)) > 0)
            {
                if (output.Length + n > max)
                    throw new IngestException(413, "too_large", $"decompressed body exceeds {max} bytes (limit or compression ratio {limits.MaxCompressionRatio})");
                output.Write(buffer, 0, n);
            }
        }
        catch (InvalidDataException ex) { throw new IngestException(400, "decode_failed", "invalid gzip body: " + ex.Message); }
        return output.ToArray();
    }

    private static void CheckLineLengths(ReadOnlySpan<byte> data, int maxLine)
    {
        while (data.Length > 0)
        {
            var nl = data.IndexOf((byte)'\n');
            var len = nl < 0 ? data.Length : nl;
            if (len > maxLine) throw new IngestException(413, "too_large", $"a body line exceeds {maxLine} bytes");
            data = nl < 0 ? default : data[(nl + 1)..];
        }
    }
}

/// <summary>Successful server answer (specs/ingest-protocol.md §3).</summary>
public sealed record IngestAck(string Status, string Producer, ulong CommittedThrough, long Stored, long SkippedDuplicates, AckLevel Ack,
    ulong ServerSeqFirst, ulong ServerSeqLast, ulong GapBefore)
{
    public JsonObject ToJson() => new()
    {
        ["status"] = Status,
        ["producer"] = Producer,
        ["committed_through"] = CommittedThrough.ToString(CultureInfo.InvariantCulture),
        ["stored"] = Stored,
        ["skipped_duplicates"] = SkippedDuplicates,
        ["ack"] = Ack.ToString(),
        ["server_seq_first"] = ServerSeqFirst.ToString(CultureInfo.InvariantCulture),
        ["server_seq_last"] = ServerSeqLast.ToString(CultureInfo.InvariantCulture),
        ["gap_before"] = GapBefore.ToString(CultureInfo.InvariantCulture),
    };

    public static IngestAck FromJson(JsonNode n) => new(
        (string)n["status"]!, (string)n["producer"]!, ulong.Parse((string)n["committed_through"]!, CultureInfo.InvariantCulture),
        (long)n["stored"]!, (long)n["skipped_duplicates"]!, Enum.Parse<AckLevel>((string)n["ack"]!),
        ulong.Parse((string)n["server_seq_first"]!, CultureInfo.InvariantCulture), ulong.Parse((string)n["server_seq_last"]!, CultureInfo.InvariantCulture),
        ulong.Parse((string)n["gap_before"]!, CultureInfo.InvariantCulture));
}
