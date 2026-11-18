using System.Buffers;
using System.IO.Compression;
using LatticeLog.Storage.Codecs;
using LatticeLog.Storage.Format;

namespace LatticeLog.Storage;

public sealed record SegmentWriterOptions
{
    public CodecId Codec { get; init; } = CodecId.Structural;
    public Compression Compression { get; init; } = Compression.None;
    public int MaxRecordsPerBlock { get; init; } = 4096;
    public CompressionLevel DeflateLevel { get; init; } = CompressionLevel.Fastest;
    /// <summary>Fixed creation time (deterministic golden files). Null = now.</summary>
    public long? CreatedUnixNanos { get; init; }
}

/// <summary>Append-only writer of one segment file. Not thread-safe; owned by the store writer thread.</summary>
public sealed class SegmentWriter : IDisposable
{
    private readonly FileStream _fs;
    private readonly SegmentTables _tables = new();
    private readonly SegmentIndexBuilder _index = new();
    private readonly ByteWriter _payload = new(64 * 1024);
    private readonly ByteWriter _frame = new(64 * 1024);
    private readonly SegmentWriterOptions _options;
    private bool _sealed;

    public string Path { get; }
    public ulong SegmentId { get; }
    public long Length { get; private set; }
    public long RecordCount => _index.Current.RecordCount;
    public long CreatedUnixNanos { get; }
    public SegmentIndex CurrentIndex => _index.Current;
    /// <summary>True after an I/O error; the segment must be sealed (best effort) and replaced.</summary>
    public bool Faulted { get; private set; }

