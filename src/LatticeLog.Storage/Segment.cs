using System.Buffers;
using Microsoft.Win32.SafeHandles;
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

/// <summary>
/// Append-only writer of one segment file. Not thread-safe, except <see cref="Fsync"/>, which may run on another thread
/// concurrently with <see cref="Append"/>: writes are positional (<see cref="RandomAccess"/>) on a raw OS handle, so a
/// background fsync never shares stream state with the writer (P14: fsync off the writer thread).
/// </summary>
public sealed class SegmentWriter : IDisposable
{
    private readonly SafeFileHandle _handle;
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
    /// <summary>True once the footer was written and the file closed.</summary>
    public bool IsSealed => Volatile.Read(ref _sealedFlag) != 0;
    private int _sealedFlag;
    /// <summary>True after an I/O error; the segment must be sealed (best effort) and replaced.</summary>
    public bool Faulted { get; private set; }

    /// <param name="publishAfterHeader">
    /// Create the file as <c>path.tmp</c> and rename it to <paramref name="path"/> once the file header is written, so a
    /// concurrent reader listing the directory never sees a segment shorter than its header. Used for live stores; a
    /// leftover <c>*.llog.tmp</c> after a crash is removed by recovery.
    /// </param>
    public SegmentWriter(string path, ulong segmentId, SegmentWriterOptions? options = null, bool publishAfterHeader = false)
    {
        _options = options ?? new SegmentWriterOptions();
        Path = path;
        SegmentId = segmentId;
        CreatedUnixNanos = _options.CreatedUnixNanos ?? UnixTime.NowUnixNanos();
        Span<byte> h = stackalloc byte[Llog.FileHeaderSize];
        new FileHeader(Llog.FormatMajor, Llog.FormatMinor, 0, Llog.OptionalFooterCatalog | Llog.OptionalBlockSummaries, segmentId, CreatedUnixNanos).Write(h);
        if (publishAfterHeader)
        {
            var staging = path + ".tmp";
            using (var tmp = File.OpenHandle(staging, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.None))
                RandomAccess.Write(tmp, h, 0);
            File.Move(staging, path, overwrite: false);
            _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Write, FileShare.Read, FileOptions.None);
        }
        else
        {
            _handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, FileOptions.None);
            RandomAccess.Write(_handle, h, 0);
        }
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
            if (_options.Compression == Compression.Deflate && type != BlockType.Footer && decodedLength > 64) // catalog blocks too (format-compatible with 0.1 readers)
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
            RandomAccess.Write(_handle, _frame.WrittenSpan, offset);
        }
        catch
        {
            // Never leave a partial frame followed by later frames: cut back to the last good block.
            Faulted = true;
            try { RandomAccess.SetLength(_handle, offset); } catch (IOException) { /* best effort: the original exception is rethrown and recovery truncates the torn tail */ }
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

    /// <summary>Writes are unbuffered positional writes: once <see cref="Append"/> returns, the OS has the bytes.</summary>
    public void FlushToOs() { }

    /// <summary>
    /// Makes everything written so far durable. Safe to call from another thread while <see cref="Append"/> runs (the
    /// fsync then covers at least the bytes written before it started). Throws <see cref="ObjectDisposedException"/>
    /// if the segment was closed meanwhile.
    /// </summary>
    public void Fsync() => RandomAccess.FlushToDisk(_handle);

    /// <summary>Writes footer index + trailer, fsyncs and closes. The segment becomes immutable.</summary>
    public SegmentIndex Seal()
    {
        if (_sealed) return _index.Current;
        var index = _index.Build();
        index.CatalogCopy = _tables.EncodeCatalogCopy(); // P04-R1: catalogs survive corruption of a catalog block
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
        RandomAccess.Write(_handle, _frame.WrittenSpan, footerOffset);
        Length += _frame.Length;
        RandomAccess.FlushToDisk(_handle);
        _sealed = true;
        Volatile.Write(ref _sealedFlag, 1);
        _handle.Dispose();
        return index;
    }

    public void Dispose()
    {
        if (!_sealed) _handle.Dispose();
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
    /// <summary>Complete catalog from the footer (format 0.2). When present, records decode even if a catalog block is corrupt.</summary>
    public SegmentTables? FooterCatalog { get; }
    public string? FooterCatalogError { get; }

    private SegmentReader(string path, FileStream fs, FileHeader header, long length, SegmentIndex? index, long footerOffset, string? footerError)
    {
        Path = path; _fs = fs; Header = header; Length = length; Index = index; FooterOffset = footerOffset; IsSealed = index is not null; FooterError = footerError;
        if (index?.CatalogCopy is { Length: > 0 } copy)
        {
            try { FooterCatalog = SegmentTables.DecodeCatalogCopy(copy); }
            catch (LlogFormatException ex) { FooterCatalogError = ex.Message; }
        }
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

    internal static byte[] Decompress(FrameHeader h, byte[] payload)
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
        if (FooterCatalogError is not null)
            report.Add(new ScanIssue(IssueKind.CorruptFooter, Path, FooterOffset, 0, "Footer catalog unreadable, using catalog blocks: " + FooterCatalogError));
        // In-file catalog blocks are always decoded: they are the catalog of unsealed segments and, for sealed ones,
        // they are cross-checked against the footer copy. Records use the footer copy when it exists.
        var scanned = new SegmentTables();
        var tables = FooterCatalog ?? scanned;
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
                            if (f.Header.Type == BlockType.Schema) scanned.DecodeSchemaBlock(f.Payload);
                            else scanned.DecodeContextBlock(f.Payload);
                            if (FooterCatalog is not null && !FooterCatalog.IsConsistentWith(scanned))
                                report.Add(new ScanIssue(IssueKind.FooterMismatch, Path, f.Offset, f.Header.TotalLength, $"{f.Header.Type} block differs from the footer catalog copy."));
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
        if (FooterCatalog is not null)
        {
            var (fh, fdata) = ReadFrameAt(_fs, entry.Offset, Length);
            if (fh.Type != BlockType.Records) throw new LlogFormatException("Index entry does not point to a records block.");
            var flist = new List<LogRecord>((int)fh.RecordCount);
            RecordCodec.Decode(fh.Codec, fdata, (int)fh.RecordCount, FooterCatalog, flist);
            return new DecodedBlock(entry, flist, Header.SegmentId);
        }
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
        if (FooterCatalog is not null) return FooterCatalog;
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
        var rebuilt = b.Build();
        rebuilt.CatalogCopy = tables.EncodeCatalogCopy();
        return rebuilt;
    }

    public void Dispose() => _fs.Dispose();
}
