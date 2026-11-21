using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using LatticeLog.OpenTelemetry;
using LatticeLog.Storage;

namespace LatticeLog.Sources;

public sealed record InputFile(string Path, LogFormat Format);

public sealed record ImportProgress(string File, long BytesDone, long BytesTotal, long RecordsImported);

public sealed class ImportRunOptions
{
    /// <summary>service.name for records that carry none (structured JSON and text).</summary>
    public string? DefaultService { get; init; }
    /// <summary>Python local mode: skip records whose producer <c>seq</c> was already indexed from that file.</summary>
    public bool DedupeProducerSequence { get; init; }
    /// <summary>
    /// Also import a final line without '\n' when the file has not been written for this long (a finished log whose last
    /// line was never terminated). Null: only complete lines, ever (live sources, follow mode).
    /// </summary>
    public TimeSpan? UnterminatedTailAfterIdle { get; init; }
    public int BatchSize { get; init; } = 16_384;
    public Action<ImportProgress>? Progress { get; init; }
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(500);
    public StoreWriterOptions? Writer { get; init; }
}

public sealed record FileOutcome(string Path, string Format, long RecordsImported, long TotalRecords, long Offset, long Size, string Status);

public sealed class ImportRunResult
{
    public long Imported { get; set; }
    /// <summary>Lines the importer could not parse in this run, kept as text records (flag LossyImport).</summary>
    public long Unparsed { get; set; }
    public LossReport Loss { get; } = new();
    public List<FileOutcome> Files { get; } = [];
    public List<string> Warnings { get; } = [];
    public ulong StoreLastSequence { get; set; }
}

/// <summary>
/// Incremental, idempotent import of append-only log files into a LatticeLog store (specs/local-sources.md,
/// "Checkpoints"). Only complete new lines are read; a checkpoint per file remembers the byte offset, the file's
/// identity (hash of its first bytes, so renames by rotation are followed and truncation/replacement is detected) and
/// how many records of the current range are already in the store. No record is ever written twice:
/// <list type="bullet">
/// <item>Before a range is imported the checkpoint names it as pending together with the store's last sequence. After a
/// crash, the store's recovered last sequence tells exactly how many records of that range are durable; they are skipped.</item>
/// <item>Cancellation is honoured between batches and leaves a consistent checkpoint.</item>
/// </list>
/// Inputs are opened read-only with full sharing; they are never modified, moved or deleted.
/// </summary>
public static class IncrementalImporter
{
    public const int HeadBytes = 1024;

