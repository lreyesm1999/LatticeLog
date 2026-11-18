using LatticeLog.Storage.Codecs;
using LatticeLog.Storage.Format;

namespace LatticeLog.Storage;

/// <summary>
/// Incremental reader of a live store (forwarders, tail -f): returns records with sequence &gt; a cursor while keeping
/// the open segment, its byte position and its catalog tables between calls, so each poll decodes only new frames
/// instead of re-reading the active segment from the start. Segment transitions (rotation, compaction, retention)
/// consult the store catalog again. Anything unexpected falls back to a full, issue-reporting scan.
/// </summary>
public sealed class StoreTail : IDisposable
{
    private sealed class Followed : IDisposable
    {
        public required ulong Id;
        public required FileStream Stream;
        public readonly SegmentTables Tables = new();
        public long Position = Llog.FileHeaderSize;
        public bool Finished; // footer reached: the segment is sealed and fully read
        public long StallPosition = -1;
        public int StallCount;
        public void Dispose() => Stream.Dispose();
    }

    private readonly string _root;
    private Followed? _seg;
    private readonly Queue<LogRecord> _pending = new(); // decoded but not yet returned (call limit)
    private ulong _pendingAfter;

    /// <summary>Polls a frame may stay incomplete while the file grows past it before it is treated as corruption.</summary>
    public int StallLimit { get; init; } = 50;

    public StoreTail(string root) => _root = root;

    /// <summary>Statistics for tests and diagnostics.</summary>
    public long FramesRead { get; private set; }
    public long FullScans { get; private set; }

    /// <summary>
    /// Up to <paramref name="max"/> records with sequence &gt; <paramref name="after"/>, as a dense run.
    /// <paramref name="gap"/> is true when the run does not start at <paramref name="after"/> + 1.
    /// </summary>
    public List<LogRecord> ReadAfter(ulong after, int max, out bool gap)
    {
        var batch = new List<LogRecord>(Math.Min(max, 4096));
        gap = false;
        // The incremental position is only valid when the caller continues exactly where the last answer ended. A
        // retry of an unacknowledged batch, a rewind or a jump re-reads from the segments instead.
        if (_pendingAfter != after) Reset();
        if (!TryIncremental(after, max, batch))
        {
            // Fallback: full scan with resynchronization and reporting.
            Reset();
            FullScans++;
            batch = FullScan(after, max);
        }
        else if (batch.Count > 0 && batch[0].Sequence != after + 1)
        {
            // A gap makes the server accept missing records: never claim one on the incremental path alone.
            Reset();
            FullScans++;
            batch = FullScan(after, max);
        }
        if (batch.Count > 0)
        {
            gap = batch[0].Sequence != after + 1;
            for (var i = 1; i < batch.Count; i++)
                if (batch[i].Sequence != batch[i - 1].Sequence + 1) { batch.RemoveRange(i, batch.Count - i); break; }
            _pendingAfter = batch[^1].Sequence;
        }
        else _pendingAfter = after;
        return batch;
    }

    private void Reset()
    {
        _seg?.Dispose();
        _seg = null;
        _pending.Clear();
    }

    /// <summary>Returns false when the incremental path cannot answer correctly (caller falls back to a full scan).</summary>
    private bool TryIncremental(ulong after, int max, List<LogRecord> batch)
    {
        while (_pending.Count > 0 && batch.Count < max)
        {
            var r = _pending.Dequeue();
            if (r.Sequence > after) batch.Add(r);
        }
        if (batch.Count >= max) return true;
        for (var hops = 0; hops < 64; hops++)
        {
            if (_seg is null || _seg.Finished)
            {
                // Next segment after what this call already collected (a finished segment may end mid-batch).
                var from = batch.Count > 0 ? batch[^1].Sequence : after;
                if (!SelectSegment(from)) return false;
                if (_seg is null) return true; // nothing (more) to follow yet
            }
            var r = ReadNewFrames(_seg, after, max, batch);
            if (r is null) return false; // corruption or vanished file: full scan
            if (batch.Count >= max || !_seg.Finished) return true;
            // Segment sealed and fully consumed: move on to the next one in the same call.
        }
        return true;
    }