    public SegmentWriter(string path, ulong segmentId, SegmentWriterOptions? options = null)
    {
        _options = options ?? new SegmentWriterOptions();
        Path = path;
        SegmentId = segmentId;
        CreatedUnixNanos = _options.CreatedUnixNanos ?? UnixTime.NowUnixNanos();
        _fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize: 0, FileOptions.None);
        Span<byte> h = stackalloc byte[Llog.FileHeaderSize];
        new FileHeader(Llog.FormatMajor, Llog.FormatMinor, 0, 0, segmentId, CreatedUnixNanos).Write(h);
        _fs.Write(h);
        Length = Llog.FileHeaderSize;
    }

    /// <summary>Appends records (sequence numbers already assigned, ascending). Returns bytes written to the OS.</summary>
    public long Append(IReadOnlyList<LogRecord> records, int start, int count)
    {
        if (_sealed) throw new InvalidOperationException("Segment is sealed.");
        if (Faulted) throw new InvalidOperationException("Segment writer is faulted after an I/O error.");
        long written = 0;
        var end = start + count;
        for (var s = start; s < end; s += _options.MaxRecordsPerBlock)
        {
            var n = Math.Min(_options.MaxRecordsPerBlock, end - s);
            var (newSchemas, newContexts) = _tables.CollectNew(records, s, n);
            if (newSchemas.Count > 0)
            {
                _payload.Clear();
                _tables.EncodeSchemaBlock(newSchemas, _payload);
                written += WriteBlock(BlockType.Schema, CodecId.Raw, newSchemas.Count, null, 0);
            }
            if (newContexts.Count > 0)
            {
                _payload.Clear();
                _tables.EncodeContextBlock(newContexts, _payload);
                written += WriteBlock(BlockType.Context, CodecId.Raw, newContexts.Count, null, 0);
            }
            _payload.Clear();
            RecordCodec.Encode(_options.Codec, records, s, n, _tables, _payload);
            written += WriteBlock(BlockType.Records, _options.Codec, n, records, s);
        }
        return written;
    }

    private long WriteBlock(BlockType type, CodecId codec, int count, IReadOnlyList<LogRecord>? records, int start)
    {
        var decodedLength = _payload.Length;
        ReadOnlySpan<byte> payload = _payload.WrittenSpan;
        var compression = Compression.None;
        byte[]? rented = null;
        try
        {
            if (_options.Compression == Compression.Deflate && type == BlockType.Records && decodedLength > 64)
            {
                rented = ArrayPool<byte>.Shared.Rent(decodedLength + 1024);
                using var ms = new MemoryStream(rented, 0, rented.Length, writable: true);
                using (var ds = new DeflateStream(ms, _options.DeflateLevel, leaveOpen: true)) ds.Write(payload);
                var clen = (int)ms.Position;
                if (clen < decodedLength) { payload = rented.AsSpan(0, clen); compression = Compression.Deflate; }
            }
        }
        catch (NotSupportedException)
        {
            // Compressed output larger than the rented buffer: store uncompressed.
            payload = _payload.WrittenSpan;
            compression = Compression.None;
        }

        var header = new FrameHeader(type, codec, compression, 0, (uint)payload.Length, (uint)decodedLength, (uint)count, Checksum.Crc32(payload));
        _frame.Clear();
        header.Write(_frame.GetSpan(Llog.FrameHeaderSize));
        _frame.Advance(Llog.FrameHeaderSize);
        _frame.WriteBytes(payload);
        if (rented is not null) ArrayPool<byte>.Shared.Return(rented);

        var offset = Length;
        try
        {
            _fs.Write(_frame.WrittenSpan);
        }
        catch
        {
            // Never leave a partial frame followed by later frames: cut back to the last good block.
            Faulted = true;
            try { _fs.SetLength(offset); _fs.Position = offset; } catch (IOException) { }
            throw;
        }
        Length += _frame.Length;

        if (records is not null)
        {
            long min = long.MaxValue, max = long.MinValue;
            for (var i = start; i < start + count; i++)
            {
                var r = records[i];
                _index.AddRecord(r);
                if (r.TimestampUnixNanos < min) min = r.TimestampUnixNanos;
                if (r.TimestampUnixNanos > max) max = r.TimestampUnixNanos;
            }
            _index.AddBlock(new BlockIndexEntry(offset, _frame.Length, type, count, min, max, records[start].Sequence, records[start + count - 1].Sequence));
        }
        else
        {
            _index.AddBlock(new BlockIndexEntry(offset, _frame.Length, type, count, 0, 0, 0, 0));
        }
        return _frame.Length;
    }

    public void FlushToOs() => _fs.Flush(flushToDisk: false);

    public void Fsync() => _fs.Flush(flushToDisk: true);

    /// <summary>Writes footer index + trailer, fsyncs and closes. The segment becomes immutable.</summary>
    public SegmentIndex Seal()
    {
        if (_sealed) return _index.Current;
        var index = _index.Build();
        _payload.Clear();
        index.Encode(_payload);
        var footerOffset = Length;
        var header = new FrameHeader(BlockType.Footer, CodecId.Raw, Compression.None, 0, (uint)_payload.Length, (uint)_payload.Length, 0, Checksum.Crc32(_payload.WrittenSpan));
        _frame.Clear();
        header.Write(_frame.GetSpan(Llog.FrameHeaderSize));
        _frame.Advance(Llog.FrameHeaderSize);
        _frame.WriteBytes(_payload.WrittenSpan);
        new Trailer((ulong)footerOffset, 0).Write(_frame.GetSpan(Llog.TrailerSize));
        _frame.Advance(Llog.TrailerSize);
        _fs.Write(_frame.WrittenSpan);
        Length += _frame.Length;
        _fs.Flush(flushToDisk: true);
        _sealed = true;
        _fs.Dispose();
        return index;
    }

    public void Dispose()
    {
        if (!_sealed) _fs.Dispose();
        _payload.Release();
        _frame.Release();
    }
}

public enum IssueKind
{
    CorruptFrameHeader,
    CorruptPayload,
    UndecodableBlock,
    TruncatedTail,
    MissingFooter,
    CorruptFooter,
    FooterMismatch,
    SequenceRegression,
    UnsupportedFile,
    Io,
}

public sealed record ScanIssue(IssueKind Kind, string File, long Offset, long Length, string Message, long? LostRecords = null);

/// <summary>Everything a reader skipped or could not trust. Never silently empty when data was lost (P06-T12).</summary>
public sealed class ScanReport
{
    public List<ScanIssue> Issues { get; } = [];
    public long BlocksRead { get; set; }
    public long RecordsRead { get; set; }
    public long BytesRead { get; set; }
    public long SegmentsRead { get; set; }
    public long SegmentsSkippedByIndex { get; set; }
    public long BlocksSkippedByIndex { get; set; }
    public bool IsClean => Issues.Count == 0;
    public void Add(ScanIssue i) { lock (Issues) Issues.Add(i); }
}