    public static ImportRunResult Run(string storeRoot, string checkpointPath, IReadOnlyList<InputFile> files, ImportRunOptions? options = null, CancellationToken ct = default)
    {
        var o = options ?? new ImportRunOptions();
        var result = new ImportRunResult();
        var cp = ImportCheckpoint.Load(checkpointPath);
        using var w = StoreWriter.Open(storeRoot, o.Writer);
        Reconcile(cp, w, result);
        cp.Save(checkpointPath);
        var now = UnixTime.NowUnixNanos();
        var heads = files.Select(f => (File: f, Info: Probe(f.Path))).Where(x => x.Info is not null).ToList();
        var claims = Claim(cp, heads.Select(h => (h.File.Path, h.Info!.Value.Size)).ToList());
        var bytesTotal = heads.Sum(h => Math.Max(0, h.Info!.Value.Size - (claims[h.File.Path]?.Offset ?? 0)));
        long bytesBefore = 0;
        var lastProgress = Stopwatch.StartNew();
        foreach (var (file, info) in heads)
        {
            ct.ThrowIfCancellationRequested();
            var (_, mtime) = info!.Value; // the size is read again from the open file below
            var st = claims[file.Path];
            if (st is null)
            {
                st = new FileState { Key = "f" + (++cp.NextFileId).ToString(CultureInfo.InvariantCulture), Path = file.Path, Format = file.Format.ToString() };
                cp.Files.Add(st);
            }
            else if (!PathPolicy.SamePath(st.Path, file.Path))
            {
                result.Warnings.Add($"{file.Path}: continues {st.Path} (renamed by rotation); only new lines are imported");
                st.Path = file.Path;
            }
            using var fs = Inputs.OpenShared(file.Path);
            var size = fs.Length;
            if (st.HeadLen < HeadBytes && size > st.HeadLen) (st.HeadLen, st.HeadHash) = Head(fs, Math.Min(size, HeadBytes));
            var format = Enum.Parse<LogFormat>(st.Format);
            long end;
            if (format == LogFormat.OtlpJsonDocument)
            {
                // A single JSON document cannot be read incrementally: import it once, report later changes.
                if (st.Offset > 0)
                {
                    if (size != st.Offset) result.Warnings.Add($"{file.Path}: OTLP JSON document changed after it was imported; not re-imported (it would duplicate records)");
                    result.Files.Add(new(file.Path, st.Format, 0, st.Records, st.Offset, size, "unchanged"));
                    continue;
                }
                end = size;
            }
            else
            {
                end = LastLineEnd(fs, st.Offset, size);
                if (end < size && o.UnterminatedTailAfterIdle is { } idle && DateTime.UtcNow - mtime > idle) end = size;
            }
            if (end <= st.Offset)
            {
                result.Files.Add(new(file.Path, st.Format, 0, st.Records, st.Offset, size, end < size ? "waiting for the line being written" : "up to date"));
                continue;
            }
            cp.Pending = st.Key;
            cp.StoreLastSequence = w.LastSequence;
            cp.Save(checkpointPath);

            var newDefs = new List<string>();
            var range = new RangeStream(fs, st.Offset, end);
            var loss = new LossReport();
            IEnumerable<LogRecord> records = RecordParsers.Parse(format, range, Path.GetFileName(file.Path), o.DefaultService, st.Defs, newDefs, loss, now);
            if (o.DedupeProducerSequence)
            {
                var floor = st.LastProducerSeq;
                records = records.Where(r => r.Sequence == 0 || r.Sequence > floor);
            }
            long produced = 0, written = 0, unparsed = 0;
            var alreadyStored = st.Skip;
            var startOffset = st.Offset;
            ulong maxSeq = st.LastProducerSeq;
            var batch = new List<LogRecord>(o.BatchSize);
            var cancelled = false;
            void Commit()
            {
                if (batch.Count == 0) return;
                w.WriteBatch(batch);
                written += batch.Count;
                st.Skip += batch.Count;
                st.Records += batch.Count;
                batch.Clear();
            }
            foreach (var r in records)
            {
                produced++;
                if (produced <= alreadyStored) continue; // already in the store (crash or cancellation mid-range)
                if (r.Sequence > maxSeq) maxSeq = r.Sequence;
                if (r.Flags.HasFlag(RecordFlags.LossyImport) && r.TryGetAttribute(RecordParsers.AttrImportError, out _)) unparsed++;
                r.Sequence = 0;
                batch.Add(r);
                if (batch.Count >= o.BatchSize)
                {
                    Commit();
                    if (o.Progress is not null && lastProgress.Elapsed >= o.ProgressInterval)
                    {
                        o.Progress(new(file.Path, bytesBefore + range.Consumed, bytesTotal, result.Imported + written));
                        lastProgress.Restart();
                    }
                    if (ct.IsCancellationRequested) { cancelled = true; break; }
                }
            }
            if (!cancelled) Commit();
            w.Flush(AckLevel.FsyncConfirmed);
            result.Imported += written;
            result.Unparsed += unparsed;
            st.Unparsed += unparsed;
            foreach (var (k, v) in loss.Items) result.Loss.Add(k, v);
            cp.StoreLastSequence = w.LastSequence;
            cp.Pending = null;
            if (cancelled)
            {
                cp.Save(checkpointPath); // st.Skip says how many records of this range are stored
                ct.ThrowIfCancellationRequested();
            }
            st.Offset = end;
            st.Skip = 0;
            st.LastProducerSeq = maxSeq;
            st.Defs.AddRange(newDefs);
            st.Size = size;
            (st.TailLen, st.TailHash) = HashRange(fs, Math.Max(0, end - HeadBytes), end);
            cp.Save(checkpointPath);
            bytesBefore += end - startOffset;
            result.Files.Add(new(file.Path, st.Format, written, st.Records, st.Offset, size, "imported"));
        }
        o.Progress?.Invoke(new("", bytesTotal, bytesTotal, result.Imported));
        result.StoreLastSequence = w.LastSequence;
        return result;
    }