    /// <summary>Opens the segment that holds the first record after <paramref name="after"/> (or the active one).</summary>
    private bool SelectSegment(ulong after)
    {
        var previous = _seg;
        _seg = null;
        previous?.Dispose();
        StoreReader reader;
        try { reader = StoreReader.Open(_root); }
        catch (IOException) { return false; }
        ulong? chosen = null;
        foreach (var id in reader.SegmentIds) // ordered by first sequence, unsealed last
        {
            var idx = reader.Indexes.GetValueOrDefault(id);
            if (idx is not null && (!idx.HasRecords || idx.LastSequence <= after)) continue;
            if (previous is not null && id == previous.Id) continue; // just finished it
            chosen = id;
            break;
        }
        if (chosen is null) return true;
        try
        {
            var fs = new FileStream(reader.SegmentPath(chosen.Value), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            _seg = new Followed { Id = chosen.Value, Stream = fs };
        }
        catch (FileNotFoundException) { return false; }
        return true;
    }

    /// <summary>Reads complete frames from the current position. Null = cannot continue incrementally.</summary>
    private bool? ReadNewFrames(Followed s, ulong after, int max, List<LogRecord> batch)
    {
        var hb = new byte[Llog.FrameHeaderSize];
        while (batch.Count < max)
        {
            long length;
            try { length = s.Stream.Length; } catch (IOException) { return null; }
            if (length - s.Position < Llog.FrameHeaderSize) return true; // nothing new (or header still being written)
            s.Stream.Position = s.Position;
            s.Stream.ReadExactly(hb);
            if (!FrameHeader.TryRead(hb, out var h) || h.Validate() is not null)
                return Stall(s, length, s.Position + Llog.FrameHeaderSize);
            if (h.Type == BlockType.Footer) { s.Finished = true; return true; }
            if (s.Position + h.TotalLength > length) return true; // frame still being written
            var payload = new byte[h.PayloadLength];
            s.Stream.ReadExactly(payload);
            if (Checksum.Crc32(payload) != h.PayloadCrc) return Stall(s, length, s.Position + h.TotalLength);
            byte[] decoded;
            try { decoded = SegmentReader.Decompress(h, payload); }
            catch (Exception ex) when (ex is LlogFormatException or InvalidDataException) { return null; }
            try
            {
                switch (h.Type)
                {
                    case BlockType.Schema: s.Tables.DecodeSchemaBlock(decoded); break;
                    case BlockType.Context: s.Tables.DecodeContextBlock(decoded); break;
                    case BlockType.Records:
                        {
                            var list = new List<LogRecord>((int)h.RecordCount);
                            RecordCodec.Decode(h.Codec, decoded, (int)h.RecordCount, s.Tables, list);
                            foreach (var r in list)
                            {
                                if (r.Sequence <= after) continue;
                                if (batch.Count < max) batch.Add(r); else _pending.Enqueue(r);
                            }
                            break;
                        }
                }
            }
            catch (Exception ex) when (ex is LlogFormatException or OverflowException or ArgumentException) { return null; }
            FramesRead++;
            s.Position += h.TotalLength;
            s.StallCount = 0;
        }
        return true;
    }

    /// <summary>
    /// An invalid frame at the tail is normal for a moment (a write in progress may be visible in pieces). If it stays
    /// invalid while the file grows past it, it is corruption: give up incremental reading.
    /// </summary>
    private bool? Stall(Followed s, long length, long frameEnd)
    {
        if (s.StallPosition != s.Position) { s.StallPosition = s.Position; s.StallCount = 0; }
        s.StallCount++;
        return length > frameEnd && s.StallCount >= StallLimit ? null : true;
    }

    private List<LogRecord> FullScan(ulong after, int max)
    {
        var batch = new List<LogRecord>(Math.Min(max, 4096));
        StoreReader reader;
        try { reader = StoreReader.Open(_root); }
        catch (IOException) { return batch; }
        var report = new ScanReport();
        foreach (var block in reader.ReadBlocks(report,
                     segmentFilter: (_, idx) => idx is null || !idx.HasRecords || idx.LastSequence > after,
                     blockFilter: b => b.Type != BlockType.Records || b.LastSequence > after))
        {
            foreach (var r in block.Records)
            {
                if (r.Sequence <= after) continue;
                if (batch.Count > 0 && r.Sequence != batch[^1].Sequence + 1) return batch; // stop at a hole
                batch.Add(r);
                if (batch.Count >= max) return batch;
            }
        }
        return batch;
    }

    public void Dispose() => Reset();
}