public sealed class DecodedBlock(BlockIndexEntry entry, List<LogRecord> records, ulong segmentId)
{
    public BlockIndexEntry Entry { get; } = entry;
    public List<LogRecord> Records { get; } = records;
    public ulong SegmentId { get; } = segmentId;
}

/// <summary>Streaming reader of one segment (P06-T01). Never loads the whole file.</summary>
public sealed class SegmentReader : IDisposable
{
    private readonly FileStream _fs;
    private SegmentTables? _tables;
    private long _tablesLoadedUpTo = -1;

    public string Path { get; }
    public FileHeader Header { get; }
    public long Length { get; }
    public bool IsSealed { get; }
    public SegmentIndex? Index { get; }
    public long FooterOffset { get; }
    public string? FooterError { get; }

    private SegmentReader(string path, FileStream fs, FileHeader header, long length, SegmentIndex? index, long footerOffset, string? footerError)
    {
        Path = path; _fs = fs; Header = header; Length = length; Index = index; FooterOffset = footerOffset; IsSealed = index is not null; FooterError = footerError;
    }

    public static SegmentReader Open(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess);
        try
        {
            var len = fs.Length;
            Span<byte> hb = stackalloc byte[Llog.FileHeaderSize];
            if (len < Llog.FileHeaderSize) throw new LlogFormatException("File shorter than header.");
            fs.ReadExactly(hb);
            var header = FileHeader.Read(hb);
            SegmentIndex? index = null;
            long footerOffset = len;
            string? footerError = null;
            if (len >= Llog.FileHeaderSize + Llog.FrameHeaderSize + Llog.TrailerSize)
            {
                Span<byte> tb = stackalloc byte[Llog.TrailerSize];
                fs.Position = len - Llog.TrailerSize;
                fs.ReadExactly(tb);
                if (Trailer.TryRead(tb, out var t))
                {
                    try
                    {
                        if (t.FooterOffset < Llog.FileHeaderSize || (long)t.FooterOffset > len - Llog.TrailerSize - Llog.FrameHeaderSize)
                            throw new LlogFormatException("Footer offset out of range.");
                        var (fh, payload) = ReadFrameAt(fs, (long)t.FooterOffset, len);
                        if (fh.Type != BlockType.Footer) throw new LlogFormatException("Trailer does not point to a footer block.");
                        if ((long)t.FooterOffset + fh.TotalLength != len - Llog.TrailerSize) throw new LlogFormatException("Footer does not end at trailer.");
                        index = SegmentIndex.Decode(payload);
                        footerOffset = (long)t.FooterOffset;
                    }
                    catch (LlogFormatException ex) { footerError = ex.Message; index = null; footerOffset = len; }
                }
            }
            return new SegmentReader(path, fs, header, len, index, footerOffset, footerError);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    private static (FrameHeader, byte[]) ReadFrameAt(FileStream fs, long offset, long fileLength)
    {
        Span<byte> hb = stackalloc byte[Llog.FrameHeaderSize];
        fs.Position = offset;
        fs.ReadExactly(hb);
        if (!FrameHeader.TryRead(hb, out var h)) throw new LlogFormatException($"Corrupt frame header at {offset}.");
        if (h.Validate() is { } err) throw new LlogFormatException($"Invalid frame at {offset}: {err}.");
        if (offset + h.TotalLength > fileLength) throw new LlogFormatException($"Frame at {offset} extends past end of file.");
        var payload = new byte[h.PayloadLength];
        fs.ReadExactly(payload);
        if (Checksum.Crc32(payload) != h.PayloadCrc) throw new LlogFormatException($"Payload checksum mismatch at {offset}.");
        return (h, Decompress(h, payload));
    }

    private static byte[] Decompress(FrameHeader h, byte[] payload)
    {
        if (h.Compression == Compression.None) return payload;
        var output = new byte[h.DecodedLength];
        using var ms = new MemoryStream(payload, writable: false);
        using var ds = new DeflateStream(ms, CompressionMode.Decompress);
        var total = 0;
        while (total < output.Length)
        {
            var n = ds.Read(output, total, output.Length - total);
            if (n == 0) break;
            total += n;
        }
        if (total != output.Length) throw new LlogFormatException("Decompressed size smaller than declared.");
        Span<byte> one = stackalloc byte[1];
        if (ds.Read(one) != 0) throw new LlogFormatException("Decompressed size larger than declared.");
        return output;
    }

    private readonly record struct RawFrame(long Offset, FrameHeader Header, byte[]? Payload, string? Error);

    /// <summary>
    /// Sequential scan of every block up to the footer (or end of file for unsealed segments).
    /// Corrupt regions are reported and skipped; a resync searches for the next valid frame.
    /// </summary>
    private IEnumerable<RawFrame> ScanFrames(ScanReport report)
    {
        var end = IsSealed ? FooterOffset : Length;
        var pos = (long)Llog.FileHeaderSize;
        var hb = new byte[Llog.FrameHeaderSize];
        while (pos < end)
        {
            if (end - pos < Llog.FrameHeaderSize)
            {
                report.Add(new ScanIssue(IssueKind.TruncatedTail, Path, pos, end - pos, "Incomplete frame header at end of segment (torn write)."));
                yield break;
            }
            _fs.Position = pos;
            _fs.ReadExactly(hb);
            if (!FrameHeader.TryRead(hb, out var h) || h.Validate() is not null)
            {
                var next = Resync(pos + 1, end);
                if (next < 0)
                {
                    report.Add(new ScanIssue(!IsSealed ? IssueKind.TruncatedTail : IssueKind.CorruptFrameHeader, Path, pos, end - pos,
                        "Corrupt or incomplete frame; no valid frame follows."));
                    yield break;
                }
                report.Add(new ScanIssue(IssueKind.CorruptFrameHeader, Path, pos, next - pos, $"Corrupt frame header; resynchronized at offset {next}."));
                pos = next;
                continue;
            }
            if (pos + h.TotalLength > end)
            {
                report.Add(new ScanIssue(IssueKind.TruncatedTail, Path, pos, end - pos, "Frame extends past end of segment (torn write).", h.Type == BlockType.Records ? h.RecordCount : null));
                yield break;
            }
            if (h.Type == BlockType.Footer)
            {
                // A footer in the middle of an unsealed segment cannot be valid.
                report.Add(new ScanIssue(IssueKind.CorruptFooter, Path, pos, h.TotalLength, "Unexpected footer block before end of segment."));
                pos += h.TotalLength;
                continue;
            }
            var payload = new byte[h.PayloadLength];
            _fs.ReadExactly(payload);
            report.BytesRead += h.TotalLength;
            if (Checksum.Crc32(payload) != h.PayloadCrc)
            {
                report.Add(new ScanIssue(IssueKind.CorruptPayload, Path, pos, h.TotalLength, $"Payload checksum mismatch in {h.Type} block.", h.Type == BlockType.Records ? h.RecordCount : null));
                yield return new RawFrame(pos, h, null, "checksum");
                pos += h.TotalLength;
                continue;
            }
            byte[]? decoded = null;
            string? err = null;
            try { decoded = Decompress(h, payload); }
            catch (Exception ex) when (ex is LlogFormatException or InvalidDataException) { err = ex.Message; }
            if (err is not null)
            {
                report.Add(new ScanIssue(IssueKind.UndecodableBlock, Path, pos, h.TotalLength, "Decompression failed: " + err, h.Type == BlockType.Records ? h.RecordCount : null));
                yield return new RawFrame(pos, h, null, err);
            }
            else yield return new RawFrame(pos, h, decoded, null);
            pos += h.TotalLength;
        }
    }

    private long Resync(long from, long end)
    {
        // Chunked in-memory scan (one read per MiB instead of one syscall per byte).
        const int chunk = 1 << 20;
        var buf = new byte[chunk + Llog.FrameHeaderSize];
        for (var baseOff = from; baseOff + Llog.FrameHeaderSize <= end; baseOff += chunk)
        {
            var len = (int)Math.Min(buf.Length, end - baseOff);
            _fs.Position = baseOff;
            _fs.ReadExactly(buf, 0, len);
            for (var i = 0; i + Llog.FrameHeaderSize <= len && i < chunk; i++)
            {
                // Cheap pre-filter on type/codec/compression/reserved bytes before the CRC.
                if (buf[i] is < 1 or > 3 || buf[i + 1] > 2 || buf[i + 2] > 1 || buf[i + 3] != 0) continue;
                if (!FrameHeader.TryRead(buf.AsSpan(i, Llog.FrameHeaderSize), out var h) || h.Validate() is not null) continue;
                var p = baseOff + i;
                if (p + h.TotalLength > end) continue;
                var payload = new byte[h.PayloadLength];
                _fs.Position = p + Llog.FrameHeaderSize;
                _fs.ReadExactly(payload);
                if (Checksum.Crc32(payload) == h.PayloadCrc) return p;
            }
        }
        return -1;
    }

    /// <summary>Decodes all record blocks in file order. Corrupt blocks are reported (with lost record counts) and skipped.</summary>
    public IEnumerable<DecodedBlock> ReadBlocks(ScanReport report, Func<BlockIndexEntry, bool>? blockFilter = null)
    {
        var tables = new SegmentTables();
        var segId = Header.SegmentId;
        ulong lastSeq = 0;
        foreach (var f in ScanFrames(report))
        {
            if (f.Payload is null) continue;
            switch (f.Header.Type)
            {
                case BlockType.Schema:
                case BlockType.Context:
                    {
                        try
                        {
                            if (f.Header.Type == BlockType.Schema) tables.DecodeSchemaBlock(f.Payload);
                            else tables.DecodeContextBlock(f.Payload);
                        }
                        catch (LlogFormatException ex)
                        {
                            report.Add(new ScanIssue(IssueKind.UndecodableBlock, Path, f.Offset, f.Header.TotalLength, $"{f.Header.Type} block: {ex.Message}"));
                        }
                        break;
                    }
                case BlockType.Records:
                    {
                        var entry = new BlockIndexEntry(f.Offset, f.Header.TotalLength, BlockType.Records, (int)f.Header.RecordCount, long.MinValue, long.MaxValue, 0, 0);
                        if (blockFilter is not null && Index is not null)
                        {
                            var known = Index.Blocks.FirstOrDefault(b => b.Offset == f.Offset);
                            if (known is not null && !blockFilter(known)) { report.BlocksSkippedByIndex++; break; }
                        }
                        var list = new List<LogRecord>((int)f.Header.RecordCount);
                        try
                        {
                            RecordCodec.Decode(f.Header.Codec, f.Payload, (int)f.Header.RecordCount, tables, list);
                        }
                        catch (Exception ex) when (ex is LlogFormatException or OverflowException or ArgumentException)
                        {
                            report.Add(new ScanIssue(IssueKind.UndecodableBlock, Path, f.Offset, f.Header.TotalLength, "Records block: " + ex.Message, f.Header.RecordCount));
                            break;
                        }
                        if (list.Count > 0)
                        {
                            for (var i = 0; i < list.Count; i++)
                            {
                                if (list[i].Sequence <= lastSeq && lastSeq != 0)
                                    report.Add(new ScanIssue(IssueKind.SequenceRegression, Path, f.Offset, 0, $"Sequence {list[i].Sequence} after {lastSeq}."));
                                lastSeq = list[i].Sequence;
                            }
                            entry = entry with
                            {
                                MinTimestamp = list.Min(r => r.TimestampUnixNanos),
                                MaxTimestamp = list.Max(r => r.TimestampUnixNanos),
                                FirstSequence = list[0].Sequence,
                                LastSequence = list[^1].Sequence,
                            };
                        }
                        report.BlocksRead++;
                        report.RecordsRead += list.Count;
                        yield return new DecodedBlock(entry, list, segId);
                        break;
                    }
            }
        }
    }

    /// <summary>Random access: decode a single records block using the footer directory (loads schema/context blocks before it).</summary>
    public DecodedBlock ReadBlockAt(BlockIndexEntry entry)
    {
        if (Index is null) throw new InvalidOperationException("Random access requires a sealed segment.");
        if (_tables is null || entry.Offset < _tablesLoadedUpTo)
        {
            _tables = new SegmentTables();
            _tablesLoadedUpTo = Llog.FileHeaderSize;
        }
        foreach (var b in Index.Blocks)
        {
            if (b.Offset >= entry.Offset) break;
            if (b.Offset < _tablesLoadedUpTo) continue;
            if (b.Type is BlockType.Schema or BlockType.Context)
            {
                var (_, payload) = ReadFrameAt(_fs, b.Offset, Length);
                if (b.Type == BlockType.Schema) _tables.DecodeSchemaBlock(payload); else _tables.DecodeContextBlock(payload);
            }
            _tablesLoadedUpTo = b.Offset + 1;
        }
        var (h, data) = ReadFrameAt(_fs, entry.Offset, Length);
        if (h.Type != BlockType.Records) throw new LlogFormatException("Index entry does not point to a records block.");
        var list = new List<LogRecord>((int)h.RecordCount);
        RecordCodec.Decode(h.Codec, data, (int)h.RecordCount, _tables, list);
        return new DecodedBlock(entry, list, Header.SegmentId);
    }

    /// <summary>Schema and context catalogs of this segment (event contracts, P12-T13).</summary>
    public SegmentTables ReadCatalog(ScanReport report)
    {
        var tables = new SegmentTables();
        if (Index is not null)
        {
            foreach (var b in Index.Blocks)
            {
                if (b.Type is not (BlockType.Schema or BlockType.Context)) continue;
                try
                {
                    var (_, payload) = ReadFrameAt(_fs, b.Offset, Length);
                    if (b.Type == BlockType.Schema) tables.DecodeSchemaBlock(payload); else tables.DecodeContextBlock(payload);
                }
                catch (LlogFormatException ex) { report.Add(new ScanIssue(IssueKind.UndecodableBlock, Path, b.Offset, b.Length, ex.Message)); }
            }
            return tables;
        }
        foreach (var f in ScanFrames(report))
        {
            if (f.Payload is null) continue;
            try
            {
                if (f.Header.Type == BlockType.Schema) tables.DecodeSchemaBlock(f.Payload);
                else if (f.Header.Type == BlockType.Context) tables.DecodeContextBlock(f.Payload);
            }
            catch (LlogFormatException ex) { report.Add(new ScanIssue(IssueKind.UndecodableBlock, Path, f.Offset, f.Header.TotalLength, ex.Message)); }
        }
        return tables;
    }

    /// <summary>Byte offset of the end of the last fully valid block (used by recovery to cut a torn tail).</summary>
    public long FindValidPrefixEnd(ScanReport report)
    {
        // End of the last valid frame. Corrupt blocks in the middle are kept (and reported by verify);
        // only bytes after the last valid frame are considered a torn tail.
        var last = (long)Llog.FileHeaderSize;
        foreach (var f in ScanFrames(report))
        {
            if (f.Payload is not null) last = f.Offset + f.Header.TotalLength;
        }
        return last;
    }

    /// <summary>Rebuilds the footer index of an unsealed segment from its valid blocks (used by recovery).</summary>
    public SegmentIndex RebuildIndex(ScanReport report)
    {
        var b = new SegmentIndexBuilder();
        var tables = new SegmentTables();
        foreach (var f in ScanFrames(report))
        {
            if (f.Payload is null) continue;
            try
            {
                switch (f.Header.Type)
                {
                    case BlockType.Schema: tables.DecodeSchemaBlock(f.Payload); b.AddBlock(new BlockIndexEntry(f.Offset, f.Header.TotalLength, f.Header.Type, (int)f.Header.RecordCount, 0, 0, 0, 0)); break;
                    case BlockType.Context: tables.DecodeContextBlock(f.Payload); b.AddBlock(new BlockIndexEntry(f.Offset, f.Header.TotalLength, f.Header.Type, (int)f.Header.RecordCount, 0, 0, 0, 0)); break;
                    case BlockType.Records:
                        {
                            var list = new List<LogRecord>((int)f.Header.RecordCount);
                            RecordCodec.Decode(f.Header.Codec, f.Payload, (int)f.Header.RecordCount, tables, list);
                            if (list.Count == 0) break;
                            foreach (var r in list) b.AddRecord(r);
                            b.AddBlock(new BlockIndexEntry(f.Offset, f.Header.TotalLength, BlockType.Records, list.Count,
                                list.Min(r => r.TimestampUnixNanos), list.Max(r => r.TimestampUnixNanos), list[0].Sequence, list[^1].Sequence));
                            break;
                        }
                }
            }
            catch (Exception ex) when (ex is LlogFormatException or OverflowException or ArgumentException)
            {
                report.Add(new ScanIssue(IssueKind.UndecodableBlock, Path, f.Offset, f.Header.TotalLength, ex.Message, f.Header.Type == BlockType.Records ? f.Header.RecordCount : null));
            }
        }
        return b.Build();
    }

    public void Dispose() => _fs.Dispose();
}