    /// <summary>After a crash the store may hold records of the pending range that the checkpoint does not count yet.</summary>
    private static void Reconcile(ImportCheckpoint cp, StoreWriter w, ImportRunResult result)
    {
        var last = w.LastSequence;
        if (last > cp.StoreLastSequence)
        {
            var extra = (long)(last - cp.StoreLastSequence);
            if (cp.Pending is { } key && cp.Files.FirstOrDefault(f => f.Key == key) is { } st)
            {
                st.Skip += extra;
                st.Records += extra;
                result.Warnings.Add($"resumed after an interruption: {extra} record(s) of {st.Path} were already stored and are skipped");
            }
            else result.Warnings.Add($"the store has {extra} record(s) the checkpoint does not account for (written by another tool?)");
        }
        else if (last < cp.StoreLastSequence)
            result.Warnings.Add($"the store lost {cp.StoreLastSequence - last} record(s) since the last checkpoint (damaged segment recovered by 'repair'?)");
        cp.StoreLastSequence = last;
        cp.Pending = null;
    }

    private static (long Size, DateTime MtimeUtc)? Probe(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? (fi.Length, fi.LastWriteTimeUtc) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Assigns each input file to the checkpoint entry whose content it continues: same path and same head bytes with
    /// at least as many bytes as already read; otherwise any entry with the same head (the file was renamed by
    /// rotation). A shorter file or a different head starts a new entry (truncated, replaced or a new generation).
    /// </summary>
    private static Dictionary<string, FileState?> Claim(ImportCheckpoint cp, List<(string Path, long Size)> inputs)
    {
        var claims = new Dictionary<string, FileState?>(StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        // Same first bytes AND same bytes just before the checkpoint offset: the file still holds what was imported.
        bool Continues(FileState st, string path, long size)
        {
            if (size < st.Offset || size < st.HeadLen) return false;
            try
            {
                using var fs = Inputs.OpenShared(path);
                if (Head(fs, st.HeadLen).Hash != st.HeadHash) return false;
                return st.Offset == 0 || HashRange(fs, st.Offset - st.TailLen, st.Offset).Hash == st.TailHash;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        foreach (var (path, size) in inputs)
        {
            var same = cp.Files.Where(f => !taken.Contains(f.Key) && PathPolicy.SamePath(f.Path, path)).FirstOrDefault(f => Continues(f, path, size));
            if (same is not null) { claims[path] = same; taken.Add(same.Key); }
        }
        foreach (var (path, size) in inputs)
        {
            if (claims.ContainsKey(path)) continue;
            var moved = cp.Files.Where(f => !taken.Contains(f.Key) && f.Offset > 0).FirstOrDefault(f => Continues(f, path, size));
            claims[path] = moved;
            if (moved is not null) taken.Add(moved.Key);
        }
        return claims;
    }

    private static (int Len, string Hash) Head(FileStream fs, long len) => HashRange(fs, 0, len);

    private static (int Len, string Hash) HashRange(FileStream fs, long from, long to)
    {
        var buf = new byte[Math.Max(0, to - from)];
        fs.Position = from;
        var n = fs.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
        fs.Position = 0;
        return (n, Convert.ToHexString(SHA256.HashData(buf.AsSpan(0, n))));
    }

    /// <summary>Position just after the last '\n' in [from, size), or <paramref name="from"/> when there is none.</summary>
    internal static long LastLineEnd(FileStream fs, long from, long size)
    {
        var buf = new byte[64 * 1024];
        var pos = size;
        while (pos > from)
        {
            var len = (int)Math.Min(buf.Length, pos - from);
            pos -= len;
            fs.Position = pos;
            fs.ReadExactly(buf, 0, len);
            var i = buf.AsSpan(0, len).LastIndexOf((byte)'\n');
            if (i >= 0) { fs.Position = 0; return pos + i + 1; }
        }
        fs.Position = 0;
        return from;
    }
}

internal sealed class FileState
{
    public required string Key { get; init; }
    public required string Path { get; set; }
    public required string Format { get; init; }
    public int HeadLen { get; set; }
    public string HeadHash { get; set; } = Convert.ToHexString(SHA256.HashData([]));
    public long Offset { get; set; }
    /// <summary>Records of the range starting at <see cref="Offset"/> that are already stored.</summary>
    public long Skip { get; set; }
    public long Records { get; set; }
    public long Unparsed { get; set; }
    public long Size { get; set; }
    public ulong LastProducerSeq { get; set; }
    public int TailLen { get; set; }
    public string TailHash { get; set; } = "";
    /// <summary>latticelog.jsonl definition lines (header, contexts, schemas) before <see cref="Offset"/>.</summary>
    public List<string> Defs { get; } = [];
}

internal sealed class ImportCheckpoint
{
    public ulong StoreLastSequence { get; set; }
    public string? Pending { get; set; }
    public long NextFileId { get; set; }
    public List<FileState> Files { get; } = [];

    public static ImportCheckpoint Load(string path)
    {
        var cp = new ImportCheckpoint();
        if (!File.Exists(path)) return cp;
        var n = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException($"{path}: not a JSON object.");
        if ((string?)n["format"] != "latticelog.checkpoint" || (int?)n["version"] != 1) throw new InvalidDataException($"{path}: unknown checkpoint format.");
        cp.StoreLastSequence = (ulong)n["store_last_sequence"]!;
        cp.Pending = (string?)n["pending"];
        cp.NextFileId = (long?)n["next_file_id"] ?? 0;
        foreach (var f in n["files"]!.AsArray())
        {
            var st = new FileState
            {
                Key = (string)f!["key"]!,
                Path = (string)f["path"]!,
                Format = (string)f["format"]!,
                HeadLen = (int)f["head_len"]!,
                HeadHash = (string)f["head_sha256"]!,
                Offset = (long)f["offset"]!,
                Skip = (long)f["skip"]!,
                Records = (long)f["records"]!,
                Unparsed = (long?)f["unparsed"] ?? 0,
                Size = (long?)f["size"] ?? 0,
                LastProducerSeq = (ulong?)f["last_producer_seq"] ?? 0,
                TailLen = (int?)f["tail_len"] ?? 0,
                TailHash = (string?)f["tail_sha256"] ?? "",
            };
            foreach (var d in f["defs"] as JsonArray ?? []) st.Defs.Add((string)d!);
            cp.Files.Add(st);
        }
        return cp;
    }

    public void Save(string path)
    {
        var files = new JsonArray();
        foreach (var f in Files)
        {
            files.Add((JsonNode)new JsonObject
            {
                ["key"] = f.Key,
                ["path"] = f.Path,
                ["format"] = f.Format,
                ["head_len"] = f.HeadLen,
                ["head_sha256"] = f.HeadHash,
                ["offset"] = f.Offset,
                ["skip"] = f.Skip,
                ["records"] = f.Records,
                ["unparsed"] = f.Unparsed,
                ["size"] = f.Size,
                ["last_producer_seq"] = f.LastProducerSeq,
                ["tail_len"] = f.TailLen,
                ["tail_sha256"] = f.TailHash,
                ["defs"] = new JsonArray(f.Defs.Select(d => (JsonNode?)JsonValue.Create(d)).ToArray()),
            });
        }
        var node = new JsonObject
        {
            ["format"] = "latticelog.checkpoint",
            ["version"] = 1,
            ["store_last_sequence"] = StoreLastSequence,
            ["pending"] = Pending,
            ["next_file_id"] = NextFileId,
            ["files"] = files,
        };
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
